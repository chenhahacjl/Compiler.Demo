namespace Cocoa.Build
{
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