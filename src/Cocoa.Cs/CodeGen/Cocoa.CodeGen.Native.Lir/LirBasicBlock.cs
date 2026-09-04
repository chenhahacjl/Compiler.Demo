using System.Collections.Generic;


namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>
    /// 基本块（Phase 2 LLVM 式显式 CFG）：块尾以 <see cref="LirTerminator"/> 收束，
    /// 块内只含非分支指令（Label/Jmp/Jcc/Ret 不进入 Instructions）。
    /// 一个块可挂多个 label 别名（顺序 Label 折叠、以及 Ret 的 EndLabelId 与前置标签同址）。
    /// </summary>
    public sealed class LirBasicBlock
    {
        private readonly List<int> _labels = new();

        public LirBasicBlock()
        {
            Instructions = new List<LirInstruction>();
        }

        /// <summary>投向本块的 label id 集合（块首原样 MarkLabel，保持与线性 IR 同址）。</summary>
        public IReadOnlyList<int> Labels => _labels;

        public List<LirInstruction> Instructions { get; }

        /// <summary>块尾控制传输；块闭合（LirProgram 构建）时写入，此前为 null。</summary>
        public LirTerminator? Terminator { get; set; }

        public void AddLabel(int labelId) => _labels.Add(labelId);
    }

    /// <summary>块尾控制传输：无条件跳转 / 条件跳转（false 落入下一块）/ 返回（指向 EndLabelId）。</summary>
    public sealed class LirTerminator
    {
        public LirTerminatorKind Kind { get; private set; }

        /// <summary>Jump/CondJump 的目标 label id；Return 的 EndLabelId。</summary>
        public int TargetLabelId { get; private set; }

        /// <summary>CondJump 条件（Jump/Return 忽略）。</summary>
        public LirCond Cond { get; private set; }

        private LirTerminator()
        {
        }

        public static LirTerminator Jump(int targetLabelId) => new LirTerminator
        {
            Kind = LirTerminatorKind.Jump,
            TargetLabelId = targetLabelId,
        };

        public static LirTerminator CondJump(LirCond cond, int targetLabelId) => new LirTerminator
        {
            Kind = LirTerminatorKind.CondJump,
            Cond = cond,
            TargetLabelId = targetLabelId,
        };

        public static LirTerminator Return(int endLabelId) => new LirTerminator
        {
            Kind = LirTerminatorKind.Return,
            TargetLabelId = endLabelId,
        };
    }

    public enum LirTerminatorKind
    {
        Jump,
        CondJump,
        Return,
    }
}