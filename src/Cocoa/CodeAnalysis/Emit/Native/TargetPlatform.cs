using System;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>目标操作系统：决定 PE/ELF/Mach-O 文件格式与运行时导入机制。</summary>
    internal enum TargetOS
    {
        Windows,
        Linux,
        MacOS,
    }

    /// <summary>CPU 架构：决定指令集、寄存器宽度与指针大小。</summary>
    internal enum Architecture
    {
        X64,
        X86,
    }

    /// <summary>代码生成后端：决定产物的类型。</summary>
    internal enum CodeBackend
    {
        DotNet,
        Native,
    }

    /// <summary>原生编译目标平台（仅 Native 后端使用）。</summary>
    internal readonly record struct TargetPlatform(TargetOS OS, Architecture Arch)
    {
        public static TargetPlatform Default => new(TargetOS.Windows, Architecture.X64);

        public static string SupportedTargets =>
            "windows-x64, windows-x86, linux-x64, linux-x86, macos-x64, macos-x86";

        public static bool TryParse(string text, out TargetPlatform platform)
        {
            platform = default;

            var parts = text.Split('-');
            if (parts.Length != 2)
            {
                return false;
            }

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
                case "x64":
                case "amd64":
                    arch = Architecture.X64;
                    break;
                case "x86":
                case "x32":
                case "i386":
                    arch = Architecture.X86;
                    break;
                default:
                    return false;
            }

            platform = new TargetPlatform(os, arch);
            return true;
        }

        public override string ToString()
        {
            var os = OS switch
            {
                TargetOS.Windows => "windows",
                TargetOS.Linux => "linux",
                TargetOS.MacOS => "macos",
                _ => throw new ArgumentOutOfRangeException(nameof(OS)),
            };

            var arch = Arch switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                _ => throw new ArgumentOutOfRangeException(nameof(Arch)),
            };

            return $"{os}-{arch}";
        }
    }
}
