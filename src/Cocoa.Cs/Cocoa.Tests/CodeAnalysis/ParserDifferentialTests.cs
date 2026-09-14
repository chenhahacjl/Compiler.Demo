using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量二 M8：自举 Parser 差分。
    /// 差分口径 = 规范树 dump（红树递归 `(Kind "text" children...)`）逐字节一致：
    /// C# 基准 = <see cref="TreeDump"/>；自举侧 = `MiniParser.Parser.ParseCompilationUnit().Dump()`。
    /// 语料 = 内嵌小集合（M8-a1 语法子集内的有效程序）。
    /// </summary>
    public class ParserDifferentialTests
    {
        private readonly ITestOutputHelper _output;

        public ParserDifferentialTests(ITestOutputHelper output) => _output = output;

        public static string TreeDump(SyntaxNode node)
        {
            if (node is SyntaxToken token)
            {
                return $"({token.Kind} \"{Escape(token.Text)}\")";
            }

            var children = node.GetChildren().ToArray();
            return children.Length == 0
                ? $"({node.Kind})"
                : $"({node.Kind} {string.Join(" ", children.Select(TreeDump))})";
        }

        private static string Escape(string text)
            => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        private static string[] Corpus() => new[]
        {
            "function Main(): i32\n{\n    return 1\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n",
            "function Main()\n{\n    var x = 10\n    print(x)\n}\n",
            "let f = (x: i32) => x + 1\n",
            "function Run(n: i32): void\n{\n    var sum = n * 2 + 1\n    if sum > 10\n    {\n        print(sum)\n    }\n    else\n    {\n        print(0)\n    }\n}\n",
            "var g = !flag && a <= b\n",
            "function Main()\n{\n    print(Add(2, 3))\n}\n",
            "function Main()\n{\n    while i < 10\n    {\n        i = i + 1\n    }\n}\n",
            "function Main()\n{\n    for i = 0 to 10 step 2\n    {\n        print(i)\n    }\n}\n",
            "let r = obj.Get(x) + -1\n",
            "let s = \"hi\" + c\n",
            "function Main()\n{\n    let ok = a && b || !c\n    print(ok)\n}\n",
            "class Foo extends Bar, IA\n{\n    private field _x: i32\n    public property P: i32 { get set }\n    public function Get(): i32\n    {\n        return _x\n    }\n    public constructor(x: i32)\n    {\n        _x = x\n    }\n}\n",
            "struct Point\n{\n    public field X: i32\n    public field Y: i32\n}\n",
            "interface IFoo\n{\n    function Get(): i32\n    property P: i32 { get }\n}\n",
            "function Main()\n{\n    try\n    {\n        run()\n    }\n    catch e: Exception\n    {\n        print(e.Message)\n    }\n    finally\n    {\n        cleanup()\n    }\n}\n",
            "function Main()\n{\n    foreach (var item in list)\n    {\n        print(item)\n    }\n}\n",
            "function Main()\n{\n    for (var i = 0; i < 10; i++)\n    {\n        print(i)\n    }\n}\n",
            "let map: List<i32> = null\n",
            "class Foo\n{\n    private field _x: i32 = 5\n    public property P: i32 { get set } = 42\n}\n",
            "let a = new i32[] {1, 2, 3}\n",
            "let b = x is T\nlet c = a as T\nlet d = e ?? f\n",
            "let arr = new string[] {\"x\", \"y\"}\n",
            "class Point\n{\n    public field X: i32\n    public field Y: i32\n    public function Distance(p: Point): f64\n    {\n        return 0\n    }\n}\n",
            "using System\nusing MiniLexer\n\nfunction Main()\n{\n    print(1)\n}\n",
            "using System.Collections.Generic\n\nlet items = new List<i32>()\n",
            "let p = new Point()\n",
            "using System\nfunction Main()\n{\n    Console.WriteLine(Greeting(\"Cocoa\"))\n    Console.WriteLine(Sum(20, 22))\n}\n",
            "using System\nfunction Main()\n{\n    var total = 0\n    for var i = 1 to 5\n    {\n        if i == 3\n        {\n            continue\n        }\n        total = total + i\n    }\n}\n",
            "function DoOnce(): i32\n{\n    var i = 0\n    do\n    {\n        i = i + 1\n    } while i < 0\n    return i\n}\n",
            "using System\nfunction Run(args: string[])\n{\n    Console.WriteLine(Add(20, 22))\n    Console.WriteLine(args.Length)\n    if args.Length > 0\n    {\n        Console.WriteLine(args[0])\n    }\n}\n",
            "public class Person\n{\n    private field _name: string\n}\n",
            "public enum Color { Red, Green, Blue }\n\npublic enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 }\n",
            "namespace MyLib\n{\n    public class Point\n    {\n        private field _x: i32\n    }\n}\n",
            "class Kernel32\n{\n    import kernel32.dll\n    {\n        static stdcall function GetTickCount(): i32\n    }\n}\n",
            "let a = obj.b.c\nlet d = x.y.z.W()\n",
            "let d: f64 = 3.14\nlet g = f32(0.016)\n",
            "Ui.VStack(gui, () => {\n    gui.Label(\"hi\")\n    Ui.HStack(gui, () => {\n        gui.Button(\"ok\")\n    })\n})\n",
            "function Describe(name: string, age: i32): string\n{\n    var years = age * 1\n    return name + \" (\" + (string)years + \")\"\n}\n",
        };

        [Fact]
        public void SelfParser_Dump_Matches_CShaTreeDump_ForCorpus()
        {
            var failures = new List<string>();
            foreach (var source in Corpus())
            {
                var reference = TreeDump(SyntaxTree.Parse(source).Root);
                var self = SelfDump(source);

                if (self != reference)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC#  : {reference}\nself: {self}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        /// <summary>无效程序语料（M8 错误恢复差分）：C# 与自举 Parser 必须**双方言同报错**；
        /// 自举侧消息文本不与 C# 逐字对齐，仅要求非空可报告（`error:` 前缀）。</summary>
        private static string[] InvalidCorpus() => new[]
        {
            "function Main(\n{\n}\n",
            "let x =\n",
            "function Main()\n{\n    var = 5\n}\n",
            "class Foo extends\n{\n}\n",
            "enum E { A B }\n",
            "function Main()\n{\n    return 1\n",
        };

        [Fact]
        public void SelfParser_ReportsErrors_ForInvalidCorpus()
        {
            var failures = new List<string>();
            foreach (var source in InvalidCorpus())
            {
                var csharpHasErrors = SyntaxTree.Parse(source).Diagnostics.HasErrors();
                var self = SelfDump(source);
                var selfHasErrors = self.Contains("error:");

                if (!csharpHasErrors || !selfHasErrors)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC# HasErrors: {csharpHasErrors}\nself: {self}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        [Fact]
        public void DumpObservation_Examples()
        {
            string[] corpus =
            {
                "class Foo extends Bar, IA\n{\n    private field _x: i32\n    public property P: i32 { get set }\n    public function Get(): i32\n    {\n        return _x\n    }\n    public constructor(x: i32)\n    {\n        _x = x\n    }\n}\n",
                "struct Point\n{\n    public field X: i32\n    public field Y: i32\n}\n",
                "interface IFoo\n{\n    function Get(): i32\n    property P: i32 { get }\n}\n",
                "enum Color\n{\n    Red\n    Green = 5\n    Blue\n}\n",
                "function Main()\n{\n    switch x\n    {\n        case 1:\n            print(1)\n            break\n        default:\n            print(0)\n    }\n}\n",
                "function Main()\n{\n    try\n    {\n        run()\n    }\n    catch e: Exception\n    {\n        print(e.Message)\n    }\n    finally\n    {\n        cleanup()\n    }\n}\n",
                "function Main()\n{\n    foreach (var item in list)\n    {\n        print(item)\n    }\n}\n",
                "let a: i32[] = new i32[] {1, 2, 3}\n",
                "let map: List<i32> = null\n",
                "function Main()\n{\n    for (var i = 0; i < 10; i++)\n    {\n        print(i)\n    }\n}\n",
                "class Foo\n{\n    private field _x: i32 = 5\n    public property P: i32 { get set } = 42\n}\n",
                "let a = new i32[] {1, 2, 3}\n",
                "let b = x is T\nlet c = a as T\nlet d = e ?? f\n",
                "let arr = new string[] {\"x\", \"y\"}\n",
                "class Point\n{\n    public field X: i32\n    public field Y: i32\n    public function Distance(p: Point): f64\n    {\n        return 0\n    }\n}\n",
                "using System\nusing MiniLexer\n\nfunction Main()\n{\n    print(1)\n}\n",
                "using System.Collections.Generic\n\nlet items = new List<i32>()\n",
                "function Main()\n{\n    var total = 0\n    for var i = 1 to 5\n    {\n        if i == 3\n        {\n            continue\n        }\n        total = total + i\n    }\n}\n",
            "using System\nfunction Main()\n{\n    var buf: u8[] = new u8[3]\n    buf[0] = 200\n    buf[1] = 0xFF\n    Console.WriteLine(0xFF)\n}\n",
            "using System\nfunction Main()\n{\n    var b1: u8 = 65\n    var buf: u8[] = new u8[3]\n    buf[0] = 200\n    Console.WriteLine(buf[0])\n    Console.WriteLine(Wrap(200) == Wrap(200))\n}\n",
            "function Wrap(value: i32): u8\n{\n    return (u8)value\n}\n\nfunction AsInt(b: u8): i32\n{\n    return i32(b)\n}\n\nfunction LastElement(buf: u8[]): u8\n{\n    return buf[buf.Length - 1]\n}\n",
            "function Wrap(value: i32): u8\n{\n    return (u8)value\n}\n\nfunction AsInt(b: u8): i32\n{\n    return i32(b)\n}\n\nfunction FirstElement(buf: u8[]): u8\n{\n    return buf[0]\n}\n\nfunction LastElement(buf: u8[]): u8\n{\n    return buf[buf.Length - 1]\n}\n",
        };

            var sb = new StringBuilder();
            foreach (var text in corpus)
            {
                var tree = SyntaxTree.Parse(text);
                sb.AppendLine($"SOURCE: {text.Replace("\n", "\\n")}");
                sb.AppendLine(TreeDump(tree.Root));
                sb.AppendLine("-----");
            }

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-parser-dump.txt"), sb.ToString());
            _output.WriteLine(sb.ToString());
            Assert.True(true);
        }

        [Fact]
        public void SampleCoverage_Sweep()
        {
            var samplesDir = Path.Combine(RepoRoot(), "samples");
            var files = Directory.EnumerateFiles(samplesDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var pass = 0;
            var fail = new List<string>();
            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                if (source.Length == 0) continue;
                source = source.Replace("\r\n", "\n");
                var name = Path.GetRelativePath(samplesDir, file).Replace('\\', '/');
                try
                {
                    var reference = TreeDump(SyntaxTree.Parse(source).Root);
                    var self = SelfDump(source);
                    if (self == reference)
                    {
                        pass++;
                    }
                    else
                    {
                        fail.Add($"{name} (self len {self.Length} vs C# len {reference.Length})");
                    }
                }
                catch (Exception ex)
                {
                    fail.Add($"{name} (exception: {ex.Message})");
                }
            }

            var summary = $"samples: {files.Length}, pass: {pass}, fail: {fail.Count}\n" + string.Join("\n", fail);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-parser-sample-sweep.txt"), summary);
            _output.WriteLine(summary);
            Assert.True(true);
        }

        [Fact]
        public void Debug_FileDiff()
        {
            var files = new[]
            {
                "Tutorial/Basics/Types/main.co",
                "Tutorial/Data/Doubles/main.co",
                "Tutorial/Dialects/CsStyle/lib.co",
                "Samples/UI/BasicUI/main.co",
                "Samples/UI/NativeUI/main.co",
                "Samples/UI/AdvancedUI/main.co",
                "Samples/UI/DeclarativeUI/main.co",
            };

            var sb = new StringBuilder();
            foreach (var rel in files)
            {
                var path = Path.Combine(RepoRoot(), "samples", rel.Replace('/', Path.DirectorySeparatorChar));
                var source = File.ReadAllText(path).Replace("\r\n", "\n");
                var reference = TreeDump(SyntaxTree.Parse(source).Root);
                var self = SelfDump(source);

                sb.AppendLine("===== " + rel + " =====");
                if (reference == self)
                {
                    sb.AppendLine("MATCH");
                    continue;
                }

                var i = 0;
                while (i < reference.Length && i < self.Length && reference[i] == self[i]) i++;
                var start = Math.Max(0, i - 120);
                var len = Math.Min(240, Math.Min(reference.Length, self.Length) - start);
                sb.AppendLine($"first diff @ {i}");
                sb.AppendLine("C#  …" + reference.Substring(start, Math.Max(0, len)) + "…");
                sb.AppendLine("self…" + self.Substring(start, Math.Max(0, len)) + "…");
            }

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-parser-filediff.txt"), sb.ToString());
            _output.WriteLine(sb.ToString());
            Assert.True(true);
        }

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

        private static string MainSource(string embedded)
        {
            return "using MiniParser\nusing System\n\nfunction Main(): i32\n{\n    let p = MiniParser.Parser.Create(\"" + embedded + "\")\n    let root = p.ParseCompilationUnit()\n    System.Console.WriteLine(root.Dump())\n    var i = 0\n    while i < p.DiagnosticCount()\n    {\n        System.Console.WriteLine(p.DiagnosticAt(i))\n        i = i + 1\n    }\n\n    return 0\n}";
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string SelfDump(string source)
        {
            var embedded = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var lexerCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Lexer.co"));
            var tokenCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Token.co"));
            var parserCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Parser.co"));
            var nodeCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Node.co"));
            var trees = ImmutableArray.Create(
                SyntaxTree.Parse(lexerCo),
                SyntaxTree.Parse(tokenCo),
                SyntaxTree.Parse(parserCo),
                SyntaxTree.Parse(nodeCo),
                SyntaxTree.Parse(MainSource(embedded)));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                if (result.Diagnostics.HasErrors())
                {
                    return "COCOMPILE-ERROR: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message));
                }

                return writer.ToString().Replace("\r\n", "\n").TrimEnd('\n');
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}
