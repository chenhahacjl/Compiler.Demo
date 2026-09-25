using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class ScaffoldChainProbe
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void Chain_MemberCall_Resolves(bool useChild, bool useChildText, bool useSimpleText)
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal);
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var f in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(f)));
            }

            var tiny = new System.Text.StringBuilder();
            tiny.AppendLine("class N {");
            tiny.AppendLine("    public function Child(i: i32): N { return this }");
            tiny.AppendLine("    public function Text(): string { return \"t\" }");
            tiny.AppendLine("}");
            tiny.AppendLine("class E {");
            tiny.AppendLine("    public function ChildCount(): i32 { return 1 }");
            tiny.AppendLine("    public function Child(i: i32): N { return new N() }");
            tiny.AppendLine("}");
            tiny.AppendLine("function Main(): i32 {");
            tiny.AppendLine("    var expr = new E()");
            tiny.AppendLine("    var n0 = expr.ChildCount() > 0 ? expr.Child(0) : null");
            tiny.AppendLine("    var s1 = n0.Text()");
            if (useChild)
            {
                tiny.AppendLine("    var s2 = n0.Child(0).Text()");
            }

            tiny.AppendLine("    return 0");
            tiny.AppendLine("}");

            var esc = tiny.ToString().Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
            var main = "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])\n" +
                "    var sl = h.Length\n" +
                "    if sl > 400 { sl = 400 }\n" +
                "    System.Console.WriteLine(\"HEAD:\" + h.substring(0, sl))\n" +
                "    return 0\n}\n";
            trees.Add(SyntaxTree.Parse(main));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new[] { tiny.ToString() }, new Dictionary<VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "diag: " + diag);
                var headLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("HEAD:", StringComparison.Ordinal));
                Assert.NotNull(headLine);
                var head = headLine![5..];
                if (head.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    throw new Xunit.Sdk.XunitException("链式 BuildDllHex 失败: " + head);
                }
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}