using System.Collections.Generic;

namespace Cocoa.Metadata
{
    public readonly struct IlOpCode
    {
        public IlOpCode(ushort value, IlOperandType operandType, int size)
        {
            Value = value;
            OperandType = operandType;
            Size = size;
        }

        public ushort Value { get; }
        public IlOperandType OperandType { get; }
        public int Size { get; }

        /// <summary>双字节 opcode（0xFE 前缀）</summary>
        public bool IsTwoByte => Value >= 0xFE00;

        public override string ToString()
        {
            if (IsTwoByte)
            {
                return "0xFE" + (Value & 0xFF).ToString("X2");
            }

            return "0x" + Value.ToString("X2");
        }
    }
}
