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

        [Fact]
        public void DumpObservation_Examples()
        {
            string[] corpus =
            {
                "function Main(): i32\n{\n    return 1\n}\n",
                "function Main()\n{\n    if x > 10\n    {\n        print(x)\n    }\n    else\n    {\n        print(0)\n    }\n}\n",
                "function Main()\n{\n    while i < 10\n    {\n        i = i + 1\n    }\n}\n",
                "function Main()\n{\n    for i = 0 to 10 step 2\n    {\n        print(i)\n    }\n}\n",
                "class Foo extends Bar, IA\n{\n    private field _x: i32\n    public property P: i32 { get set }\n    public function Get(): i32\n    {\n        return _x\n    }\n}\n",
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
            return "using MiniParser\nusing System\n\nfunction Main(): i32\n{\n    let p = MiniParser.Parser.Create(\"" + embedded + "\")\n    let root = p.ParseCompilationUnit()\n    System.Console.WriteLine(root.Dump())\n    return 0\n}";
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string SelfDump(string source)
        {
            var embedded = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var lexerCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Lexer", "Lexer.co"));
            var parserCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Parser", "Parser.co"));
            var trees = ImmutableArray.Create(
                SyntaxTree.Parse(lexerCo),
                SyntaxTree.Parse(parserCo),
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
