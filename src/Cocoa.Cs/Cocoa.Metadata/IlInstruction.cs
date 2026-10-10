using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>自研 IL 指令：OpCode + 可选操作数。</summary>
    public sealed class IlInstruction
    {
        public IlInstruction(IlOpCode opCode, object? operand)
        {
            OpCode = opCode;
            Operand = operand;
        }

        public IlOpCode OpCode { get; }
        public object? Operand { get; }
        public int Offset { get; set; }
    }
}
