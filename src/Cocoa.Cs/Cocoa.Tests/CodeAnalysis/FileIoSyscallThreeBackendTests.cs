using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
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
    /// 自举缺口 P0-1/P0-2，M0-4 批2：文件 IO / 环境 syscall 三后端锁定（Evaluator/IL/native）。
    /// 经 stdlib 注入的 System.Core.coa（System.IO.File / System.Environment，builtin 背书 syscall）消费。
    /// Y-P0-1 补齐 native 腿（MirToLir.Builtins 原 "G7-④ follow-up batch" 编译期拒绝 → 运行时 helper 接入）：
    /// 文件读写经 ucrtbase 低参 API（_wfopen/fread/fwrite/fclose）+ 手动 UTF-8 编码 / MultiByteToWideChar 解码，
    /// 路径复制补 null 结尾（CO 串无 null 终止）；本测试同时锁住 IL File.Copy 实参/方法签名失配修复。
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

        private static void RunNativeFileRoundTrip(string target)
        {
            // Y-P0-1：native 腿（G7-④ 补齐）——与 Evaluator/IL 同源程序、同期望、同清理
            var path = NewTestPath();
            var source = FileProgram(path);
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-fio3", "fio-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var diagnostics = compilation.EmitNative("fio", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            Assert.True(File.Exists(exePath));

            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(FileExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".bak"));
        }

        private static void RunNativeEnvironment(string target)
        {
            Environment.SetEnvironmentVariable("__G7_FIO3__", "hello_env3");
            try
            {
                TargetPlatform.TryParse(target, out var platform);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(EnvironmentProgram));
                var exePath = Path.Combine(Path.GetTempPath(), "cocoa-fio3", "fio-env-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

                var diagnostics = compilation.EmitNative("fioenv", exePath, platform);
                Assert.Empty(string.Join("\n", diagnostics));

                var stdout = NativeEmitTests.Run(exePath);
                Assert.Equal(EnvironmentExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("__G7_FIO3__", null);
            }
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_FileRoundTrip(string target) => RunNativeFileRoundTrip(target);

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_Environment(string target) => RunNativeEnvironment(target);

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_Utf8RoundTrip(string target)
        {
            // Y-P0-1：UTF-8↔UTF-16 编码路径（MultiByteToWideChar / WideCharToMultiByte）
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fio3", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "u.txt").Replace("\\", "/");
            var source = @"using System
using System.IO

function Main(): i32
{
    let p = """ + path + @"""
    File.WriteAllText(p, ""你好 Cocoa 世界！"" + ""\n"" + ""line2"")
    let content = File.ReadAllText(p)
    System.Console.WriteLine(content == ""你好 Cocoa 世界！\nline2"")
    return 0
}";
            try
            {
                TargetPlatform.TryParse(target, out var platform);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var exePath = Path.Combine(Path.GetTempPath(), "cocoa-fio3", "fio-utf8-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

                var diagnostics = compilation.EmitNative("fioutf8", exePath, platform);
                Assert.Empty(string.Join("\n", diagnostics));

                var stdout = NativeEmitTests.Run(exePath);
                Assert.Equal("True\n", stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
