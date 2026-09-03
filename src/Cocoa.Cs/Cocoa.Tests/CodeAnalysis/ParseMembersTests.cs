using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 自举缺口 P0-6，M0-4 批3：TryParse/Parse 家族补齐（Int64/UInt64/Double）源码集成
    /// `src/Cocoa.SDK/System.Core/{Int64,UInt64,Double}.co`，锁定 × 三后端（Evaluator/IL/native x64）。
    /// IL 腿经 BCL redirect 交叉校验（System.Int64/... TryParse）；Evaluator/Native 腿消费 .co 实现。
    /// </summary>
    public class ParseMembersTests
    {
        private static readonly string MainSource = @"using System

function Main(): i32
{
    var l: i64 = 0
    Console.WriteLine(Int64.TryParse(""1234567890123"", out l))
    Console.WriteLine(l)
    Console.WriteLine(Int64.TryParse(""-9223372036854775808"", out l))
    Console.WriteLine(l == -9223372036854775807 - 1)
    Console.WriteLine(Int64.TryParse(""9223372036854775808"", out l))
    Console.WriteLine(Int64.TryParse(""12ab"", out l))
    Console.WriteLine(Int64.TryParse("""", out l))
    var u: u64 = 0
    Console.WriteLine(UInt64.TryParse(""18446744073709551615"", out u))
    Console.WriteLine(u)
    Console.WriteLine(UInt64.TryParse(""18446744073709551616"", out u))
    Console.WriteLine(UInt64.TryParse(""-1"", out u))
    var d: f64 = 0
    Console.WriteLine(Double.TryParse(""3.14"", out d))
    Console.WriteLine(d)
    Console.WriteLine(Double.TryParse(""-0.5"", out d))
    Console.WriteLine(d)
    Console.WriteLine(Double.TryParse(""1e3"", out d))
    Console.WriteLine(d)
    Console.WriteLine(Double.TryParse(""2.5e-2"", out d))
    Console.WriteLine(d)
    Console.WriteLine(Double.TryParse("".5"", out d))
    Console.WriteLine(d)
    Console.WriteLine(Double.TryParse(""1."", out d))
    Console.WriteLine(d)
    Console.WriteLine(Double.TryParse(""12ab"", out d))
    Console.WriteLine(Double.TryParse(""abc"", out d))
    Console.WriteLine(Double.Parse(""3.14""))
    Console.WriteLine(Double.IsNaN(1.0))
    Console.WriteLine(Double.IsNaN(Double.NaN))
    Console.WriteLine(Double.IsInfinity(Double.PositiveInfinity))
    Console.WriteLine(Double.IsInfinity(Double.NegativeInfinity))
    Console.WriteLine(Double.IsInfinity(1.0))
    Console.WriteLine(Double.IsFinite(Double.PositiveInfinity))
    Console.WriteLine(Double.IsFinite(2.5))
    return 0
}";

        private const string ExpectedOutput = "True\n1234567890123\nTrue\nTrue\nFalse\nFalse\nFalse\n" +
            "True\n18446744073709551615\nFalse\nFalse\n" +
            "True\n3.14\nTrue\n-0.5\nTrue\n1000\nTrue\n0.025\nTrue\n0.5\nTrue\n1\n" +
            "False\nFalse\n3.14\nFalse\nTrue\nTrue\nTrue\nFalse\nFalse\nTrue\n";

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }

        private static ImmutableArray<SyntaxTree> BuildTrees()
        {
            var root = RepoRoot();
            var core = Path.Combine(root, "src", "Cocoa.SDK", "System.Core");
            var builder = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var name in new[] { "Int64.co", "UInt64.co", "Double.co" })
            {
                builder.Add(SyntaxTree.Parse(File.ReadAllText(Path.Combine(core, name))));
            }

            builder.Add(SyntaxTree.Parse(MainSource));
            return builder.ToImmutable();
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void Evaluator_ParseMembers()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ExpectedOutput, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_ParseMembers()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-parse-tests", "parse-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.Emit("parse", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("IL exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(ExpectedOutput, stdout);
        }

        [Fact]
        public void NativeX64_ParseMembers()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-parse-tests");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "parse-native-" + Guid.NewGuid().ToString("N") + ".exe");

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.EmitNative("parse", exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(ExpectedOutput, stdout);
        }
    }
}
