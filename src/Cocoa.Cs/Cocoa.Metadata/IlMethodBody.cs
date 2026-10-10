using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>方法体：指令序列 + 局部变量签名 + 最大栈深度。</summary>
    public sealed class IlMethodBody
    {
        public IlMethodBody(List<IlInstruction> instructions, IReadOnlyList<IlType> locals, int maxStack)
        {
            Instructions = instructions;
            Locals = locals;
            MaxStack = maxStack;
        }

        public List<IlInstruction> Instructions { get; }
        public IReadOnlyList<IlType> Locals { get; }
        public int MaxStack { get; }
    }
}
