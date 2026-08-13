using System;
using System.Buffers.Binary;

namespace Cocoa.CodeAnalysis.Emit.Native.PEFile
{
    /// <summary>IMAGE_RUNTIME_FUNCTION_ENTRY — 异常处理函数表项（12 字节）。</summary>
    internal readonly record struct ImageRuntimeFunctionEntry(
        uint BeginAddress,
        uint EndAddress,
        uint UnwindInfoAddress)
    {
        public static int SizeOfEntry => 12;

        public static ImageRuntimeFunctionEntry Read(ReadOnlySpan<byte> s)
        {
            return new ImageRuntimeFunctionEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, BeginAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), EndAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), UnwindInfoAddress);
        }
    }

    internal enum PeUnwindOpCode : byte
    {
        PushNonvol = 0, // UWOP_PUSH_NONVOL
        AllocLarge = 1, // UWOP_ALLOC_LARGE
        AllocSmall = 2, // UWOP_ALLOC_SMALL
        SetFrame = 3, // UWOP_SET_FPREG
        SaveNonvol = 4, // UWOP_SAVE_NONVOL
        SaveNonvolFar = 5, // UWOP_SAVE_NONVOL_FAR
        SaveXmm128 = 8, // UWOP_SAVE_XMM128
        SaveXmm128Far = 9, // UWOP_SAVE_XMM128_FAR
        PushMachFrame = 10, // UWOP_PUSH_MACHFRAME
    }

    /// <summary>UNWIND_CODE — 展开指令（2 字节）。</summary>
    internal readonly record struct PeUnwindCode(byte CodeOffset, PeUnwindOpCode UnwindOp, byte OpInfo)
    {
        public static int SizeOfEntry => 2;

        public static PeUnwindCode Read(ReadOnlySpan<byte> s)
        {
            return new PeUnwindCode(s[0], (PeUnwindOpCode)(s[1] & 0x0F), (byte)(s[1] >> 4));
        }

        public void Write(Span<byte> d)
        {
            d[0] = CodeOffset;
            d[1] = (byte)(((int)UnwindOp & 0x0F) | (OpInfo << 4));
        }
    }

    /// <summary>UNWIND_INFO — 最小展开信息（可选 UNWIND_CODE 数组 + 4 字节对齐）。</summary>
    internal readonly record struct PeUnwindInfo(
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