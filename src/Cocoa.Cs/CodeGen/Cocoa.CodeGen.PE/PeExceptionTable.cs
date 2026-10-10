using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>UNWIND_INFO — 最小展开信息（可选 UNWIND_CODE 数组 + 4 字节对齐）。</summary>
    public readonly record struct PeUnwindInfo(
        byte VersionAndFlags,
        byte SizeOfProlog,
        byte CountOfCodes,
        byte FrameRegisterAndOffset,
        byte[] Slots)
    {
        public const byte VersionMask = 0x07;
        public const byte FlagsMask = 0xF8;

        public int Size => 4 + Slots.Length;

        public byte Version => (byte)(VersionAndFlags & VersionMask);

        public byte Flags => (byte)(VersionAndFlags & FlagsMask);

        public byte FrameRegister => (byte)(FrameRegisterAndOffset & 0x0F);

        public byte FrameRegisterOffset => (byte)(FrameRegisterAndOffset >> 4);

        public static PeUnwindInfo Read(ReadOnlySpan<byte> s)
        {
            var countOfCodes = s[2];
            var codeBytes = countOfCodes * PeUnwindCode.SizeOfEntry;
            var padded = (codeBytes + 3) & ~3;
            return new PeUnwindInfo(s[0], s[1], countOfCodes, s[3], s.Slice(4, padded).ToArray());
        }

        public void Write(Span<byte> d)
        {
            d[0] = VersionAndFlags;
            d[1] = SizeOfProlog;
            d[2] = CountOfCodes;
            d[3] = FrameRegisterAndOffset;
            Slots.CopyTo(d.Slice(4));
        }
    }
}
