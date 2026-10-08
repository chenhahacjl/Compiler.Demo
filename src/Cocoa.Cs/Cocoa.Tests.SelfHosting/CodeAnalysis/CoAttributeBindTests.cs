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
    /// 6e-M32 Tier-2：co 编译器（自举）Binder 函数级 attribute 查询面对拍。
    /// 驱动 co Binder 绑定带 `[Test]` 的源，断言 GetFunctionAttribute* 结果。
    /// </summary>
    public class CoAttributeBindTests
    {
        [Theory]
        [InlineData("[Test] function F(): void\n{\n}\n", "1|Test|")]
        [InlineData("[Mark(\"a\",1)] function G(): i32\n{\n    return 0\n}\n", "1|Mark|\"a\",1")]
        public void CoBinder_FunctionAttributes_Queried(string source, string expected)
        {
            var (output, compileErrors) = RunSelfDriver(source);
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            Assert.Equal(expected, output);
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

                return (writer.ToString().Replace("\r\n", "\n").TrimEnd('\n'), new List<string>());
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
                "using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing System\n\n" +
                "function Main(): i32\n{\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(\"" + embedded + "\")\n" +
                "    b.BindCompilationUnit()\n" +
                "    System.Console.WriteLine(string(b.GetFunctionAttributeCount(0)) + \"|\" + b.GetFunctionAttributeName(0, 0) + \"|\" + b.GetFunctionAttributeArgText(0, 0))\n" +
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