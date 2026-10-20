using System;
using System.Diagnostics;
using System.IO;
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
    /// native-s1 陷阱错误径 ABI 对齐加固：除零（i32/i64/u64）与栈溢出在「内联 vs 全拆分
    /// （System.Core.Native.dll）」双形态下行为一致。
    /// 陷阱点经 <c>jcc</c> 进入 DivByZero/StackOverflow（不压返回地址，帧按 ≡8 对齐），拆分形态经本地
    /// thunk <c>jmp [IAT]</c> 保持相同入口 ABI；本类真实触发陷阱点（NativeSplitDifferentialTests 的
    /// 源码并未触发除零/溢出），把「陷阱路径跨模块可解析 + 行为等价」从注释承诺固化为门禁。
    /// </summary>
    public class TrapPathSplitTests
    {
        private const string FixtureSource = @"namespace Lib {
    public function DoNothing(): i32
    {
        return 0
    }
}
";

        private const string DivByZeroMessage = "error: division by zero\n";
        private const string StackOverflowMessage = "error: stack overflow\n";

        /// <summary>先由任一 coa 产出含全部 Cocoa_Rt_* 原语的 System.Core.Native.dll（owner），再以 splitRuntime 发射 exe。</summary>
        private void EnsureSplitLibrary(string dir, TargetPlatform platform)
        {
            var libDll = Path.Combine(dir, "System.Core.Native.dll");
            if (File.Exists(libDll))
            {
                return;
            }

            var coaPath = Path.Combine(dir, "Lib.coa");
            var libSyntax = SyntaxTree.Parse(FixtureSource);
            var libDiags = Compilation.Create(libSyntax).EmitCoa("Lib", coaPath);
            Assert.True(!libDiags.HasErrors(), "fixture coa diags: " + string.Join(" | ", libDiags.Select(d => d.Message)));
            var external = SystemLibrary.Load();
            var coa = CoaSerializer.Load(coaPath, external);
            EmitNativeLibrary.Emit(coa, libDll, platform);
            Assert.True(File.Exists(libDll));
        }

        private (int ExitCode, string Stdout) EmitInline(string dir, string source, string name, TargetPlatform platform)
        {
            var exe = Path.Combine(dir, name + "-inline-" + platform.Arch + ".exe");
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var diagnostics = compilation.EmitNative(name + "-inline", exe, platform);
            Assert.True(!diagnostics.HasErrors(), "inline diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            return Run(exe);
        }

        private (int ExitCode, string Stdout) EmitSplit(string dir, string source, string name, TargetPlatform platform)
        {
            EnsureSplitLibrary(dir, platform);
            var exe = Path.Combine(dir, name + "-split-" + platform.Arch + ".exe");
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var diagnostics = compilation.EmitNative(name + "-split", exe, platform, splitRuntime: true);
            Assert.True(!diagnostics.HasErrors(), "split diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            return Run(exe);
        }

        /// <summary>内联与拆分对比：退出码、stdout（含陷阱消息）全等，且命中预期陷阱消息。</summary>
        private void AssertTrapsAgree(string source, string expectedMessage, int expectedExitCode, Architecture arch)
        {
            var platform = new TargetPlatform(TargetOS.Windows, arch);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-trap-split-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var inline = EmitInline(dir, source, "trap", platform);
                var split = EmitSplit(dir, source, "trap", platform);

                Assert.Equal(inline.ExitCode, split.ExitCode);
                Assert.Equal(inline.Stdout, split.Stdout);

                Assert.Equal(expectedExitCode, inline.ExitCode);
                Assert.Equal(expectedMessage, inline.Stdout);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData(Architecture.X64)]
        [InlineData(Architecture.X86)]
        public void DivZero_Int32_InlineAndSplit_Agree(Architecture arch)
        {
            AssertTrapsAgree(@"using System
function Main()
{
    Console.WriteLine(1 / 0)
}", DivByZeroMessage, 1, arch);
        }

        [Fact]
        public void RealSystemCore_DivZero_X86_FullySplit_MatchesInline()
        {
            // x86 拆分重定位修复（CodeAbsoluteFixups → .reloc 代码侧 HIGHLOW）的直接回归门禁：
            // 真实 System.Core.Native.dll（含运行时 + System.Core 库体）+ exe 全部分拆
            // （linkCoaDynamically + splitRuntime）。除零陷阱经 thunk jmp [IAT] 落入真实库的
            // Cocoa_Rt_div_by_zero，行为须与内联一致（修复前 x86 拆分任何首调即 0xC0000005）。
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-trap-realsplit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var platform = new TargetPlatform(TargetOS.Windows, Architecture.X86);
                const string source = @"using System
function Main()
{
    Console.WriteLine(1 / 0)
}";

                var inline = EmitInline(dir, source, "realsplit", platform);

                var core = SystemLibrary.Load().Single(l => l.Name.StartsWith("System.Core", StringComparison.Ordinal));
                EmitNativeLibrary.Emit(core, Path.Combine(dir, "System.Core.Native.dll"), platform);
                var exe = Path.Combine(dir, "realsplit-split-X86.exe");
                var compilation = Compilation.Create(new string[0], linkCoaDynamically: true, SyntaxTree.Parse(source));
                var diagnostics = compilation.EmitNative("realsplit-split", exe, platform, splitRuntime: true);
                Assert.True(!diagnostics.HasErrors(), "split diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
                var split = Run(exe);

                Assert.Equal(inline.ExitCode, split.ExitCode);
                Assert.Equal(inline.Stdout, split.Stdout);
                Assert.Equal(1, inline.ExitCode);
                Assert.Equal(DivByZeroMessage, inline.Stdout);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData(Architecture.X64)]
        [InlineData(Architecture.X86)]
        public void ModZero_Int32_InlineAndSplit_Agree(Architecture arch)
        {
            AssertTrapsAgree(@"using System
function Main()
{
    Console.WriteLine(19 % 0)
}", DivByZeroMessage, 1, arch);
        }

        [Theory]
        [InlineData(Architecture.X64)]
        [InlineData(Architecture.X86)]
        public void DivZero_Int64_InlineAndSplit_Agree(Architecture arch)
        {
            AssertTrapsAgree(@"using System
function Main()
{
    var a: i64 = 1l
    var b: i64 = 0l
    Console.WriteLine(a / b)
}", DivByZeroMessage, 1, arch);
        }

        [Theory]
        [InlineData(Architecture.X64)]
        [InlineData(Architecture.X86)]
        public void DivZero_UInt64_InlineAndSplit_Agree(Architecture arch)
        {
            AssertTrapsAgree(@"using System
function Main()
{
    var a: u64 = 1ul
    var b: u64 = 0ul
    Console.WriteLine(a / b)
}", DivByZeroMessage, 1, arch);
        }

        [Theory]
        [InlineData(Architecture.X64)]
        [InlineData(Architecture.X86)]
        public void StackOverflow_InlineAndSplit_Agree(Architecture arch)
        {
            AssertTrapsAgree(@"function Recurse(): void
{
    Recurse()
}
function Main()
{
    Recurse()
}", StackOverflowMessage, 1, arch);
        }

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
    }
}