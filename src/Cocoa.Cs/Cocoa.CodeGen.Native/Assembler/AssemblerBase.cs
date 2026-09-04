using System;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Assembler
{
    /// <summary>
    /// x86/x64 汇编器共享簿记（5.4：原两份 ~105 行逐字节复制下沉至此）。
    /// 平台分化点仅 Patch 的数据段修补：x64 rel32 + 8B 绝对 VA；x86 绝对 32 位地址。
    /// </summary>
    public abstract class AssemblerBase
    {
        protected readonly List<byte> _bytes = new List<byte>();
        protected readonly List<byte> _data = new List<byte>();
        protected readonly Dictionary<int, int> _labels = new Dictionary<int, int>();
        protected readonly Dictionary<int, int> _dataOffsets = new Dictionary<int, int>();
        protected readonly List<(int Offset, int Label)> _labelFixups = new List<(int Offset, int Label)>();
        protected readonly List<(int Offset, int Symbol)> _dataFixups = new List<(int Offset, int Symbol)>();
        protected readonly List<(int DataOffset, int Label)> _dataCodeFixups = new List<(int DataOffset, int Label)>();
        protected readonly List<(int DataOffset, int Symbol)> _dataDataFixups = new List<(int DataOffset, int Symbol)>();
        protected readonly List<int> _dataAbsoluteFixups = new List<int>();

        protected int _nextLabelId;
        protected int _nextSymbolId;

        public System.Collections.Generic.IReadOnlyList<int> DataAbsoluteFixups => _dataAbsoluteFixups;

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

        public abstract void Patch(int dataTextDelta, long imageBase);

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
