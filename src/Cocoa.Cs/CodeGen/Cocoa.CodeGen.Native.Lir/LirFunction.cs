using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>IR 函数：指令列表 + 虚拟寄存器登记表。生成期写线性 Instructions；消费期经 Blocks 显式 CFG。</summary>
    public sealed class LirFunction
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
}