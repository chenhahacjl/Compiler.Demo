using System;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit
{
    /// <summary>dotnet 后端的 .NET 运行时种类。</summary>
    public enum IlRuntime
    {
        NetCore,
        NetFx,
    }

    /// <summary>
    /// dotnet 后端目标：由 TFM 解析（net9.0 → NetCore 9.0；net40~net48 → NetFx 4.x，默认 net48）。
    /// 决定引用程序集、runtimeconfig.json、宿主机制（netfx 直接运行 / netcore 走原生 apphost 或 dotnet host）。
    /// </summary>
    public sealed class IlTarget
    {
        private IlTarget(IlRuntime runtime, Version version)
        {
            Runtime = runtime;
            Version = version;
        }

        public IlRuntime Runtime { get; }
        public Version Version { get; }

        public bool IsNetFx => Runtime == IlRuntime.NetFx;

        public string Tfm => Runtime == IlRuntime.NetCore ? $"net{Version.Major}.{Version.Minor}" : $"net{Version.Major}{Version.Minor}";

        public static readonly IlTarget Default = Parse("net48");

        public static IlTarget Parse(string text)
        {
            if (!TryParse(text, out var target))
            {
                throw new ArgumentException($"invalid target framework '{text}'. Expected e.g. net9.0 (netcore) or net40~net48 (netfx)");
            }

            return target;
        }

        public static bool TryParse(string? text, out IlTarget target)
        {
            target = Default;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var t = text.ToLowerInvariant();
            if (!t.StartsWith("net", StringComparison.Ordinal))
            {
                return false;
            }

            var versionText = t.Substring(3);
            var dotIndex = versionText.IndexOf('.');
            var majorText = dotIndex < 0 ? versionText : versionText.Substring(0, dotIndex);

            if (dotIndex < 0 && versionText.Length == 2 && int.TryParse(versionText, out var fxMinor))
            {
                // net40~net48：.NET Framework，CLR v4.0.30319
                target = new IlTarget(IlRuntime.NetFx, new Version(4, 0));
                return true;
            }

            if (dotIndex > 0 && int.TryParse(majorText, out var major) &&
                int.TryParse(versionText.Substring(dotIndex + 1), out var minor))
            {
                target = new IlTarget(IlRuntime.NetCore, new Version(major, minor));
                return true;
            }

            return false;
        }

        /// <summary>netcore 运行时用的 runtimeconfig.json（framework-dependent）。netfx 不写。</summary>
        public string GetRuntimeConfigJson()
        {
            var version = $"{Version.Major}.{Version.Minor}.0";
            return "{\n" +
                   "  \"runtimeOptions\": {\n" +
                   $"    \"tfm\": \"{Tfm}\",\n" +
                   "    \"framework\": {\n" +
                   "      \"name\": \"Microsoft.NETCore.App\",\n" +
                   $"      \"version\": \"{version}\"\n" +
                   "    }\n" +
                   "  }\n" +
                   "}\n";
        }
    }
}
