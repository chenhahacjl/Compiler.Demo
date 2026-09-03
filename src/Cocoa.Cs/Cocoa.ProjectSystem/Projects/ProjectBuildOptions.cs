using System.Collections.Immutable;

namespace Cocoa.ProjectSystem
{
    public enum ProjectBackend
    {
        Native,
        DotNet,
    }

    public sealed class ProjectBuildOptions
    {
        public const ProjectBackend DefaultBackend = ProjectBackend.DotNet;

        public ProjectOutputFormat? FormatOverride { get; set; }
        public string? PlatformOverride { get; set; }
        public bool NoIncremental { get; set; }
        public bool? DebugOverride { get; set; }
        public string? OutputFileOverride { get; set; }
        public ImmutableArray<string> ReferenceOverrides { get; set; } = ImmutableArray<string>.Empty;
        public ProjectBackend? Backend { get; set; }
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
