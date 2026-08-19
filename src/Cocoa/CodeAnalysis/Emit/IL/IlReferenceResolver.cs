using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// 按 IlTarget 解析默认引用程序集：
    /// netcore → 对应版本的共享框架（shared\Microsoft.NETCore.App\X.Y.*）里的 System.Private.CoreLib + System.Console；
    /// netfx   → .NET Framework 4.x 的 mscorlib.dll（Framework64 优先，回退 Framework）。
    /// 返回 null 表示无法定位（调用方应回退到编译进程自身的引用并告警）。
    /// </summary>
    public static class IlReferenceResolver
    {
        private static readonly string[] DotnetRootCandidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
        }.Where(p => p != null).ToArray()!;

        public static string[]? ResolveDefaultReferences(IlTarget target)
        {
            if (target.Runtime == IlRuntime.NetFx)
            {
                var mscorlib = FindMscorlib();
                return mscorlib == null ? null : new[] { mscorlib };
            }

            return ResolveNetCoreReferences(target);
        }

        private static string? FindMscorlib()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET");
            var candidates = new[]
            {
                Path.Combine(root, "Framework64", "v4.0.30319", "mscorlib.dll"),
                Path.Combine(root, "Framework", "v4.0.30319", "mscorlib.dll"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string[]? ResolveNetCoreReferences(IlTarget target)
        {
            foreach (var dotnetRoot in DotnetRootCandidates)
            {
                if (string.IsNullOrEmpty(dotnetRoot) || !Directory.Exists(dotnetRoot))
                {
                    continue;
                }

                var shared = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared))
                {
                    continue;
                }

                var prefix = $"{target.Version.Major}.{target.Version.Minor}.";
                var versions = Directory.EnumerateDirectories(shared)
                    .Select(Path.GetFileName)
                    .Where(n => n != null && n.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(n => n!)
                    .OrderByDescending(n => n, StringComparer.Ordinal)
                    .ToArray();
                if (versions.Length == 0)
                {
                    continue;
                }

                var coreLib = Path.Combine(shared, versions[0], "System.Private.CoreLib.dll");
                var console = Path.Combine(shared, versions[0], "System.Console.dll");
                if (File.Exists(coreLib) && File.Exists(console))
                {
                    return new[] { coreLib, console };
                }
            }

            return null;
        }
    }
}
