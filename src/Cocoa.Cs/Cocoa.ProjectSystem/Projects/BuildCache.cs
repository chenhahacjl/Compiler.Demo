using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.ProjectSystem
{
    public static class BuildCache
    {
        /// <summary>集中式状态目录名（仿 C# 解决方案级 .vs/）。</summary>
        public const string CacheDirectoryName = ".cocoa";

        public static string GetDefaultCacheRoot(string anchorDirectory)
        {
            return Path.Combine(anchorDirectory, CacheDirectoryName);
        }

        /// <summary>按项目相对路径在缓存根下分层，避免多项目同名冲突。</summary>
        public static string GetCachePath(string cacheRoot, string projectDirectory, string projectName)
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(cacheRoot)!, projectDirectory);
            if (relative == "." || relative.Length == 0)
            {
                return Path.Combine(cacheRoot, projectName + ".cache");
            }

            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Array.IndexOf(parts, "..") >= 0)
            {
                return Path.Combine(cacheRoot, Path.GetFileName(projectDirectory), projectName + ".cache");
            }

            return Path.Combine(cacheRoot, relative, projectName + ".cache");
        }

        public static bool IsUpToDate(string cachePath, string fingerprint)
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            var cached = File.ReadAllText(cachePath).Trim();
            return string.Equals(cached, fingerprint, StringComparison.Ordinal);
        }

        public static void Write(string cachePath, string fingerprint)
        {
            var directory = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(cachePath, fingerprint);
        }

        /// <summary>源文件用内容 SHA-256；引用文件用名称 + 大小 + 修改时间（避免每次全量哈希大程序集）。</summary>
        public static string ComputeFingerprint(
            ImmutableArray<string> sourceFiles,
            ImmutableArray<string> referenceFiles,
            ImmutableArray<string> imports,
            ImmutableArray<string> optionTokens)
        {
            var sb = new StringBuilder();

            foreach (var file in sourceFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("S:").Append(file).Append('\n');
                if (File.Exists(file))
                {
                    sb.Append(HashFile(file)).Append('\n');
                }
            }

            foreach (var file in referenceFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("R:").Append(file).Append('\n');
                if (File.Exists(file))
                {
                    var info = new FileInfo(file);
                    sb.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
            }

            foreach (var import in imports.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("I:").Append(import).Append('\n');
            }

            foreach (var option in optionTokens)
            {
                sb.Append("O:").Append(option).Append('\n');
            }

            return Hash(sb.ToString());
        }

        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }
    }
}
