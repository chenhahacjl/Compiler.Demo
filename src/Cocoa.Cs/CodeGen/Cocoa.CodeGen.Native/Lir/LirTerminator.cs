using System.Collections.Generic;


namespace Cocoa.CodeGen.Native.Lir
{
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
}