using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_LOAD_CONFIG_CODE_INTEGRITY — 代码完整性（8 字节）。</summary>
    public readonly record struct ImageLoadConfigCodeIntegrity(
        ushort Flags,
        ushort Catalog,
        uint CatalogOffset)
    {
        public static int SizeOfEntry => 8;

        public static ImageLoadConfigCodeIntegrity Read(ReadOnlySpan<byte> s)
        {
            return new ImageLoadConfigCodeIntegrity(
                BinaryPrimitives.ReadUInt16LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(2), Catalog);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), CatalogOffset);
        }
    }

    /// <summary>IMAGE_LOAD_CONFIG_DIRECTORY64 — 加载配置目录（winnt.h 全字段，324 字节）。</summary>
    public readonly record struct ImageLoadConfigDirectory64(
        uint Size,
        uint TimeDateStamp,
        ushort MajorVersion,
        ushort MinorVersion,
        uint GlobalFlagsClear,
        uint GlobalFlagsSet,
        uint CriticalSectionDefaultTimeout,
        ulong DeCommitFreeBlockThreshold,
        ulong DeCommitTotalFreeThreshold,
        ulong LockPrefixTable,
        ulong MaximumAllocationSize,
        ulong VirtualMemoryThreshold,
        ulong ProcessAffinityMask,
        uint ProcessHeapFlags,
        ushort CSDVersion,
        ushort DependentLoadFlags,
        ulong EditList,
        ulong SecurityCookie,
        ulong SEHandlerTable,
        ulong SEHandlerCount,
        ulong GuardCFCheckFunctionPointer,
        ulong GuardCFDispatchFunctionPointer,
        ulong GuardCFFunctionTable,
        ulong GuardCFFunctionCount,
        uint GuardFlags,
        ImageLoadConfigCodeIntegrity CodeIntegrity,
        ulong GuardAddressTakenIatEntryTable,
        ulong GuardAddressTakenIatEntryCount,
        ulong GuardLongJumpTargetTable,
        ulong GuardLongJumpTargetCount,
        ulong DynamicValueRelocTable,
        ulong CHPEMetadataPointer,
        ulong GuardRFFailureRoutine,
        ulong GuardRFFailureRoutineFunctionPointer,
        uint DynamicValueRelocTableOffset,
        uint DynamicValueRelocTableSection,
        uint Reserved2,
        ulong GuardRFVerifyStackPointerFunctionPointer,
        uint HotPatchTableOffset,
        uint Reserved3,
        ulong EnclaveConfigurationPointer,
        ulong VolatileMetadataPointer,
        ulong GuardEHContinuationTable,
        ulong GuardEHContinuationCount,
        ulong GuardXFGCheckFunctionPointer,
        ulong GuardXFGDispatchFunctionPointer,
        ulong GuardXFGTableDispatchFunctionPointer,
        ulong CastGuardOsDeterminedFailureMode,
        ulong GuardMemcpyFunctionPointer)
    {
        public static int SizeOfEntry => 324;

        public static ImageLoadConfigDirectory64 Read(ReadOnlySpan<byte> s)
        {
            return new ImageLoadConfigDirectory64(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(32)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(40)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(48)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(56)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(64)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(72)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(76)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(78)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(80)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(88)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(96)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(104)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(112)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(120)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(128)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(136)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(144)),
                ImageLoadConfigCodeIntegrity.Read(s.Slice(148)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(156)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(164)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(172)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(180)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(188)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(196)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(204)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(212)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(220)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(224)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(228)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(236)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(244)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(248)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(252)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(260)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(268)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(276)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(284)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(292)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(300)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(308)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(316)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Size);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), MajorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), MinorVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), GlobalFlagsClear);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), GlobalFlagsSet);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), CriticalSectionDefaultTimeout);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(24), DeCommitFreeBlockThreshold);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(32), DeCommitTotalFreeThreshold);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(40), LockPrefixTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(48), MaximumAllocationSize);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(56), VirtualMemoryThreshold);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(64), ProcessAffinityMask);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(72), ProcessHeapFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(76), CSDVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(78), DependentLoadFlags);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(80), EditList);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(88), SecurityCookie);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(96), SEHandlerTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(104), SEHandlerCount);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(112), GuardCFCheckFunctionPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(120), GuardCFDispatchFunctionPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(128), GuardCFFunctionTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(136), GuardCFFunctionCount);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(144), GuardFlags);
            CodeIntegrity.Write(d.Slice(148));
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(156), GuardAddressTakenIatEntryTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(164), GuardAddressTakenIatEntryCount);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(172), GuardLongJumpTargetTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(180), GuardLongJumpTargetCount);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(188), DynamicValueRelocTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(196), CHPEMetadataPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(204), GuardRFFailureRoutine);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(212), GuardRFFailureRoutineFunctionPointer);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(220), DynamicValueRelocTableOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(224), DynamicValueRelocTableSection);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(228), Reserved2);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(236), GuardRFVerifyStackPointerFunctionPointer);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(244), HotPatchTableOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(248), Reserved3);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(252), EnclaveConfigurationPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(260), VolatileMetadataPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(268), GuardEHContinuationTable);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(276), GuardEHContinuationCount);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(284), GuardXFGCheckFunctionPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(292), GuardXFGDispatchFunctionPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(300), GuardXFGTableDispatchFunctionPointer);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(308), CastGuardOsDeterminedFailureMode);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(316), GuardMemcpyFunctionPointer);
        }
    }
}