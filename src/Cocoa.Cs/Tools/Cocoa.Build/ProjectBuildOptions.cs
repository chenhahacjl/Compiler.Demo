using Cocoa.Targeting;
using System.Collections.Immutable;

namespace Cocoa.Build
{
    public sealed class ProjectBuildOptions
    {
        public const CodeBackend DefaultBackend = CodeBackend.ManagedNet;

        public ProjectOutputFormat? FormatOverride { get; set; }
        public string? PlatformOverride { get; set; }

        /// <summary>T3：目标 OS 覆盖（--os linux/windows）；空 = 项目 <c>TargetOS</c> 声明。</summary>
        public TargetOS? TargetOsOverride { get; set; }

        public bool NoIncremental { get; set; }
        public ProjectConfiguration? ConfigurationOverride { get; set; }
        public string? OutputFileOverride { get; set; }
        public ImmutableArray<string> ReferenceOverrides { get; set; } = ImmutableArray<string>.Empty;
        public CodeBackend? Backend { get; set; }
        public string? DotnetRuntimeOverride { get; set; }

        /// <summary>6e-M24：XML documentation 文件输出路径（null = 不生成）。</summary>
        public string? DocOutput { get; set; }
    }
}
