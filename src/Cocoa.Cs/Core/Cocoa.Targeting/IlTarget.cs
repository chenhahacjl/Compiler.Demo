using System;
using System.Text;

namespace Cocoa.Targeting
{
    /// <summary>dotnet 后端的 .NET 运行时种类。</summary>
    public enum IlRuntime
    {
        NetCore,
        NetFx,
    }

    /// <summary>
    /// dotnet 后端目标：Runtime × Version × Architecture × Prefer32Bit 四维正交。
    /// - TFM 解析（net9.0 → NetCore 9.0；net40~net48 → NetFx 4.x，默认 net48）决定 Runtime/Version；
    /// - Arch 默认 AnyCPU（PE32 + ILONLY，运行时决定位数）；x86/x64 为显式架构；
    /// - Prefer32Bit 仅 AnyCPU 生效（COR20 32BITPREFERRED）。
    /// 决定引用程序集、runtimeconfig.json、宿主机制（netfx 直接运行 / netcore 走原生 apphost 或 dotnet host）。
    /// </summary>
    public sealed class IlTarget
    {
        private IlTarget(IlRuntime runtime, Version version, Architecture arch, bool prefer32Bit)
        {
            Runtime = runtime;
            Version = version;
            Arch = arch;
            Prefer32Bit = prefer32Bit;
        }

        public IlRuntime Runtime { get; }
        public Version Version { get; }
        public Architecture Arch { get; }
        public bool Prefer32Bit { get; }

        public bool IsNetFx => Runtime == IlRuntime.NetFx;

        /// <summary>PE32 与否：AnyCPU 与 x86 均为 PE32；x64 为 PE32+。取代旧 IsNetFx 兼任架构的判定。</summary>
        public bool IsPe32 => Arch != Architecture.X64;

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
                target = new IlTarget(IlRuntime.NetFx, new Version(4, 0), Architecture.AnyCPU, false);
                return true;
            }

            if (dotIndex > 0 && int.TryParse(majorText, out var major) &&
                int.TryParse(versionText.Substring(dotIndex + 1), out var minor))
            {
                target = new IlTarget(IlRuntime.NetCore, new Version(major, minor), Architecture.AnyCPU, false);
                return true;
            }

            return false;
        }

        /// <summary>用显式 Arch/Prefer32Bit 派生目标（Runtime/Version 已定）。</summary>
        public IlTarget With(Architecture? arch = null, bool? prefer32Bit = null)
        {
            return new IlTarget(Runtime, Version, arch ?? Arch, prefer32Bit ?? Prefer32Bit);
        }

        /// <summary>netcore 运行时用的 runtimeconfig.json（framework-dependent）。netfx 不写。
        /// <paramref name="probingPaths"/> 非空时注入 additionalProbingPaths（S6 managed 共享：宿主按附加探路
        /// 解析 SDK 共享目录中的预编译系统库，app 目录优先、探路兜底）。</summary>
        public string GetRuntimeConfigJson(IReadOnlyList<string>? probingPaths = null)
        {
            var version = $"{Version.Major}.{Version.Minor}.0";
            var json = "{\n" +
                       "  \"runtimeOptions\": {\n" +
                       $"    \"tfm\": \"{Tfm}\",\n" +
                       "    \"framework\": {\n" +
                       "      \"name\": \"Microsoft.NETCore.App\",\n" +
                       $"      \"version\": \"{version}\"\n" +
                       "    }";
            if (probingPaths is { Count: > 0 })
            {
                json += ",\n    \"additionalProbingPaths\": [\n";
                for (var i = 0; i < probingPaths.Count; i++)
                {
                    json += (i > 0 ? ",\n" : "") + $"      \"{EscapeJson(probingPaths[i])}\"";
                }

                json += "\n    ]";
            }

            return json + "\n  }\n}\n";
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
