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
    /// 自举第 ⑦ 步开工：mini-Lexer 三后端锁定（Evaluator/IL/native x64）。
    /// 源码集成 `src/Cocoa.Cs/Cocoa.Tests/Resources/MiniLexer.co`，内嵌代表性源文本
    /// 覆盖：关键字/标识符、数字（十进制/0x 十六进制/含指数 double）、字符串（含 \n 转义）、
    /// 字符字面量、注释（// 与跨行 /* */）、运算符/标点（两字符最长匹配）、行号/列号、EOF。
    /// </summary>
    public class MiniLexerTests
    {
        private const string LexerProgram = @"function main()
{
    var n = 42
    var h = 0xFF
    var d = 3.14e2
    var s = ""hi\nthere""
    var c = 'q'
    // line comment
    /* block
       comment */
    ok = a <= b && c != d
    e >> 2
    f => g
}";

        private static string MainSource()
        {
            var embedded = LexerProgram.Replace("\r\n", "\n")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");
            return "using MiniLexer\nusing System\n\nfunction Main(): i32\n{\n    let lex = MiniLexer.Lexer.Create(\"" + embedded + "\")\n    while true\n    {\n        let t = lex.Next()\n        System.Console.WriteLine(t)\n        if t == \"EOF\"\n        {\n            break\n        }\n    }\n    return 0\n}";
        }

        private const string ExpectedOutput =
            "Keyword function 1:1\n" +
            "Identifier main 1:10\n" +
            "Symbol ( 1:14\n" +
            "Symbol ) 1:15\n" +
            "Symbol { 2:1\n" +
            "Keyword var 3:5\n" +
            "Identifier n 3:9\n" +
            "Symbol = 3:11\n" +
            "Number 42 3:13\n" +
            "Keyword var 4:5\n" +
            "Identifier h 4:9\n" +
            "Symbol = 4:11\n" +
            "Number 0xFF 4:13\n" +
            "Keyword var 5:5\n" +
            "Identifier d 5:9\n" +
            "Symbol = 5:11\n" +
            "Number 3.14e2 5:13\n" +
            "Keyword var 6:5\n" +
            "Identifier s 6:9\n" +
            "Symbol = 6:11\n" +
            "String \"hi\\nthere\" 6:13\n" +
            "Keyword var 7:5\n" +
            "Identifier c 7:9\n" +
            "Symbol = 7:11\n" +
            "Char 'q' 7:13\n" +
            "Identifier ok 11:5\n" +
            "Symbol = 11:8\n" +
            "Identifier a 11:10\n" +
            "Symbol <= 11:12\n" +
            "Identifier b 11:15\n" +
            "Symbol && 11:17\n" +
            "Identifier c 11:20\n" +
            "Symbol != 11:22\n" +
            "Identifier d 11:25\n" +
            "Identifier e 12:5\n" +
            "Symbol >> 12:7\n" +
            "Number 2 12:10\n" +
            "Identifier f 13:5\n" +
            "Symbol => 13:7\n" +
            "Identifier g 13:10\n" +
            "Symbol } 14:1\n" +
            "EOF\n";

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
            var lexerCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.Tests", "Resources", "MiniLexer.co"));
            return ImmutableArray.Create(SyntaxTree.Parse(lexerCo), SyntaxTree.Parse(MainSource()));
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void Evaluator_MiniLexer()
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
        public void IlE2e_MiniLexer()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-minilex", "ml-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.Emit("ml", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeX64_MiniLexer()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-minilex");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "ml-native-" + Guid.NewGuid().ToString("N") + ".exe");

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.EmitNative("ml", exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));
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
