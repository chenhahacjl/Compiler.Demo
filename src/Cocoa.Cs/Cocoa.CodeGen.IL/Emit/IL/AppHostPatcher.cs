using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.IL
{
    /// <summary>
    /// netcore apphost 生成器（对齐 dotnet SDK 的 Microsoft.NET.HostModel.AppHost.HostWriter）。
    /// 机制：SDK 自带 apphost.exe 模板内嵌一个 ASCII 占位符串（托管 DLL 相对路径的锚点），
    /// 宿主 apphost.c 编译期将 `embed[] = EMBED_HASH_FULL_UTF8` 初始化；
    /// 构建期把占位符串原地覆写为托管 DLL 的 UTF-8 相对路径 + NUL，运行时宿主 strlen 读出。
    /// 参考：dotnet/runtime apphost.c（读）与 src/installer/managed/Microsoft.NET.HostModel（写）。
    /// </summary>
    public static class AppHostPatcher
    {
        /// <summary>模板中托管 DLL 路径的占位符（SHA-256 of "foobar"，编译进 apphost 数据段）。</summary>
        private const string AppBinaryPathPlaceholder =
            "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

        /// <summary>占位符区可容纳的最大路径长度（UTF-8 字节，不含 NUL）。</summary>
        public const int MaxAppBinaryPathSizeInBytes = 1024;

        /// <summary>补丁 apphost：把模板中的路径占位符替换为托管 DLL 相对路径（UTF-8），剩余区补零。</summary>
        public static void Patch(string templatePath, string outputPath, string appRelativeBinaryPath)
        {
            var pathBytes = Encoding.UTF8.GetBytes(appRelativeBinaryPath);
            if (pathBytes.Length > MaxAppBinaryPathSizeInBytes)
            {
                throw new ArgumentException(
                    $"app binary path '{appRelativeBinaryPath}' is {pathBytes.Length} bytes, exceeding the apphost limit of {MaxAppBinaryPathSizeInBytes} bytes");
            }

            var template = File.ReadAllBytes(templatePath);
            var placeholder = Encoding.ASCII.GetBytes(AppBinaryPathPlaceholder);
            var position = IndexOf(template, placeholder);
            if (position < 0)
            {
                throw new NotSupportedException(
                    $"apphost template '{templatePath}' does not contain the expected placeholder '{AppBinaryPathPlaceholder}'");
            }

            var result = (byte[])template.Clone();
            Array.Copy(pathBytes, 0, result, position, pathBytes.Length);
            for (var i = pathBytes.Length; i < placeholder.Length; i++)
            {
                result[position + i] = 0;
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(outputPath, result);
        }

        /// <summary>
        /// 定位本机 .NET SDK 的 apphost 模板：从运行时目录反推 dotnet 根 → sdk\*\AppHostTemplate\apphost.exe，
        /// 取包含模板的最高 SDK 版本。找不到给明确诊断。
        /// </summary>
        public static string FindDefaultTemplate()
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var dotnetRoot = FindDotnetRoot(runtimeDir);
            if (dotnetRoot == null)
            {
                throw new NotSupportedException(
                    $"cannot locate the .NET SDK apphost template: could not derive the dotnet root from runtime directory '{runtimeDir}'");
            }

            var sdkRoot = Path.Combine(dotnetRoot, "sdk");
            string? best = null;
            Version? bestVersion = null;
            foreach (var sdkDir in SafeEnumerateDirectories(sdkRoot))
            {
                var candidate = Path.Combine(sdkDir, "AppHostTemplate", "apphost.exe");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (!Version.TryParse(Path.GetFileName(sdkDir), out var version))
                {
                    continue;
                }

                if (best == null || bestVersion == null || version > bestVersion)
                {
                    best = candidate;
                    bestVersion = version;
                }
            }

            if (best == null)
            {
                throw new NotSupportedException(
                    $"cannot locate the .NET SDK apphost template: no '<dotnetRoot>/sdk/&lt;version&gt;/AppHostTemplate/apphost.exe' found under '{sdkRoot}'. " +
                    "The .NET SDK is required to generate the native launcher for netcore executables.");
            }

            return best;
        }

        private static string? FindDotnetRoot(string runtimeDirectory)
        {
            // 运行时目录形如 <dotnetRoot>\shared\Microsoft.NETCore.App\<version>\：上溯三级。
            var trimmed = runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var app = Path.GetDirectoryName(trimmed);
            var shared = app == null ? null : Path.GetDirectoryName(app);
            var root = shared == null ? null : Path.GetDirectoryName(shared);
            return string.IsNullOrEmpty(root) ? null : root;
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            if (!Directory.Exists(path))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.EnumerateDirectories(path).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return -1;
            }

            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
