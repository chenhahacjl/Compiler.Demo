namespace Cocoa.IlMetadata
{
    /// <summary>内嵌资源（ManifestResource 表行）：名称 + 原始数据。运行时经 Assembly.GetManifestResourceStream(name) 读取。</summary>
    public sealed class IlManifestResource
    {
        public IlManifestResource(string name, byte[] data)
        {
            Name = name;
            Data = data;
        }

        public string Name { get; }
        public byte[] Data { get; }
    }
}