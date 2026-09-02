using System;
using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.Native.Assembler.X64
{
    internal enum X64Size
    {
        Byte = 0,
        Dword = 1,
        Qword = 2,
        Word = 3,
    }

    internal enum X64Register
    {
        RAX = 0,
        RCX = 1,
        RDX = 2,
        RBX = 3,
        RSP = 4,
        RBP = 5,
        RSI = 6,
        RDI = 7,
        R8 = 8,
        R9 = 9,
        R10 = 10,
        R11 = 11,
        R12 = 12,
        R13 = 13,
        R14 = 14,
        R15 = 15,

        EAX = 0,
        ECX = 1,
        EDX = 2,
        EBX = 3,
        ESP = 4,
        EBP = 5,
        ESI = 6,
        EDI = 7,

        XMM0 = 16,
        XMM1 = 17,
        XMM2 = 18,
        XMM3 = 19,
        XMM4 = 20,
        XMM5 = 21,
        XMM6 = 22,
        XMM7 = 23,
        XMM8 = 24,
        XMM9 = 25,
        XMM10 = 26,
        XMM11 = 27,
        XMM12 = 28,
        XMM13 = 29,
        XMM14 = 30,
        XMM15 = 31,
    }

    internal enum X64CondCode
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Below,
        BelowOrEqual,
        Above,
        AboveOrEqual,
        Parity,
        NoParity,
    }

    internal readonly struct X64MemoryOperand
    {
        public X64MemoryOperand(X64Register baseRegister, int displacement)
        {
            Base = baseRegister;
            Displacement = displacement;
        }

        public X64Register Base { get; }
        public int Displacement { get; }
    }

    internal sealed partial class X64Assembler : IAssembler
    {
        private readonly List<byte> _bytes = new List<byte>();
        private readonly List<byte> _data = new List<byte>();
        private readonly Dictionary<int, int> _labels = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dataOffsets = new Dictionary<int, int>();
        private readonly List<(int Offset, int Label)> _labelFixups = new List<(int Offset, int Label)>();
        private readonly List<(int Offset, int Symbol)> _dataFixups = new List<(int Offset, int Symbol)>();
        private readonly List<(int DataOffset, int Label)> _dataCodeFixups = new List<(int DataOffset, int Label)>();
        private readonly List<(int DataOffset, int Symbol)> _dataDataFixups = new List<(int DataOffset, int Symbol)>();
        private readonly List<int> _dataAbsoluteFixups = new List<int>();

        public System.Collections.Generic.IReadOnlyList<int> DataAbsoluteFixups => _dataAbsoluteFixups;
        private int _nextLabelId;
        private int _nextSymbolId;

        public int Position => _bytes.Count;
        public int DataPosition => _data.Count;
        public int DataLength => _data.Count;

        public int CreateLabel() => _nextLabelId++;

        public void MarkLabel(int label)
        {
            _labels.Add(label, Position);
        }

        public int GetLabelOffset(int label)
        {
            return _labels[label];
        }

        public int CreateDataSymbol() => _nextSymbolId++;

        public void MarkDataSymbol(int symbol)
        {
            _dataOffsets.Add(symbol, DataPosition);
        }

        public int GetDataOffset(int symbol)
        {
            return _dataOffsets[symbol];
        }

        public void WriteDataByte(byte value)
        {
            _data.Add(value);
        }

        public void WriteDataInt32(int value)
        {
            _data.Add((byte)value);
            _data.Add((byte)(value >> 8));
            _data.Add((byte)(value >> 16));
            _data.Add((byte)(value >> 24));
        }

        public void WriteDataInt16(int value)
        {
            _data.Add((byte)value);
            _data.Add((byte)(value >> 8));
        }

        public void WriteDataInt64(long value)
        {
            _data.Add((byte)value);
            _data.Add((byte)(value >> 8));
            _data.Add((byte)(value >> 16));
            _data.Add((byte)(value >> 24));
            _data.Add((byte)(value >> 32));
            _data.Add((byte)(value >> 40));
            _data.Add((byte)(value >> 48));
            _data.Add((byte)(value >> 56));
        }

        public void WriteDataBytes(params byte[] values)
        {
            _data.AddRange(values);
        }

        public void WriteDataBytes(IEnumerable<byte> values)
        {
            _data.AddRange(values);
        }

        public void WriteDataUtf16(string value)
        {
            WriteDataInt32(value.Length);
            foreach (var c in value)
            {
                WriteDataInt16(c);
            }
        }

        public void AlignData(int alignment)
        {
            while (_data.Count % alignment != 0)
            {
                _data.Add(0);
            }
        }

        public void Patch(int dataTextDelta, long imageBase)
        {
            foreach (var fixup in _labelFixups)
            {
                if (!_labels.TryGetValue(fixup.Label, out var target))
                {
                    throw new InvalidOperationException($"Label {fixup.Label} was never marked.");
                }

                WriteInt32At(fixup.Offset, target - (fixup.Offset + 4));
            }

            foreach (var fixup in _dataFixups)
            {
                if (!_dataOffsets.TryGetValue(fixup.Symbol, out var dataOffset))
                {
                    throw new InvalidOperationException($"Data symbol {fixup.Symbol} was never marked.");
                }

                WriteInt32At(fixup.Offset, dataTextDelta + dataOffset - (fixup.Offset + 4));
            }

            // M4a：数据段内绝对地址（VA）——vtable 槽 → 代码 / 名字指针 → 数据
            foreach (var fixup in _dataCodeFixups)
            {
                if (!_labels.TryGetValue(fixup.Label, out var labelOffset))
                {
                    throw new InvalidOperationException($"Label {fixup.Label} was never marked.");
                }

                WriteDataInt64At(fixup.DataOffset, imageBase + PeFileWriter.TextRva + labelOffset);
            }

            foreach (var fixup in _dataDataFixups)
            {
                if (!_dataOffsets.TryGetValue(fixup.Symbol, out var dataOffset))
                {
                    throw new InvalidOperationException($"Data symbol {fixup.Symbol} was never marked.");
                }

                WriteDataInt64At(fixup.DataOffset, imageBase + dataTextDelta + PeFileWriter.TextRva + dataOffset);
            }
        }

        public void AddDataCodeFixup(int dataOffset, int label)
        {
            _dataCodeFixups.Add((dataOffset, label));
            _dataAbsoluteFixups.Add(dataOffset);
        }

        public void AddDataDataFixup(int dataOffset, int symbol)
        {
            _dataDataFixups.Add((dataOffset, symbol));
            _dataAbsoluteFixups.Add(dataOffset);
        }

        private void WriteDataInt64At(int offset, long value)
        {
            _data[offset] = (byte)value;
            _data[offset + 1] = (byte)(value >> 8);
            _data[offset + 2] = (byte)(value >> 16);
            _data[offset + 3] = (byte)(value >> 24);
            _data[offset + 4] = (byte)(value >> 32);
            _data[offset + 5] = (byte)(value >> 40);
            _data[offset + 6] = (byte)(value >> 48);
            _data[offset + 7] = (byte)(value >> 56);
        }

        public byte[] ToArray()
        {
            return _bytes.ToArray();
        }

        public byte[] GetData()
        {
            return _data.ToArray();
        }

    }
}
