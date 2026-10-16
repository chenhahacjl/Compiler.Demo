using System.Collections.Generic;

namespace Cocoa.Targeting
{
    /// <summary>
    /// 目标数据目录/程序头条目（格式中立）：如 PE 数据目录（Import/Export/Resource…）、ELF 程序头（PT_LOAD/PT_DYNAMIC…）。
    /// Entry 为格式相关索引（PE 用 PeDataDirectoryEntry；ELF 用段类型），由实现解释。
    /// </summary>
    public readonly record struct TargetEntry(int Entry, uint Rva, uint Size);

    /// <summary>
    /// 对象写出目标配置（格式中立）：机器/基址/子系统/入口 + 节 + 数据目录。
    /// PE（PeImageBuilder）与 ELF（ElfObjectWriter）经此描述镜像骨架。
    /// </summary>
    public sealed class ObjectWriteOptions
    {
        public ObjectWriteOptions(TargetOS os, Cpu cpu, ulong imageBase, ushort subsystem, uint addressOfEntryPoint, uint sectionAlignment, uint fileAlignment)
        {
            Os = os;
            Cpu = cpu;
            ImageBase = imageBase;
            Subsystem = subsystem;
            AddressOfEntryPoint = addressOfEntryPoint;
            SectionAlignment = sectionAlignment;
            FileAlignment = fileAlignment;
        }

        public TargetOS Os { get; }
        public Cpu Cpu { get; }
        public ulong ImageBase { get; }
        public ushort Subsystem { get; }
        public uint AddressOfEntryPoint { get; }
        public uint SectionAlignment { get; }
        public uint FileAlignment { get; }

        /// <summary>SizeOfHeaders（PE 头+节表区大小；PeFileWriter 动态计算，PeObjectWriter 用之）。</summary>
        public uint SizeOfHeaders { get; set; } = 0x1000;

        /// <summary>输出节序列（格式中立 SectionSpec）。</summary>
        public List<SectionSpec> Sections { get; } = new();

        /// <summary>数据目录/程序头条目。</summary>
        public List<TargetEntry> Entries { get; } = new();

        /// <summary>PE 特有附加特性（Machine/FileCharacteristics 等由实现按 Cpu/Os 推导）。</summary>
        public ushort AdditionalFileCharacteristics { get; set; }

        /// <summary>DllCharacteristics（PE 可选头；ASLR/CFG 等标志）。</summary>
        public ushort DllCharacteristics { get; set; }

        /// <summary>FileCharacteristicsOverride（PE COFF 头；非零则覆盖默认 ExecutableImage）。</summary>
        public ushort FileCharacteristicsOverride { get; set; }
    }

    /// <summary>
    /// 对象写出器接口：把格式中立镜像描述（ObjectWriteOptions）写为具体对象文件字节。
    /// PeObjectWriter（PeImageBuilder 包装）/ ElfObjectWriter（linux-x64）为两实现。
    /// </summary>
    public interface IObjectWriter
    {
        byte[] Write(ObjectWriteOptions options);
    }
}
