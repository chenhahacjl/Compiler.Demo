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
    /// A 可行性探针 2：co 编译器（自持）对方法组/函数引用的支持——
    /// 编译 co 编译器源码 + 驱动（`let f = Add; f(41)`），Evaluator 求值断言。
    /// </summary>
    public class CoFunctionReferenceProbe
    {
        [Fact]
        public void CoCompiler_MethodGroup_Callable()
        {
            var (output, exitValue, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            Assert.Equal(42, exitValue);
        }

        private static (string Output, int ExitValue, List<string> CompileErrors) RunSelfDriver()
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(DriverSource()));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return ("", -1, result.Diagnostics.Select(d => d.Message).ToList());
                }

                return (writer.ToString().Replace("\r\n", "\n"), result.Value is int i ? i : -1, new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string DriverSource() => @"using System

function Add(a: i32): i32
{
    return a + 1
}

function Main(): i32
{
    let f = Add
    return f(41)
}
";

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