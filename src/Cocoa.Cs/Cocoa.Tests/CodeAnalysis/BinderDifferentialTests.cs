using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量三 M9：自举 Binder 差分。
    /// 差分口径 = 符号表规范化文本（函数行 / 全局变量行 / main 行）逐字节一致：
    /// C# 基准 = <see cref="SymbolDump"/>（经 <see cref="Compilation"/> 公共 API）；
    /// 自举侧 = `MiniBinder.Binder.Create(source).BindCompilationUnit().DescribeSymbols()`。
    /// M9-a1 语料 = 符号声明面内的有效程序（显式类型 + 字面量 var 推断 + 数组/out 参数 + main 判定）。
    /// </summary>
    public class BinderDifferentialTests
    {
        private readonly ITestOutputHelper _output;

        public BinderDifferentialTests(ITestOutputHelper output) => _output = output;

        /// <summary>C# 基准：符号表规范化输出（声明序）。</summary>
        public static string SymbolDump(Compilation compilation)
        {
            var sb = new StringBuilder();
            foreach (var function in compilation.Functions)
            {
                var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name}: {p.Type.Name}"));
                sb.AppendLine($"function {function.Name}({parameters}): {function.ReturnType.Name}");
            }

            foreach (var variable in compilation.Variables)
            {
                sb.AppendLine($"var {variable.Name}: {variable.Type.Name}");
            }

            sb.AppendLine($"main: {compilation.MainFunction?.Name ?? "<none>"}");
            return sb.ToString();
        }

        private static string[] Corpus() => new[]
        {
            "function Main(): i32\n{\n    return 1\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n\nfunction Main(): i32\n{\n    return Add(1, 2)\n}\n",
            "var g: i32 = 1\nlet s: string = \"hi\"\n\nfunction Main(): i32\n{\n    return g\n}\n",
            "var n = 10\nvar d = 3.14\nvar t = \"hi\"\nvar f = true\nvar c = 'a'\n\nfunction Main(): i32\n{\n    return n\n}\n",
            "function Log(msg: string)\n{\n}\n\nfunction Main(): i32\n{\n    Log(\"x\")\n    return 0\n}\n",
            "function Sum(values: i32[]): i32\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function TryParse(s: string, out v: i32): bool\n{\n    return false\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function Run(): i32\n{\n    return 0\n}\n",
            "var x = -1\nvar y = -2.5\n\nfunction Main(): i32\n{\n    return x\n}\n",
            "let flag = true\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            "function F(v: u8, r: f32): u64\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
        };

        [Fact]
        public void SelfBinder_Symbols_Match_CSharp_ForCorpus()
        {
            var failures = new List<string>();
            foreach (var source in Corpus())
            {
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                var reference = SymbolDump(compilation).Replace("\r\n", "\n").TrimEnd('\n');
                var self = SelfBinderDump(source);

                if (self != reference)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC#  : {reference.Replace("\n", "\\n")}\nself: {self.Replace("\n", "\\n")}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        [Fact]
        public void DumpObservation_Symbols()
        {
            var sb = new StringBuilder();
            foreach (var source in Corpus())
            {
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                sb.AppendLine($"SOURCE: {source.Replace("\n", "\\n")}");
                sb.AppendLine(SymbolDump(compilation).Replace("\r\n", "\n").TrimEnd('\n'));
                sb.AppendLine("-----");
            }

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-binder-symbols.txt"), sb.ToString());
            _output.WriteLine(sb.ToString());
            Assert.True(true);
        }

        private static string BinderMainSource(string embedded)
        {
            return "using MiniBinder\nusing System\n\nfunction Main(): i32\n{\n    let b = MiniBinder.Binder.Create(\"" + embedded + "\")\n    b.BindCompilationUnit()\n    System.Console.WriteLine(b.DescribeSymbols())\n    return 0\n}";
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string SelfBinderDump(string source)
        {
            var embedded = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var root = RepoRoot();
            var lexerCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Lexer", "Lexer.co"));
            var tokenCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Lexer", "Token.co"));
            var parserCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Parser", "Parser.co"));
            var nodeCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Parser", "Node.co"));
            var functionSymbolCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Binder", "FunctionSymbol.co"));
            var variableSymbolCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Binder", "VariableSymbol.co"));
            var binderCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Binder", "Binder.co"));
            var trees = ImmutableArray.Create(
                SyntaxTree.Parse(lexerCo),
                SyntaxTree.Parse(tokenCo),
                SyntaxTree.Parse(parserCo),
                SyntaxTree.Parse(nodeCo),
                SyntaxTree.Parse(functionSymbolCo),
                SyntaxTree.Parse(variableSymbolCo),
                SyntaxTree.Parse(binderCo),
                SyntaxTree.Parse(BinderMainSource(embedded)));

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
