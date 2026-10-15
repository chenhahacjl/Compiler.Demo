namespace Cocoa.Targeting
{
    /// <summary>
    /// 代码生成后端（产物链，显式必填）：决定产物类型。
    /// managed-fx = .NET Framework（net40~net48，仅 Windows，PE32 + mscoree 宿主）；
    /// managed-net = .NET（net5+，跨平台，apphost/dotnet host + runtimeconfig）。
    /// </summary>
    public enum CodeBackend
    {
        Native,
        ManagedFx,
        ManagedNet,
    }

    public static class CodeBackendExtensions
    {
        /// <summary>是否托管后端（ManagedFx/ManagedNet，即旧 CodeBackend.DotNet 语义）。</summary>
        public static bool IsManaged(this CodeBackend backend) => backend != CodeBackend.Native;
    }
}
