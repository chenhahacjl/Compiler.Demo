using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M32 Tier-2 attribute：co 编译器（自举）解析层对拍。
    /// 编译 co 编译器源码 + 驱动，经 co Parser 解析 attribute 源，断言 AttributeList 树形。
    /// </summary>
    public class CoAttributeParseTests
    {
        [Theory]
        [InlineData("[Test] function F(): void\n{\n}\n", "(FunctionDeclaration (AttributeList (Attribute (IdentifierToken \"Test\")))")]
        [InlineData("[Facade(\"System.IntPtr\")] public class NativeInt32\n{\n}\n", "(ClassDeclaration (AttributeList (Attribute (IdentifierToken \"Facade\") (StringToken \"\\\"System.IntPtr\\\"\")))")]
        [InlineData("[Test] function F(): void\n{\n}\n\n[Other] public function G(): i32\n{\n    return 0\n}\n", "(AttributeList (Attribute (IdentifierToken \"Test\")))")]
        public void CoParser_AttributeList_Produced(string source, string expectedFragment)
        {
            var (output, compileErrors) = RunSelfDriver(source);
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            Assert.True(output.Contains(expectedFragment), $"expected fragment '{expectedFragment}' not found.\nOUTPUT: {output}");
        }

        private static (string Output, List<string> CompileErrors) RunSelfDriver(string source)
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource(source)));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return ("", result.Diagnostics.Select(d => d.Message).ToList());
                }

                return (writer.ToString().Replace("\r\n", "\n"), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string BuildDriverSource(string source)
        {
            var embedded = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return
                "using Cocoa.CodeAnalysis.Syntax\nusing System\n\n" +
                "function Main(): i32\n{\n" +
                "    let p = Cocoa.CodeAnalysis.Syntax.Parser.Create(\"" + embedded + "\")\n" +
                "    let ri = p.ParseCompilationUnit()\n" +
                "    System.Console.WriteLine(ri.Dump())\n" +
                "    System.Console.WriteLine(\"DIAG:\" + string(p.DiagnosticCount()))\n" +
                "    return 0\n" +
                "}\n";
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
    }
}