using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.PE;
 using Cocoa.Targeting;

using Cocoa.CodeAnalysis;


using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>IR → IAssembler 的发射结果：全部函数/特殊函数 label 与入口 stub label。</summary>
    internal sealed class LirEmitResult
    {
        public LirEmitResult(Dictionary<string, int> labels, int stubLabel, List<PefileImport> imports)
        {
            Labels = labels;
            StubLabel = stubLabel;
            Imports = imports;
        }

        public Dictionary<string, int> Labels { get; }
        public int StubLabel { get; }
        public List<PefileImport> Imports { get; }
    }

    /// <summary>
    /// IR 到 IAssembler 的映射。寄存器分配策略：每个虚拟寄存器 → 函数帧内唯一栈槽
    /// （slot k @ [rbp - 16 - slotSize*k]），物理寄存器（eax/ecx/edx…）仅作瞬时运算载体。
    /// 帧布局、参数传递、TEB 栈限检查、x64 16 字节对齐与现有 NativeCodeEmitter 完全一致。
    /// </summary>
    internal sealed partial class LirToAssembler
    {
        private readonly IAssembler _a;
        private readonly LirProgram _program;
        private readonly int _entryLabel;
        private readonly TargetPlatform _platform;
        private readonly bool _isX64;
        private readonly int _slotSize;
        private readonly int _paramOffset;
        private readonly int _stackLimitOffset;
        private readonly Action<IReadOnlyList<PefileImport>, int>? _emitStub;

        private readonly Dictionary<LirFunction, int> _functionLabels = new();
        private readonly Dictionary<string, int> _nameToLabel = new();
        private readonly Dictionary<int, int> _asmLabelCache = new();
        private readonly Dictionary<string, int> _dataSymbols = new();
        private readonly Dictionary<LirImport, int> _importSlots = new();
        private readonly List<PefileImport> _imports = new();
        private readonly List<LirVirtualRegister> _sysArgs = new();

        private Dictionary<LirVirtualRegister, int> _slots = new();
        private int _stackDepth;
        private int _frameBytes;

        // 1c/C1：x87 暂存区字节数（帧底 [-frameBytes..) 起；cw 槽 [-fb..+4)、u64→浮点常量槽
        // [-fb+8..+16)）。LeaSlot 缓冲必须从 -frameBytes + _x87ScratchBytes + 槽宽 起步，
        // 否则两特性共存时（同一函数既用 BuildInt 缓冲又做 u64→浮点转换）缓冲字节会被
        // 常量写入踩踏——旧布局两者都在 [-fb+4..) 起，构成重叠。
        private int _x87ScratchBytes;

        private readonly Stack<bool> _alignStack = new();
        private LirFunction? _currentFunction;
        private int _stubLabel;

        private LirToAssembler(IAssembler a, LirProgram program, int entryLabel, TargetPlatform platform,
            Action<IReadOnlyList<PefileImport>, int>? emitStub)
        {
            _a = a;
            _program = program;
            _entryLabel = entryLabel;
            _platform = platform;
            _isX64 = platform.Arch == Architecture.X64;
            _slotSize = _isX64 ? 8 : 4;
            _paramOffset = _isX64 ? 16 : 8;
            _stackLimitOffset = _isX64 ? 0x10 : 0x08;
            _emitStub = emitStub;
        }

        private X64Size SlotSize => _slotSize == 8 ? X64Size.Qword : X64Size.Dword;

        public static LirEmitResult Emit(IAssembler a, LirProgram program, int entryLabel, TargetPlatform platform,
            Action<IReadOnlyList<PefileImport>, int>? emitStub)
        {
            var emitter = new LirToAssembler(a, program, entryLabel, platform, emitStub);
            emitter.EmitProgram();
            return new LirEmitResult(emitter._nameToLabel, emitter._stubLabel, emitter._imports);
        }

        private void EmitProgram()
        {
            if (System.Environment.GetEnvironmentVariable("COCOA_DUMP_IR") != null)
                DumpIr();
            EmitData();
            EmitStub();
            EmitFunctions();
            RegisterVTableFixups();
        }

        private void DumpIr()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var fn in _program.Functions)
            {
                sb.AppendLine($"=== {fn.Name} (ret={fn.ReturnSize}) ===");
                foreach (var block in fn.Blocks)
                {
                    var labelText = block.Labels.Count > 0 ? string.Join("/", block.Labels) : "-";
                    sb.AppendLine($"  bb:{labelText}");
                    foreach (var ins in block.Instructions)
                        sb.AppendLine("    " + ins.ToString());
                    if (block.Terminator != null)
                        sb.AppendLine("    " + LirPrinter.Format(block.Terminator));
                }
            }
            var fn2 = _isX64 ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-ir-x64.txt") : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-ir-x86.txt");
            try { System.IO.File.WriteAllText(fn2, sb.ToString()); } catch { }
        }

        // ------------------------------------------------------------------
        // 数据段：语言层字符串 + 运行时数据项 + 导入槽
        // ------------------------------------------------------------------

        private void EmitData()
        {
            foreach (var item in _program.DataItems)
            {
                var symbol = _a.CreateDataSymbol();
                _a.MarkDataSymbol(symbol);
                _dataSymbols.Add(item.Key, symbol);

                switch (item.Kind)
                {
                    case LirDataKind.Int32:
                        _a.WriteDataInt32(item.IntValue);
                        break;
                    case LirDataKind.Pointer:
                        if (_isX64)
                        {
                            _a.WriteDataInt64(0);
                        }
                        else
                        {
                            _a.WriteDataInt32(0);
                        }

                        break;
                    case LirDataKind.Utf16:
                        _a.WriteDataUtf16(item.Text!);
                        _a.AlignData(4);
                        break;
                    case LirDataKind.Bytes:
                        _a.WriteDataBytes(item.Bytes!);
                        break;
                    case LirDataKind.VTable:
                        EmitVTableData(item);
                        break;
                    default:
                        throw new Exception($"Unexpected data kind: {item.Kind}");
                }
            }

            // 分组内聚：kernel32 组（运行时基础）全部在前，其余 DLL 组按首见顺序聚合，组内保持相对顺序。
            // IAT 由 OS 加载器按描述符 FirstThunk 连续填充，槽数组必须与 specs 分组顺序一致。
            var seenImports = new HashSet<LirImport>();
            var imports = _program.Imports.Where(seenImports.Add).ToList();
            var kernel32Group = imports.Where(i => string.Equals(i.DllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            var otherGroups = imports.Where(i => !string.Equals(i.DllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
                                     .GroupBy(i => i.DllName, StringComparer.OrdinalIgnoreCase)
                                     .SelectMany(g => g)
                                     .ToList();
            var reordered = kernel32Group.Concat(otherGroups).ToList();

            foreach (var import in reordered)
            {
                var symbol = _a.CreateDataSymbol();
                _a.MarkDataSymbol(symbol);
                _importSlots.Add(import, symbol);

                // IAT 槽（8/4 字节）：磁盘初值 0，OS 加载器启动时按导入描述符填充
                if (_isX64)
                {
                    _a.WriteDataInt64(0);
                }
                else
                {
                    _a.WriteDataInt32(0);
                }
            }

            foreach (var import in reordered)
            {
                _imports.Add(new PefileImport(import.DllName, import.Name, _a.GetDataOffset(_importSlots[import])));
            }
        }

        private void EmitStub()
        {
            if (_emitStub == null)
            {
                // 无自解析 stub（6c-2）：入口即用户入口
                _stubLabel = _entryLabel;
                return;
            }

            _stubLabel = _a.CreateLabel();
            _a.MarkLabel(_stubLabel);
            _emitStub(_imports, _stubLabel);
        }

        // ------------------------------------------------------------------
        // vtable 数据发射与重定位（6e-M19 M4）
        // 布局：[0] typeId:int32（伪记录 = 自引用指针，宽 ps）[4] pad [8] 名字指针
        //       [8+ps·(i+1)] 槽 i 函数绝对地址。两架构槽偏移公式一致。
        // ------------------------------------------------------------------

        private readonly List<(LirDataItem Item, int Symbol)> _vtableSymbols = new();

        private void EmitVTableData(LirDataItem item)
        {
            var symbol = _dataSymbols[item.Key];
            _vtableSymbols.Add((item, symbol));
            var ps = _isX64 ? 8 : 4;

            if (item.TypeId < 0)
            {
                // 伪记录：[0..ps) 自引用指针（Patch 阶段经 AddDataDataFixup 回填自身地址），
                // 使 Type 值直接作为对象使用时 [[x+0]+8] 取名字成立。头部补齐到 8 字节：
                // x64 自引用即占满 [0..8)；x86 补 4 字节 pad。
                for (var i = 0; i < ps; i++)
                {
                    _a.WriteDataByte(0);
                }

                if (!_isX64)
                {
                    _a.WriteDataInt32(0);
                }
            }
            else
            {
                _a.WriteDataInt32(item.TypeId);
                _a.WriteDataInt32(0); // pad
            }

            _a.WriteDataBytes(new byte[ps]); // [8] 名字指针占位

            foreach (var _ in item.Slots!)
            {
                _a.WriteDataBytes(new byte[ps]); // 函数指针槽占位
            }
        }

        /// <summary>EmitFunctions 之后调用（_nameToLabel/_dataSymbols 已就绪）：注册 vtable 内部重定位。</summary>
        private void RegisterVTableFixups()
        {
            var ps = _isX64 ? 8 : 4;

            foreach (var (item, symbol) in _vtableSymbols)
            {
                var baseOffset = _a.GetDataOffset(symbol);

                if (item.TypeId < 0)
                {
                    // 自引用指针 → 自身数据地址
                    _a.AddDataDataFixup(baseOffset, symbol);
                }

                // 名字指针 @8 → 类型全名字符串数据项
                _a.AddDataDataFixup(baseOffset + 8, _dataSymbols[item.NameKey!]);

                // 函数槽 @8+ps·(i+1) → 函数代码绝对地址
                for (var i = 0; i < item.Slots!.Count; i++)
                {
                    var functionName = item.Slots[i];
                    if (!_nameToLabel.TryGetValue(functionName, out var label))
                    {
                        throw new Exception($"VTable slot function '{functionName}' was not emitted.");
                    }

                    _a.AddDataCodeFixup(baseOffset + 8 + ps * (i + 1), label);
                }
            }
        }

        private void EmitFunctions()
        {
            foreach (var function in _program.Functions)
            {
                var label = function.Name == _program.EntryFunctionName ? _entryLabel : _a.CreateLabel();
                _functionLabels.Add(function, label);
                _nameToLabel[function.Name] = label;
            }

            foreach (var kvp in _program.SpecialFunctions)
            {
                _nameToLabel[kvp.Key] = _functionLabels[kvp.Value];
            }

            foreach (var function in _program.Functions)
            {
                EmitFunction(function);
            }
        }

        private void EmitFunction(LirFunction function)
        {
            _currentFunction = function;
            _asmLabelCache.Clear();
            _sysArgs.Clear();
            _pendingCmp64Trichotomy = false;
            _x87ScratchBytes = 0;

            _slots = new Dictionary<LirVirtualRegister, int>();
            var registers = new List<LirVirtualRegister>(function.Registers);
            registers.Sort((x, y) => x.Id.CompareTo(y.Id));
            var slotCount = 0;
            foreach (var register in registers)
            {
                _slots.Add(register, slotCount);
                // x86 槽宽 4 字节：8 字节虚拟寄存器（double）占两个连续槽（低地址=低 32 位）
                slotCount += !_isX64 && function.RegisterSize(register) == 8 ? 2 : 1;
            }

            _stackDepth = 0;
            _a.MarkLabel(_functionLabels[function]);

            _a.Push(X64Register.RBP);
            _a.Mov(X64Size.Qword, X64Register.RBP, X64Register.RSP);

            // 栈帧：variables.Count 个虚拟寄存器槽 + 1 个返回值槽（与现有 ABI 一致）。
            // 若函数使用 LeaSlot（把槽当临时缓冲，如 BuildInt 需要 buf+44），在帧底预留缓冲空间。
            if (_isX64)
            {
                var frameBytes = 8 * (_slots.Count + 1);
                if (FunctionUsesOpCode(function, LirOpCode.LeaSlot))
                {
                    frameBytes += 0x80;
                }

                if (frameBytes % 16 == 8)
                {
                    frameBytes += 8;
                }

                // DivByZero/StackOverflow 由 jcc（不压栈）进入，入口 rsp 与 call 进入相差 8，
                // 帧大小需 ≡8（而非 ≡0），使 sub 后函数内 rsp 对齐与 EmitAlign 假设一致（避免调用 kernel32 时对齐崩溃）。
                if (function.Name == "DivByZero" || function.Name == "StackOverflow")
                {
                    frameBytes += 8;
                }

                _a.Sub(X64Size.Qword, X64Register.RSP, frameBytes);
                _frameBytes = frameBytes;
            }
            else
            {
                var frameBytes = 4 * (slotCount + 3);

                // 1c/C1：帧布局分区——x87 暂存区固定占据帧底 [-fb..+16)，LeaSlot 缓冲
                // 随之上移（EmitLeaSlot 基址同步加 _x87ScratchBytes），两者不再重叠。
                // 6e-M21 Phase 5b/7：x87 控制字专用槽（-frameBytes）+ u64→浮点常量槽（[-fb+8..+16)），
                // 与变量槽/LeaSlot 缓冲隔离，避免恢复 fldcw 覆盖 fistp 写入的转换结果
                if (FunctionUsesOpCode(function, LirOpCode.FCvtSD64) || FunctionUsesOpCode(function, LirOpCode.FCvtSI64U))
                {
                    _x87ScratchBytes = 16;
                    frameBytes += _x87ScratchBytes;
                }

                if (FunctionUsesOpCode(function, LirOpCode.LeaSlot))
                {
                    frameBytes += 0x80;
                }

                _a.Sub(X64Size.Dword, X64Register.RSP, frameBytes);
                _frameBytes = frameBytes;
            }

            foreach (var block in function.Blocks)
            {
                foreach (var labelId in block.Labels)
                {
                    _a.MarkLabel(GetLabel(labelId));
                }

                foreach (var instruction in block.Instructions)
                {
                    EmitInstruction(instruction);
                }

                if (block.Terminator != null)
                {
                    EmitTerminator(block);
                }
            }
        }

        private static bool FunctionUsesOpCode(LirFunction function, LirOpCode opCode)
        {
            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction.OpCode == opCode)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void EmitTerminator(LirBasicBlock block)
        {
            switch (block.Terminator!.Kind)
            {
                case LirTerminatorKind.Jump:
                    _a.Jmp(GetLabel(block.Terminator!.TargetLabelId));
                    break;

                case LirTerminatorKind.CondJump:
                    if (_pendingCmp64Trichotomy)
                    {
                        // x86 Cmp64 三路结果在 EAX：cmp eax,0 后按条件分支
                        _a.Cmp(X64Size.Dword, X64Register.EAX, 0);
                        _pendingCmp64Trichotomy = false;
                    }

                    _a.Jcc(MapCond(block.Terminator!.Cond), GetLabel(block.Terminator!.TargetLabelId));
                    break;

                case LirTerminatorKind.Return:
                    EmitRet(block.Terminator!.TargetLabelId);
                    break;

                default:
                    throw new Exception($"Unexpected terminator: {block.Terminator!.Kind}");
            }
        }

    }
}
