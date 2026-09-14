using Cocoa.CodeAnalysis.Syntax;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量二 M8：自举 Parser 差分基建。
    /// 差分目标 = 规范树 dump（红树递归 `(Kind "text" children...)`），C# 侧据此生成基准，
    /// self-hosted Parser 需产出逐字节一致的同名结构。
    /// 本文件：定义 dump + 用 debug 输出观察 C# 树结构（约束差分格式的"金标准"）。
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

        [Fact]
        public void DumpObservation_Examples()
        {
            string[] corpus =
            {
                "function Main(): i32\n{\n    return 1\n}\n",
                "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n",
                "function Main()\n{\n    var x = 10\n    print(x)\n}\n",
                "let f = (x: i32) => x + 1\n",
            };

            var sb = new StringBuilder();
            foreach (var text in corpus)
            {
                var tree = SyntaxTree.Parse(text);
                sb.AppendLine($"SOURCE: {text.Replace("\n", "\\n")}");
                sb.AppendLine(TreeDump(tree.Root));
                sb.AppendLine();
                sb.AppendLine("[diagnostics: " + (tree.Diagnostics.Any(d => d.IsError) ? "error" : "ok") + "]");
                sb.AppendLine("-----");
            }

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-parser-dump.txt"), sb.ToString());
            _output.WriteLine(sb.ToString());
            Assert.True(true);
        }
    }
}