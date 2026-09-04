using Cocoa.Targeting;
using System.Collections.Immutable;

namespace Cocoa.ProjectSystem
{
    public sealed class ProjectBuildOptions
    {
        public const CodeBackend DefaultBackend = CodeBackend.DotNet;

        public ProjectOutputFormat? FormatOverride { get; set; }
        public string? PlatformOverride { get; set; }
        public bool NoIncremental { get; set; }
        public bool? DebugOverride { get; set; }
        public string? OutputFileOverride { get; set; }
        public ImmutableArray<string> ReferenceOverrides { get; set; } = ImmutableArray<string>.Empty;
        public CodeBackend? Backend { get; set; }
        public string? DotnetRuntimeOverride { get; set; }
        public string? CacheRoot { get; set; }
    }

    public sealed class ProjectBuildResult
    {
        public ProjectBuildResult(bool success, bool upToDate)
        {
            Success = success;
            UpToDate = upToDate;
        }

        public static ProjectBuildResult Failed { get; } = new(success: false, upToDate: false);

        public bool Success { get; }
        public bool UpToDate { get; }
    }
}
