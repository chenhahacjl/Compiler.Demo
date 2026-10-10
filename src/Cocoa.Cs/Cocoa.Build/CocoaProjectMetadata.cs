namespace Cocoa.Build
{
    /// <summary>程序集元数据（`Label="Assembly"` 等，发射预留）。</summary>
    public sealed class CocoaProjectMetadata
    {
        public string? RootNamespace { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public string? Company { get; init; }
        public string? Product { get; init; }
        public string? Authors { get; init; }
        public string? Copyright { get; init; }
        public string? AssemblyVersion { get; init; }
        public string? FileVersion { get; init; }
        public string? ProjectGuid { get; init; }
    }
}