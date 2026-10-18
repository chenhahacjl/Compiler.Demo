using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// S1（native-s1 Phase A）：consumer 差分验证——同一程序「内联 vs 拆分」（<c>splitRuntime</c>）产物
    /// 行为等价。拆分形态：exe 对运行时原语的调用改走 IAT 导入 <c>System.Core.Native.dll</c>（对应产物由
    /// <see cref="EmitNativeLibrary"/> 生成并复制到 exe 目录），触发 Alloc/NewArray/Concat/Print 等原语
    /// 与 StackOverflow 陷阱点 / ExitProcess 的跨模块路径。
    /// </summary>
    public class NativeSplitDifferentialTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Windows, Architecture.X64);

        private const string AppSource = @"using System

function Main(): i32
{
    var s = ""a"" + ""b"" + ""c""
    var failed: i32 = 0
    if !(s is string) { failed = 1 }
    if s != ""abc"" { failed = 2 }
    var lit = ""literal""
    if !(lit is string) { failed = 3 }
    if lit.GetType().Name != ""String"" { failed = 4 }
    Console.WriteLine(""ok"")
    return failed
}
";

        private static (int ExitCode, string Stdout) EmitInlineAndRun(string dir)
        {
            var exe = Path.Combine(dir, "app-inline.exe");
            var syntaxTree = SyntaxTree.Parse(AppSource);
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("app-inline", exe, Platform);
            Assert.True(!diagnostics.HasErrors(), "inline diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            return Run(exe);
        }

        private static (int ExitCode, string Stdout) EmitSplitAndRun(string dir)
        {
            // 1) 由任一 coa（此处自包含夹具）产出 System.Core.Native.dll（含全部 cocoa_rt_* 原语）
            var libDir = Path.Combine(Path.GetTempPath(), "cocoa-native-split-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(libDir);
            var coaPath = Path.Combine(libDir, "Lib.coa");
            var libDllDir = Path.Combine(dir);
            var libDll = Path.Combine(libDllDir, "System.Core.Native.dll");
            var libSyntax = SyntaxTree.Parse(FixtureSource);
            var libDiags = Compilation.Create(libSyntax).EmitCoa("Lib", coaPath);
            Assert.True(!libDiags.HasErrors(), "fixture coa diags: " + string.Join(" | ", libDiags.Select(d => d.Message)));
            var external = SystemLibrary.Load();
            var coa = CoaSerializer.Load(coaPath, external);
            EmitNativeLibrary.Emit(coa, libDll, Platform);
            Assert.True(File.Exists(libDll));

            // 2) exe 以 splitRuntime 发射（原语导入 System.Core.Native）
            var exe = Path.Combine(dir, "app-split.exe");
            var syntaxTree = SyntaxTree.Parse(AppSource);
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("app-split", exe, Platform, splitRuntime: true);
            Assert.True(!diagnostics.HasErrors(), "split diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            return Run(exe);
        }

        private const string FixtureSource = @"
namespace Lib {
    public function DoNothing(): i32
    {
        return 0
    }
}
";

        private static (int ExitCode, string Stdout) Run(string exe)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new TimeoutException("native exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            return (process.ExitCode, stdout);
        }

        [Fact]
        public void Split_Behaves_Identically_To_Inline()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-split-diff");
            Directory.CreateDirectory(directory);

            var (inlineExit, inlineOut) = EmitInlineAndRun(directory);
            var (splitExit, splitOut) = EmitSplitAndRun(directory);

            Assert.Equal(inlineExit, splitExit);
            Assert.Equal(inlineOut, splitOut);
        }

        // ------------------------------------------------------------------
        // S3：真实 System.Core.coa → System.Core.Native.dll 全部分拆（系统函数 + 运行时原语均导入），
        // 与内联差分行为等价（GuessNumber 冒烟：Console/Math/字符串往返）。
        // ------------------------------------------------------------------

        private const string RealAppSource = @"using System

function Main(): i32
{
    var x = Math.Abs(-5.5)
    Console.WriteLine(x)
    var s = ""a"" + ""b"" + ""c""
    Console.WriteLine(s)
    Console.WriteLine(42)
    if !(s is string) return 1
    var guess = 7
    var target = 42
    while guess < target
    {
        guess = guess * 2
    }
    Console.WriteLine(guess)
    return 0
}
";

        [Fact]
        public void RealSystemCore_FullySplit_BehavesIdentically_ToInlinedSystemCore()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-native-s3-realsplit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            // owner = 真实 System.Core.Native.dll（含运行时 + System.Core 库体）
            var core = SystemLibrary.Load().Single(l => l.Name.StartsWith("System.Core", StringComparison.Ordinal));
            EmitNativeLibrary.Emit(core, Path.Combine(dir, "System.Core.Native.dll"), Platform);

            // 内联形态（默认）
            var inlineExe = Path.Combine(dir, "app-inline.exe");
            var inlineDiags = Compilation.Create(SyntaxTree.Parse(RealAppSource)).EmitNative("app-inline", inlineExe, Platform);
            Assert.True(!inlineDiags.HasErrors(), "inline: " + string.Join(" | ", inlineDiags.Select(d => d.Message)));
            var inline = Run(inlineExe);

            // 全部分拆形态：linkCoaDynamically（System.Core 外链）+ splitRuntime（原语外链）
            var splitExe = Path.Combine(dir, "app-split.exe");
            var splitDiags = Compilation.Create(new string[0], linkCoaDynamically: true, SyntaxTree.Parse(RealAppSource))
                .EmitNative("app-split", splitExe, Platform, splitRuntime: true);
            Assert.True(!splitDiags.HasErrors(), "split: " + string.Join(" | ", splitDiags.Select(d => d.Message)));
            var split = Run(splitExe);

            Assert.Equal(inline.ExitCode, split.ExitCode);
            Assert.Equal(inline.Stdout, split.Stdout);
            Assert.True(new FileInfo(splitExe).Length > 0);
        }
    }
}