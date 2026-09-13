using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
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
    /// 源码集成 `src/Cocoa.Co/Lexer/Lexer.co`（M7-a0 起结构化 Token + Describe() 输出）。
    /// M7-a1 起补 token 面测试（TokenSurfaceProgram）：verbatim/raw/插值字符串、@ident、
    /// 0b/_/数字后缀、/// 注释、三字符运算符（&lt;&lt;= &gt;&gt;= ??=）、?. ?? -&gt; .. 等、非法字符 Error token。
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

        private const string TokenSurfaceProgram = @"function Test()
{
    var v = @""verbatim""
    var r = """"""raw""""""
    var i = $""x={1}""
    var n = 0b1010_1
    var d = 1_000.5
    var l = 42L
    var u = 1ul
    var f = 1.5f
    @ident = a <<= b
    c >>= d
    e ??= f
    g?.h
    i ?? j
    k -> l
    m..n
    bad = #
    brk = ""abc
}";

        private static string MainSource(string program)
        {
            var embedded = program.Replace("\r\n", "\n")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");
            return "using MiniLexer\nusing System\n\nfunction Main(): i32\n{\n    let lex = MiniLexer.Lexer.Create(\"" + embedded + "\")\n    while true\n    {\n        let t = lex.Next()\n        System.Console.WriteLine(t.Describe())\n        if t.Kind() == \"EOF\"\n        {\n            break\n        }\n    }\n    return 0\n}";
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
            "EOF  14:2\n";

        private const string TokenSurfaceExpected =
            "Keyword function 1:1\n" +
            "Identifier Test 1:10\n" +
            "Symbol ( 1:14\n" +
            "Symbol ) 1:15\n" +
            "Symbol { 2:1\n" +
            "Keyword var 3:5\n" +
            "Identifier v 3:9\n" +
            "Symbol = 3:11\n" +
            "VerbatimString @\"verbatim\" 3:13\n" +
            "Keyword var 4:5\n" +
            "Identifier r 4:9\n" +
            "Symbol = 4:11\n" +
            "RawString \"\"\"raw\"\"\" 4:13\n" +
            "Keyword var 5:5\n" +
            "Identifier i 5:9\n" +
            "Symbol = 5:11\n" +
            "InterpolatedString $\"x={1}\" 5:13\n" +
            "Keyword var 6:5\n" +
            "Identifier n 6:9\n" +
            "Symbol = 6:11\n" +
            "Number 0b1010_1 6:13\n" +
            "Keyword var 7:5\n" +
            "Identifier d 7:9\n" +
            "Symbol = 7:11\n" +
            "Number 1_000.5 7:13\n" +
            "Keyword var 8:5\n" +
            "Identifier l 8:9\n" +
            "Symbol = 8:11\n" +
            "Number 42L 8:13\n" +
            "Keyword var 9:5\n" +
            "Identifier u 9:9\n" +
            "Symbol = 9:11\n" +
            "Number 1ul 9:13\n" +
            "Keyword var 10:5\n" +
            "Identifier f 10:9\n" +
            "Symbol = 10:11\n" +
            "Number 1.5f 10:13\n" +
            "Identifier @ident 11:5\n" +
            "Symbol = 11:12\n" +
            "Identifier a 11:14\n" +
            "Symbol <<= 11:16\n" +
            "Identifier b 11:20\n" +
            "Identifier c 12:5\n" +
            "Symbol >>= 12:7\n" +
            "Identifier d 12:11\n" +
            "Identifier e 13:5\n" +
            "Symbol ??= 13:7\n" +
            "Identifier f 13:11\n" +
            "Identifier g 14:5\n" +
            "Symbol ?. 14:6\n" +
            "Identifier h 14:8\n" +
            "Identifier i 15:5\n" +
            "Symbol ?? 15:7\n" +
            "Identifier j 15:10\n" +
            "Identifier k 16:5\n" +
            "Symbol -> 16:7\n" +
            "Identifier l 16:10\n" +
            "Identifier m 17:5\n" +
            "Symbol .. 17:6\n" +
            "Identifier n 17:8\n" +
            "Identifier bad 18:5\n" +
            "Symbol = 18:9\n" +
            "Error # 18:11\n" +
            "Identifier brk 19:5\n" +
            "Symbol = 19:9\n" +
            "String \"abc 19:11\n" +
            "Symbol } 20:1\n" +
            "EOF  20:2\n";

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

        private static ImmutableArray<SyntaxTree> BuildTrees(string program)
        {
            var lexerCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Lexer", "Lexer.co"));
            return ImmutableArray.Create(SyntaxTree.Parse(lexerCo), SyntaxTree.Parse(MainSource(program)));
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static void RunEvaluator(string program, string expected)
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), BuildTrees(program).ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static void RunIl(string program, string expected)
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-minilex", "ml-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var compilation = Compilation.Create("Main", References(), BuildTrees(program).ToArray());
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
            Assert.Equal(expected, stdout);
        }

        private static void RunNative(string program, string expected)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-minilex");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "ml-native-" + Guid.NewGuid().ToString("N") + ".exe");

            var compilation = Compilation.Create("Main", References(), BuildTrees(program).ToArray());
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
            Assert.Equal(expected, stdout);
        }

        [Fact]
        public void Evaluator_MiniLexer() => RunEvaluator(LexerProgram, ExpectedOutput);

        [Fact]
        public void IlE2e_MiniLexer() => RunIl(LexerProgram, ExpectedOutput);

        [Fact]
        public void NativeX64_MiniLexer() => RunNative(LexerProgram, ExpectedOutput);

        [Fact]
        public void Evaluator_TokenSurface() => RunEvaluator(TokenSurfaceProgram, TokenSurfaceExpected);

        [Fact]
        public void IlE2e_TokenSurface() => RunIl(TokenSurfaceProgram, TokenSurfaceExpected);

        [Fact]
        public void NativeX64_TokenSurface() => RunNative(TokenSurfaceProgram, TokenSurfaceExpected);
    }
}
