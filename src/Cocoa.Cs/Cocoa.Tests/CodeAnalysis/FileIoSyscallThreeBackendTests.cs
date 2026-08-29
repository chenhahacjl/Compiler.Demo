using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
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
    /// 自举缺口 P0-1/P0-2，M0-4 批2：文件 IO / 环境 syscall 双后端锁定（Evaluator/IL）。
    /// 经 stdlib 注入的 System.Core.cod（System.IO.File / System.Environment，builtin 背书 syscall）消费。
    /// 注：native 侧整个文件/环境 syscall 族尚未接入（BoundTreeToIr.Builtins "G7-④ follow-up batch" 编译期拒绝），
    /// 待后续批次；本测试同时锁住 IL File.Copy 实参/方法签名失配修复（原 3 参解析只压 2 实参 → InvalidProgramException）。
    /// </summary>
    public class FileIoSyscallThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string NewTestPath()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fio3", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "t.txt").Replace("\\", "/");
        }

        private static string FileProgram(string path) => @"using System
using System.IO

function Main(): i32
{
    let p = """ + path + @"""
    File.Delete(p)
    File.WriteAllText(p, ""hello G7"")
    System.Console.WriteLine(File.Exists(p))
    System.Console.WriteLine(File.ReadAllText(p))
    File.Copy(p, p + "".bak"")
    System.Console.WriteLine(File.Exists(p + "".bak""))
    File.Delete(p)
    File.Delete(p + "".bak"")
    System.Console.WriteLine(File.Exists(p))
    return 0
}";

        private const string FileExpected = "True\nhello G7\nTrue\nFalse\n";

        private static string EnvironmentProgram => @"using System

function Main(): i32
{
    System.Console.WriteLine(Environment.GetEnvironmentVariable(""__G7_FIO3__""))
    System.Console.WriteLine(Environment.GetCurrentDirectory().Length > 0)
    return 0
}";

        private const string EnvironmentExpected = "hello_env3\nTrue\n";

        [Fact]
        public void Evaluator_FileRoundTrip()
        {
            var path = NewTestPath();
            var source = FileProgram(path);
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(FileExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".bak"));
        }

        [Fact]
        public void IlE2e_FileRoundTrip()
        {
            var path = NewTestPath();
            var source = FileProgram(path);
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-fio3", "fio-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.Emit("fio", References(), exePath, IlTarget.Parse("net9.0"));
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
            Assert.Equal(FileExpected, stdout);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".bak"));
        }

        [Fact]
        public void Evaluator_Environment()
        {
            Environment.SetEnvironmentVariable("__G7_FIO3__", "hello_env3");
            try
            {
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);

                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(EnvironmentProgram));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(EnvironmentExpected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("__G7_FIO3__", null);
            }
        }

        [Fact]
        public void IlE2e_Environment()
        {
            Environment.SetEnvironmentVariable("__G7_FIO3__", "hello_env3");
            try
            {
                var exePath = Path.Combine(Path.GetTempPath(), "cocoa-fio3", "fio-env-il-" + Guid.NewGuid().ToString("N") + ".exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(EnvironmentProgram));
                var diagnostics = compilation.Emit("fioenv", References(), exePath, IlTarget.Parse("net9.0"));
                Assert.Empty(string.Join("\n", diagnostics));

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
                Assert.Equal(EnvironmentExpected, stdout);
            }
            finally
            {
                Environment.SetEnvironmentVariable("__G7_FIO3__", null);
            }
        }
    }
}
