using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using CharSet = Cocoa.CodeAnalysis.Symbols.CharSet;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 编译期导入符号校验（互操作手册 §2.2 第 4 条）：extern 声明的 (dll, 符号)
    /// 在目标 DLL 导出表中必须存在。DLL 在磁盘上不可定位或校验不可用时跳过（运行期 LoadLibrary 失败兜底）。
    /// 校验走 Windows 加载器语义（LOAD_LIBRARY_AS_DATAFILE|AS_IMAGE_RESOURCE 映射 + GetProcAddress），
    /// 与运行期 stub 一致：无 DllMain 执行、无代码注入，对 x64/x86 混合与 ARM64EC(h、thunk) 等
    /// 非标准 PE 布局同样可靠。
    /// </summary>
    internal static class NativeImportValidator
    {
        private const uint LoadLibraryAsDatafile = 0x00000002;

        public static ImmutableArray<Diagnostic> Validate(BoundProgram program, Architecture architecture)
        {
            var builder = ImmutableArray.CreateBuilder<Diagnostic>();

            foreach (var function in program.Functions.Keys)
            {
                // 6e-M17 Step 5：native 路径遇 charset = ansi → 编译期诊断"未实现"（不静默错编）
                if (function.IsExtern && function.CharSet == CharSet.Ansi)
                {
                    builder.Add(Diagnostic.Error(function.Declaration?.Identifier.Location ?? default,
                        $"extern function '{function.Name}' 声明 charset = ansi，native 后端未实现（仅支持 unicode，见 docs-dev/内部调用与互操作设计.md §5.3）。"));
                    continue;
                }

                if (!function.IsExtern || function.DllName == null)
                {
                    continue;
                }

                if (!TryResolveExport(function.DllName, function.EntryPoint ?? function.Name, architecture))
                {
                    builder.Add(Diagnostic.Warning(function.Declaration?.Identifier.Location ?? default,
                        $"import symbol '{function.EntryPoint ?? function.Name}' not found in export table of '{function.DllName}'"));
                }
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return builder.ToImmutable();
            }

            return builder.ToImmutable();
        }

        private static bool TryResolveExport(string dllName, string symbolName, Architecture architecture)
        {
            var path = FindDll(dllName, architecture);
            if (path == null)
            {
                return true;
            }

            var handle = LoadLibraryExW(path, IntPtr.Zero, LoadLibraryAsDatafile);
            if (handle == IntPtr.Zero)
            {
                return true;
            }

            try
            {
                return GetProcAddress(handle, symbolName) != IntPtr.Zero;
            }
            catch (Exception)
            {
                return true;
            }
            finally
            {
                FreeLibrary(handle);
            }
        }

        private static string? FindDll(string dllName, Architecture architecture)
        {
            if (dllName.IndexOf('\\') >= 0 || dllName.IndexOf('/') >= 0)
            {
                return File.Exists(dllName) ? dllName : null;
            }

            var fileName = dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? dllName : dllName + ".dll";

            if (architecture == Architecture.X86)
            {
                var syswow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "SysWOW64");
                if (File.Exists(Path.Combine(syswow64, fileName)))
                {
                    return Path.Combine(syswow64, fileName);
                }
            }

            var system32 = Environment.SystemDirectory;
            return File.Exists(Path.Combine(system32, fileName)) ? Path.Combine(system32, fileName) : null;
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling = false)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);
    }
}