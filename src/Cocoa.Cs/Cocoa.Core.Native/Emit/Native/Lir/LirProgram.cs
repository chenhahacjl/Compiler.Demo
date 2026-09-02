using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>数据段项语义：Int32 / 指针（平台宽 4/8）/ UTF-16 字符串 / 原始字节 / vtable 记录（M4）。</summary>
    internal enum LirDataKind
    {
        Int32,
        Pointer,
        Utf16,
        Bytes,
        VTable,
    }

    /// <summary>数据段项（运行时数据与字符串字面量统一建模）。</summary>
    internal sealed class LirDataItem
    {
        public LirDataItem(string key, LirDataKind kind, int intValue, string? text, byte[]? bytes)
        {
            Key = key;
            Kind = kind;
            IntValue = intValue;
            Text = text;
            Bytes = bytes;
        }

        public string Key { get; }
        public LirDataKind Kind { get; }
        public int IntValue { get; }
        public string? Text { get; }
        public byte[]? Bytes { get; }

        /// <summary>vtable 记录：类型 id（M4；伪记录 -1）。</summary>
        public int TypeId { get; }

        /// <summary>vtable 记录：类型全名字符串的数据 key（名字指针槽重定位目标）。</summary>
        public string? NameKey { get; }

        /// <summary>vtable 记录：函数名槽数组（用户函数 mangle 名或运行时函数名）。</summary>
        public System.Collections.Generic.IReadOnlyList<string>? Slots { get; }

        public LirDataItem(string key, LirDataKind kind, int intValue, string? text, byte[]? bytes,
            int typeId = -1, string? nameKey = null, System.Collections.Generic.IReadOnlyList<string>? slots = null)
        {
            Key = key;
            Kind = kind;
            IntValue = intValue;
            Text = text;
            Bytes = bytes;
            TypeId = typeId;
            NameKey = nameKey;
            Slots = slots;
        }

        public static LirDataItem Int32(string key, int value) => new LirDataItem(key, LirDataKind.Int32, value, null, null);
        public static LirDataItem Pointer(string key) => new LirDataItem(key, LirDataKind.Pointer, 0, null, null);
        public static LirDataItem Utf16(string key, string text) => new LirDataItem(key, LirDataKind.Utf16, 0, text, null);
        public static LirDataItem ByteArray(string key, byte[] bytes) => new LirDataItem(key, LirDataKind.Bytes, 0, null, bytes);

        /// <summary>
        /// vtable 记录（M4，即 System.Type 对象）：[0] typeId:int [4] pad [8] 名字指针（数据重定位）
        /// [8+ps·(i+1)] 槽 i 函数绝对地址（代码重定位）。typeId &lt; 0 = 基元/Type 伪记录
        /// （[0] 为自引用指针，使 ObjectToString 等对 Type 值同样成立）。
        /// </summary>
        public static LirDataItem VTable(string key, int typeId, string nameKey, System.Collections.Generic.IReadOnlyList<string> slots)
            => new LirDataItem(key, LirDataKind.VTable, 0, null, null, typeId, nameKey, slots);
    }

    /// <summary>函数参数：仅携带序号（调用约定由后端决定）。</summary>
    internal sealed class LirParameter
    {
        public LirParameter(string? name, int ordinal)
        {
            Name = name;
            Ordinal = ordinal;
        }

        public string? Name { get; }
        public int Ordinal { get; }
    }

    /// <summary>IR 函数：指令列表 + 虚拟寄存器登记表。生成期写线性 Instructions；消费期经 Blocks 显式 CFG。</summary>
    internal sealed class LirFunction
    {
        private List<LirBasicBlock>? _blocks;
        private readonly List<LirVirtualRegister> _registers = new();

        public LirFunction(string name, IReadOnlyList<LirParameter> parameters)
        {
            Name = name;
            Parameters = parameters;
            Instructions = new List<LirInstruction>();
        }

        public string Name { get; }
        public IReadOnlyList<LirParameter> Parameters { get; }
        public List<LirInstruction> Instructions { get; }
        public int ReturnSize { get; set; }
        public int EndLabelId { get; set; }

        /// <summary>函数内登记的全部虚拟寄存器（登记顺序 = 槽位分配顺序，与线性 LIR 一致）。</summary>
        public IReadOnlyList<LirVirtualRegister> Registers => _registers;

        /// <summary>显式基本块（Phase 2 显式 CFG），第一次访问时由线性 Instructions 建块缓存。</summary>
        public IReadOnlyList<LirBasicBlock> Blocks => _blocks ??= BuildBlocks();

        /// <summary>登记虚拟寄存器（幂等，槽位按首次登记顺序）。</summary>
        public void Register(LirVirtualRegister register)
        {
            if (!_registers.Contains(register))
            {
                _registers.Add(register);
            }
        }

        public int RegisterSize(LirVirtualRegister register) => register.Type.Size();

        /// <summary>
        /// 可选优化 pass（Phase 2 B3，默认不启用）：显式 CFG 上的保守常量传播。
        /// 仅折叠「块内相邻」的 `Const dst, c; Mov x, dst` → `Const x, c`（dst 不再被后续读取），
        /// 不触碰副作用指令（Call/Store/Load/Lea*/InitParam 等），保证行为等价。
        /// </summary>
        public void Optimize()
        {
            foreach (var block in Blocks)
            {
                var instructions = block.Instructions;
                for (var i = 0; i < instructions.Count - 1; i++)
                {
                    var a = instructions[i];
                    if (a.OpCode != LirOpCode.Const || a.Dst == null || a.Dst.Type != LirType.I32)
                    {
                        continue;
                    }

                    // 仅当 a 的 dst 只在紧随的 Mov 中出现一次、且其后无任何读取 → 折叠
                    if (i + 1 < instructions.Count &&
                        instructions[i + 1].OpCode == LirOpCode.Mov &&
                        instructions[i + 1].A.Kind == LirOperandKind.Register &&
                        ReferenceEquals(instructions[i + 1].A.Register, a.Dst) &&
                        instructions[i + 1].Dst != null &&
                        !RegisterReadLater(instructions, i + 2, a.Dst))
                    {
                        var mov = instructions[i + 1];
                        var folded = new LirInstruction(
                            LirOpCode.Const,
                            mov.Dst,
                            LirOperand.Constant(a.A.Imm),
                            LirOperand.None,
                            0,
                            0);
                        instructions[i] = folded;
                        instructions.RemoveAt(i + 1);
                    }
                }
            }
        }

        private static bool RegisterReadLater(List<LirInstruction> instructions, int startIndex, LirVirtualRegister register)
        {
            for (var i = startIndex; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                if (instruction.Dst != null && ReferenceEquals(instruction.Dst, register))
                {
                    return true;
                }

                if (instruction.A.Kind == LirOperandKind.Register && ReferenceEquals(instruction.A.Register, register))
                {
                    return true;
                }

                if (instruction.B.Kind == LirOperandKind.Register && ReferenceEquals(instruction.B.Register, register))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把线性指令流切成基本块：Label 开启新块（顺序相邻的纯标签折叠为本块别名）；
        /// Jmp/Jcc/Ret 收束为本块 terminator（指令本身移出 Instructions，
        /// 对应 label id 作为目标；Ret 的 EndLabelId 作为本块别名，令跳转 EndLabelId 与 Ret 同址）。
        /// </summary>
        private List<LirBasicBlock> BuildBlocks()
        {
            var blocks = new List<LirBasicBlock>();
            var current = new LirBasicBlock();

            foreach (var instruction in Instructions)
            {
                switch (instruction.OpCode)
                {
                    case LirOpCode.Label:
                        if (current.Instructions.Count > 0 || current.Terminator != null || current.Labels.Count > 0)
                        {
                            blocks.Add(current);
                            current = new LirBasicBlock();
                        }

                        current.AddLabel((int)instruction.A.Imm);
                        break;

                    case LirOpCode.Jmp:
                        current.Terminator = LirTerminator.Jump((int)instruction.A.Imm);
                        blocks.Add(current);
                        current = new LirBasicBlock();
                        break;

                    case LirOpCode.Jcc:
                        current.Terminator = LirTerminator.CondJump((LirCond)instruction.A.Imm, (int)instruction.B.Imm);
                        blocks.Add(current);
                        current = new LirBasicBlock();
                        break;

                    case LirOpCode.Ret:
                        // EndLabelId 与原语义同址：Ret 前若已有指令（fall-through 代码）
                        // 先原样收束该块，Ret 独立落入空 epilog 块，块首标 EndLabelId =
                        // 原 Ret 指令位置（Jmp EndLabelId 与 Ret 汇聚，不重执行中间代码）。
                        if (current.Instructions.Count > 0)
                        {
                            blocks.Add(current);
                            current = new LirBasicBlock();
                        }

                        current.AddLabel((int)instruction.A.Imm);
                        current.Terminator = LirTerminator.Return((int)instruction.A.Imm);
                        blocks.Add(current);
                        current = new LirBasicBlock();
                        break;

                    default:
                        current.Instructions.Add(instruction);
                        break;
                }
            }

            if (current.Instructions.Count > 0 || current.Terminator != null || current.Labels.Count > 0)
            {
                blocks.Add(current);
            }
            else if (blocks.Count == 0)
            {
                blocks.Add(current);
            }

            return blocks;
        }
    }

    /// <summary>导入规格：DLL 名 + 函数名（DLL 导出名，已含 entry 别名）+ x86 调用约定（cdecl 调用方清理）。x64 约定统一，Cdecl 忽略。</summary>
    internal readonly record struct LirImport(string DllName, string Name, bool Cdecl)
    {
        public override string ToString() => DllName + "!" + Name;
    }

    /// <summary>整个 IR 程序：函数表 + 数据段 + 运行时配置 + 入口函数名。</summary>
    internal sealed class LirProgram
    {
        private readonly Dictionary<string, int> _dataIndex = new();

        public LirProgram(string entryFunctionName)
        {
            EntryFunctionName = entryFunctionName;
            Functions = new List<LirFunction>();
            Data = new Dictionary<string, LirDataItem>();
            DataItems = new List<LirDataItem>();
            Imports = new List<LirImport>();
            SpecialFunctions = new Dictionary<string, LirFunction>();
        }

        public string EntryFunctionName { get; }
        public List<LirFunction> Functions { get; }
        public Dictionary<string, LirDataItem> Data { get; }
        public List<LirDataItem> DataItems { get; }
        public List<LirImport> Imports { get; }
        public Dictionary<string, LirFunction> SpecialFunctions { get; }

        /// <summary>取或建字符串字面量数据项（去重，返回 key）。</summary>
        public string InternString(string text)
        {
            if (!_dataIndex.TryGetValue(text, out _))
            {
                var item = LirDataItem.Utf16(text, text);
                _dataIndex.Add(text, DataItems.Count);
                Data.Add(text, item);
                DataItems.Add(item);
            }

            return text;
        }

        /// <summary>追加数据项（运行时数据），返回其 key。</summary>
        public string AddData(LirDataItem item)
        {
            if (!_dataIndex.TryGetValue(item.Key, out _))
            {
                _dataIndex.Add(item.Key, DataItems.Count);
                DataItems.Add(item);
            }

            return item.Key;
        }
    }
}