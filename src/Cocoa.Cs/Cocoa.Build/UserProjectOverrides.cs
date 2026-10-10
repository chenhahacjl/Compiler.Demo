namespace Cocoa.Build
{
    /// <summary>用户级覆盖（`.coproj.user`，仿 `.csproj.user`）：仅可覆盖构建属性，未知节/键为 IDE 预留。</summary>
    public sealed class UserProjectOverrides
    {
        public string? AssemblyName { get; set; }
        public ProjectOutputFormat? Output { get; set; }
        public string? Platform { get; set; }
        public string? TargetFramework { get; set; }
        public CocoaTargetOs? TargetOs { get; set; }
        public ProjectConfiguration? Configuration { get; set; }
        public string? OutputPath { get; set; }
    }
}