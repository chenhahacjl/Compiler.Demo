using System;

namespace Cocoa.Targeting
{
    /// <summary>
    /// 目标三元组：OS × Architecture。Abi 由 native 后端按 (Os, Arch) 直接选择具体描述符（WinX64Abi/SysVAmd64Abi 等），
    /// 不再经枚举中转（见 LirToAssembler）。managed 后端仅用 Os（宿主可用性）与 Arch（PE 形态）。
    /// </summary>
    public readonly record struct TargetTriple(TargetOS Os, Architecture Arch)
    {
        public static TargetTriple Default => new(TargetOS.Windows, Architecture.X64);

        public static TargetTriple Create(TargetOS os, Architecture arch) => new(os, arch);

        /// <summary>字符串形式：`{os}-{arch}`（如 `windows-x64`），供 .coproj Condition 的 $(TargetTriple)。</summary>
        public override string ToString()
        {
            var os = Os switch
            {
                TargetOS.Windows => "windows",
                TargetOS.Linux => "linux",
                TargetOS.MacOS => "macos",
                _ => "unknown",
            };
            var arch = Arch switch
            {
                Architecture.AnyCPU => "anycpu",
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                _ => "unknown",
            };
            return $"{os}-{arch}";
        }

        /// <summary>解析 `os-arch`（如 `windows-x64` / `linux-arm64` 预留）；Abi 由 (Os,Arch) 推导。</summary>
        public static bool TryParse(string text, out TargetTriple triple)
        {
            triple = Default;
            if (string.IsNullOrEmpty(text)) return false;

            var parts = text.Split('-');
            if (parts.Length != 2) return false;

            TargetOS os;
            switch (parts[0].ToLowerInvariant())
            {
                case "windows":
                case "win":
                    os = TargetOS.Windows;
                    break;
                case "linux":
                    os = TargetOS.Linux;
                    break;
                case "macos":
                case "mac":
                    os = TargetOS.MacOS;
                    break;
                default:
                    return false;
            }

            Architecture arch;
            switch (parts[1].ToLowerInvariant())
            {
                case "anycpu":
                    arch = Architecture.AnyCPU;
                    break;
                case "x86":
                case "x32":
                case "i386":
                    arch = Architecture.X86;
                    break;
                case "x64":
                case "amd64":
                    arch = Architecture.X64;
                    break;
                default:
                    return false;
            }

            triple = Create(os, arch);
            return true;
        }
    }
}
