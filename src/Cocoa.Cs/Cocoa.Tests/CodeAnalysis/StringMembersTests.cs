using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;
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
    /// String 成员补齐（自举缺口 P0-6，M0-4 第一批）：源码集成 `src/Cocoa.SDK/System.Core/String.co`
    /// （System.Core.coa 重建受 Exception.co 实例类门禁阻断，故走源码编译路径），
    /// 锁定 IndexOf(char)/LastIndexOf/TrimStart·End/ToCharArray/Remove/Insert/Join × 三后端（Evaluator/IL/native x64）。
    /// </summary>
    public class StringMembersTests
    {
        private static readonly string MainSource = @"using System

function Main(): i32
{
    var s = ""hello world""
    Console.WriteLine(s.IndexOf('o'))
    Console.WriteLine(s.IndexOf('z'))
    Console.WriteLine(s.LastIndexOf('o'))
    Console.WriteLine(s.LastIndexOf(""lo""))
    Console.WriteLine(""  hi"".TrimStart(' '))
    Console.WriteLine(""hi!!"".TrimEnd('!'))
    var chars = s.ToCharArray()
    Console.WriteLine(chars.Length)
    Console.WriteLine(string(chars[4]))
    Console.WriteLine(s.Remove(5, 6))
    Console.WriteLine(s.Insert(5, "" CO""))
    var parts = new string[3]
    parts[0] = ""a""
    parts[1] = ""b""
    parts[2] = ""c""
    Console.WriteLine(String.Join(""-"", parts))
    return 0
}";

        private const string ExpectedOutput = "4\n-1\n7\n3\nhi\nhi\n11\no\nhello\nhello CO world\na-b-c\n";

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
            var stringCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.SDK", "System.Core", "String.co"));
            return ImmutableArray.Create(SyntaxTree.Parse(stringCo), SyntaxTree.Parse(MainSource));
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void Evaluator_StringMembers()
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
        public void IlE2e_StringMembers()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-str-tests", "str-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.Emit("str", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeX64_StringMembers()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-str-tests");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "str-native-" + Guid.NewGuid().ToString("N") + ".exe");

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.EmitNative("str", exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));
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