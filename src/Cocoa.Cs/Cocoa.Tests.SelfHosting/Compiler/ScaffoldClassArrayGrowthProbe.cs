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
    public class ScaffoldClassArrayGrowthProbe
    {
        [Theory]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(16)]
        [InlineData(31)]
        [InlineData(32)]
        [InlineData(33)]
        [InlineData(64)]
        public void SelfBinder_ClassGrowth_Reachable(int n)
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .Where(f => f.Replace('\\', '/').Contains("/Binding/") ||
                            f.Replace('\\', '/').Contains("/Syntax/") ||
                            f.Replace('\\', '/').Contains("/Symbols/"))
                .OrderBy(f => f, StringComparer.Ordinal);

            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var f in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(f)));
            }

            var inner = new System.Text.StringBuilder();
            inner.Append("class V { private field g: i32 }").AppendLine();
            for (var i = 0; i < n; i++)
            {
                inner.Append("class C").Append(i).AppendLine(" {");
                inner.Append("    private field f0: i32").AppendLine();
                inner.Append("    private field f1: string").AppendLine();
                inner.Append("    private field f2: i32[]").AppendLine();
                for (var m = 0; m < 12; m++)
                {
                    inner.Append("    public function M").Append(m).Append("(a: i32, b: string): V").AppendLine(" {");
                    inner.Append("        return new V()").AppendLine();
                    inner.AppendLine("    }");
                }

                inner.AppendLine("}");
            }

            var esc = inner.ToString()
                .Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
            var main = "using System\n" +
                "function Main(): i32\n{\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(\"" + esc + "\")\n" +
                "    b.BindCompilationUnit()\n" +
                "    System.Console.WriteLine(\"DC:\" + string(b.DiagnosticCount()))\n" +
                "    var i = 0\n" +
                "    while i < b.GetClassCount()\n    {\n" +
                "        System.Console.WriteLine(\"C\" + string(i) + \"=\" + string(b.GetClassFieldCount(i)))\n" +
                "        i = i + 1\n    }\n" +
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
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "diag: " + diag);
                Assert.NotNull(result.Value);
                Assert.Equal(0, (int)result.Value!);
                Assert.Contains($"C0=1", output);
                for (var i = 0; i < n; i++)
                {
                    Assert.Contains($"C{i + 1}=3", output);
                }
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    [Fact]
        public void SelfBinder_RealCorpus_AllClassesReachable()
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

            var allSrc = string.Join(Environment.NewLine, files.Select(f => File.ReadAllText(f)));
            var main = "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(args[0])\n" +
                "    b.BindCompilationUnit()\n" +
                "    System.Console.WriteLine(\"DC:\" + string(b.DiagnosticCount()))\n" +
                "    var i = 0\n" +
                "    while i < b.GetClassCount()\n    {\n" +
                "        System.Console.WriteLine(\"C\" + string(i) + \"=\" + string(b.GetClassFieldCount(i)))\n" +
                "        i = i + 1\n    }\n" +
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
                var result = compilation.Evaluate(new[] { allSrc }, new Dictionary<VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "diag: " + diag);
                Assert.NotNull(result.Value);
                Assert.Equal(0, (int)result.Value!);
                Assert.Contains("DC:0", output);
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    [Fact]
        public void SelfDriver_BuildDllHex_FullCorpus()
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

            var allSrc = string.Join(Environment.NewLine, files.Select(f => File.ReadAllText(f)))
                + Environment.NewLine + "function Main(): i32 { return 0 }" + Environment.NewLine;
            var main = "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])\n" +
                "    var sl = h.Length\n" +
                "    if sl > 300 { sl = 300 }\n" +
                "    System.Console.WriteLine(\"HEAD:\" + h.substring(0, sl))\n" +
                "    System.Console.WriteLine(\"LEN:\" + string(h.Length))\n" +
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
                var result = compilation.Evaluate(new[] { allSrc }, new Dictionary<VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                if (output.Contains("GCDB"))
                {
                    var gc = output.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("GCDB", StringComparison.Ordinal));
                    throw new Xunit.Sdk.XunitException("GCDB OOB hit: index " + gc + "\nfull out:\n" + output.Substring(0, Math.Min(output.Length, 800)));
                }

                Assert.True(result.Diagnostics.HasErrors() == false, "diag: " + diag);
                Assert.NotNull(result.Value);
                Assert.Equal(0, (int)result.Value!);
                var headLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("HEAD:", StringComparison.Ordinal));
                Assert.NotNull(headLine);
                var head = headLine![5..];
                if (head.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    throw new Xunit.Sdk.XunitException("BuildDllHex 失败: " + head);
                }

                var lenLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("LEN:", StringComparison.Ordinal));
                Assert.NotNull(lenLine);
                var len = int.Parse(lenLine![4..].Trim());
                Assert.True(len > 200000, "B1 hex 过短 (" + len + ")，疑似 ERR 而非完整 DLL");
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}