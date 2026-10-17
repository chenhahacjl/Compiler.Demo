using System;

namespace Cocoa.Targeting
{
    /// <summary>
    /// 目标三元组：OS × Cpu。Abi 由 native 后端按 (Os, Cpu) 直接选择具体描述符（WinX64Abi/SysVAmd64Abi 等），
    /// 不再经枚举中转（见 LirToAssembler）。managed 后端仅用 Os（宿主可用性）与 Cpu（PE 形态）。
    /// </summary>
    public readonly record struct TargetTriple(TargetOS Os, Cpu Cpu)
    {
        public static TargetTriple Default => new(TargetOS.Windows, Cpu.X64);

        public static TargetTriple Create(TargetOS os, Cpu cpu) => new(os, cpu);

        /// <summary>字符串形式：`{os}-{cpu}`（如 `windows-x64`），供 .coproj Condition 的 $(TargetTriple)。</summary>
        public override string ToString()
        {
            var os = Os switch
            {
                TargetOS.Windows => "windows",
                TargetOS.Linux => "linux",
                TargetOS.MacOS => "macos",
                _ => "unknown",
            };
            var cpu = Cpu switch
            {
                Cpu.AnyCPU => "anycpu",
                Cpu.X86 => "x86",
                Cpu.X64 => "x64",
                _ => "unknown",
            };
            return $"{os}-{cpu}";
        }

        /// <summary>解析 `os-cpu`（如 `windows-x64` / `linux-arm64` 预留）；Abi 由 (Os,Cpu) 推导。</summary>
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

            Cpu cpu;
            switch (parts[1].ToLowerInvariant())
            {
                case "anycpu":
                    cpu = Cpu.AnyCPU;
                    break;
                case "x86":
                case "x32":
                case "i386":
                    cpu = Cpu.X86;
                    break;
                case "x64":
                case "amd64":
                    cpu = Cpu.X64;
                    break;
                default:
                    return false;
            }

            triple = Create(os, cpu);
            return true;
        }
    }
}