using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// S2（native-s1）：用户 `.coa` 库跨模块调用——app 显式声明 MathLib 为「外部化库」（不合并体），
    /// native 发射把对 MathLib.Add/Scale 的调用改经 IAT 导入 MathLib.Native.dll（cocoa_+mangle(FnKey)），
    /// 运行时原语仍导入 System.Core.Native.dll（host）。校验：进程内三模块协作、跨模块调用往返正确。
    /// </summary>
    public class NativeLibraryCrossCallTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Windows, Architecture.X64);

        private const string HostSource = @"
namespace Host {
    public function Marker(): i32
    {
        return 0
    }
}
";

        private const string MathLibSource = @"
namespace MathLib {
    public function Add(a: i32, b: i32): i32
    {
        return a + b
    }

    public function Scale(v: i32, k: i32): i32
    {
        return v * k
    }

    public function Greeting(): string
    {
        return ""lib"" + ""!""
    }

    public function IsLib(s: string): i32
    {
        if s is string return 1
        return 0
    }
}
";

        private const string AppSource = @"
function Main(): i32
{
    var r = MathLib.Add(3, 4)
    if r != 7 return 11
    var q = MathLib.Scale(r, 2)
    if q != 14 return 12
    if MathLib.Add(100, 1) != 101 return 13

    // 用户库经导入原语创建字符串 → 跨模块身份/取值一致
    var s = MathLib.Greeting()
    if !(s is string) return 21
    if s != ""lib!"" return 22
    if s.GetType().Name != ""String"" return 23

    // 用户库内 is string（库侧经 Cocoa_Rt_type_get 取身份）
    if MathLib.IsLib(s) != 1 return 24
    return 0
}
";

        [Fact]
        public void UserLibrary_CrossModule_Calls()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-s2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var external = SystemLibrary.Load();
            EmitCoa(HostSource, "Host", Path.Combine(directory, "Host.coa"));
            EmitCoa(MathLibSource, "MathLib", Path.Combine(directory, "MathLib.coa"));

            // host = System.Core.Native.dll（承载运行时原语 + 共享类型表）
            EmitNativeLibrary.Emit(CoaSerializer.Load(Path.Combine(directory, "Host.coa"), external), Path.Combine(directory, "System.Core.Native.dll"), Platform);
            // 用户库 = MathLib.Native.dll（全公共导出；不内嵌原语/类型表，共享 host 运行时）
            EmitNativeLibrary.Emit(CoaSerializer.Load(Path.Combine(directory, "MathLib.coa"), external), Path.Combine(directory, "MathLib.Native.dll"), Platform, embedRuntime: false);

            var syntaxTree = SyntaxTree.Parse(AppSource);
            var compilation = Compilation.Create(new[] { Path.Combine(directory, "MathLib.coa") }, syntaxTree);
            compilation.ExternalNativeLibraries = ImmutableHashSet.Create("MathLib");
            var exePath = Path.Combine(directory, "app.exe");
            var diagnostics = compilation.EmitNative("app", exePath, Platform, splitRuntime: true);
            Assert.True(!diagnostics.HasErrors(), "diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            // 校验 exe 导入表含 MathLib.Native + System.Core.Native
            var image = File.ReadAllBytes(exePath);
            var reader = PeImageReader.TryOpen(image);
            Assert.NotNull(reader);
            var importDir = reader!.GetDirectory(PeDataDirectoryEntry.Import);
            var imports = ImportTableReader.ReadAt(image, reader.RvaToFileOffset, importDir.VirtualAddress, importDir.Size);
            var dlls = imports.Select(i => i.DllName).ToArray();
            Assert.Contains(dlls, d => d.IndexOf("MathLib.Native", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(dlls, d => d.IndexOf("System.Core.Native", StringComparison.OrdinalIgnoreCase) >= 0);

            var (exitCode, _) = Run(exePath);
            Assert.Equal(0, exitCode);
        }

        private static void EmitCoa(string source, string name, string coaPath)
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(source)).EmitCoa(name, coaPath);
            Assert.True(!diagnostics.HasErrors(), $"{name} coa diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
        }

        private static (int ExitCode, string Stdout) Run(string exe)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new TimeoutException("native exe did not exit in time.");
            }

            outputTask.Wait();
            return (process.ExitCode, Encoding.Unicode.GetString(output.ToArray()));
        }
    }
}