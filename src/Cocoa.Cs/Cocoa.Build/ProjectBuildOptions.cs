using Cocoa.Targeting;
using System.Collections.Immutable;

namespace Cocoa.Build
{
    public sealed class ProjectBuildOptions
    {
        public const CodeBackend DefaultBackend = CodeBackend.DotNet;

        public ProjectOutputFormat? FormatOverride { get; set; }
        public string? PlatformOverride { get; set; }
        public bool NoIncremental { get; set; }
        public ProjectConfiguration? ConfigurationOverride { get; set; }
        public string? OutputFileOverride { get; set; }
        public ImmutableArray<string> ReferenceOverrides { get; set; } = ImmutableArray<string>.Empty;
        public CodeBackend? Backend { get; set; }
        public string? DotnetRuntimeOverride { get; set; }
        public string? CacheRoot { get; set; }

        /// <summary>6e-M24：XML documentation 文件输出路径（null = 不生成）。</summary>
        public string? DocOutput { get; set; }
    }
}
