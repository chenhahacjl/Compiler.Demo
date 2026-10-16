namespace Cocoa.Targeting
{
    /// <summary>
    /// 格式中立节描述（PE/ELF 共用）：名称 + 原始数据 + 虚拟地址 + 特性标志。
    /// PE 侧（PeSectionSpec）与 ELF 侧均映射至此；Characteristics 语义按对象格式解释。
    /// </summary>
    public sealed class SectionSpec
    {
        public SectionSpec(string name, byte[] rawData, uint virtualAddress, uint characteristics)
        {
            Name = name;
            RawData = rawData;
            VirtualAddress = virtualAddress;
            Characteristics = characteristics;
        }

        public string Name { get; }
        public byte[] RawData { get; }
        public uint VirtualAddress { get; }
        public uint Characteristics { get; }
    }
}
