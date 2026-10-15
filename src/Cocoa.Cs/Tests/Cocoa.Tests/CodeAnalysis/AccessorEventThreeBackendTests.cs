using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 访问器式事件（C# 式）：`event E: T { add {…} remove {…} }`——自定义 add/remove 访问器方法对。
    /// 订阅 `+=`/`-=` 分派到 add_Name/remove_Name（后备字段写仅访问器体内豁免）；触发仍走后备字段。
    /// 三后端一致（add/remove 方法 = 普通方法面 + 事件订阅调用）。
    /// </summary>
    public class AccessorEventThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

delegate Act(): void

public class Button
{
    private field _count: i32
    private field _removed: i32

    event Clicked: Act
    {
        add
        {
            _count = _count + 1
            _Clicked = _Clicked == null ? value : _Clicked + value
        }
        remove
        {
            _removed = _removed + 1
            _Clicked = _Clicked == null ? null : _Clicked - value
        }
    }

    public function Count(): i32 { return _count }
    public function Removed(): i32 { return _removed }

    public function Fire(): void
    {
        Clicked()
    }
}

function PrintA(): void { Console.WriteLine(""A"") }
function PrintB(): void { Console.WriteLine(""B"") }

function Main(): i32
{
    var b = new Button()
    b.Clicked += PrintA
    b.Clicked += PrintB
    b.Clicked += PrintA
    b.Fire()
    Console.WriteLine(b.Count())
    b.Clicked -= PrintA
    b.Fire()
    Console.WriteLine(b.Count())
    Console.WriteLine(b.Removed())
    return 0
}";

        private const string Expected = "A\nB\nA\n3\nA\nB\n3\n1\n";

        // ---- Evaluator ---------------------------------------------------------

        [Fact]
        public void Evaluator_AccessorEvent_AddRemoveCountAndDispatch()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        // ---- IL ---------------------------------------------------------------

        [Fact]
        public void IlE2e_AccessorEvent()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-evt-il-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("t", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));

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

        // ---- Native -----------------------------------------------------------

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_AccessorEvent(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-evt-native-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t-" + target + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.EmitNative("t", exePath, platform);
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));

            var psi = new ProcessStartInfo(exePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            using var errReader = process.StandardError;
            var errTask = errReader.ReadToEndAsync();
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode + " stderr=" + errTask.Result.Replace("\n", " | "));
            Assert.Equal(Expected, stdout);
        }

        // ---- 绑定语义 ----------------------------------------------------------

        [Fact]
        public void Bind_BackingFieldWriteOutsideAccessor_Blocked()
        {
            // 事件后备字段写只能在访问器体内；外部（其它方法/外部类）赋值被拦
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"using System

delegate Act(): void

public class Button
{
    event Clicked: Act
    {
        add { _Clicked = value }
    }

    public function Broken(): void
    {
        _Clicked = null
    }
}

function Main(): i32 { return 0 }")).GetDiagnostics();
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("不能作为值使用"));
        }
    }
}