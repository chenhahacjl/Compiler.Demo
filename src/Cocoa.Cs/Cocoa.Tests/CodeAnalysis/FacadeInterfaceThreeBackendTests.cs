using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// facade interface：Cocoa 接口声明映射 BCL 同名接口（System.IDisposable 等）。
    /// IL 端接口类型/实现/成员调用重定向到 BCL；Evaluator/native 按 Cocoa 接口语义（vtable 分派到实现类）。
    /// </summary>
    public class FacadeInterfaceThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System
[Facade(""System.IDisposable"")] public interface IDisposable
{
    public function Dispose(): void
}

public class Resource extends IDisposable
{
    private field _used: bool

    public function Dispose(): void
    {
        _used = true
    }

    public function WasUsed(): bool
    {
        return _used
    }
}

function Main(): i32
{
    var r = new Resource()
    var d: IDisposable = r
    d.Dispose()
    System.Console.WriteLine(r.WasUsed())
    return 0
}";

        private const string Expected = "True\n";

        [Fact]
        public void Evaluator_FacadeInterface()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_FacadeInterface()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-facade-iface", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "fi.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("fitest", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            using var errReader = process.StandardError;
            var errTask = errReader.ReadToEndAsync();
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode + " stderr=" + errTask.Result.Replace("\n", " | "));
            Assert.Equal(Expected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_FacadeInterface(string target)
        {
            // B3：facade 接口 native 端已支持——接口方法经 vtable 虚分派到实现类（与 Evaluator/IL 三端一致）。
            TargetPlatform.TryParse(target, out var platform);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-facade-iface-native", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "fi-" + target + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("fisdk", exePath, platform);
            Assert.True(!diagnostics.HasErrors(), "got: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath) { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode);
            Assert.Equal(Expected, stdout);
        }
    }
}