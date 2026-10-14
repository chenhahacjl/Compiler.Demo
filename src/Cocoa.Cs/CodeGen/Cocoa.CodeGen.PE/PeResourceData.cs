namespace Cocoa.CodeGen.PE
{
    /// <summary>PE 资源条目：类型（RT_*）、名称 ID、语言 ID 与原始数据。用于 .rsrc 节生成。</summary>
    public sealed class PeResourceData
    {
        public PeResourceData(uint type, uint nameId, ushort languageId, byte[] data)
        {
            Type = type;
            NameId = nameId;
            LanguageId = languageId;
            Data = data;
        }

        public uint Type { get; }
        public uint NameId { get; }
        public ushort LanguageId { get; }
        public byte[] Data { get; }
    }
}