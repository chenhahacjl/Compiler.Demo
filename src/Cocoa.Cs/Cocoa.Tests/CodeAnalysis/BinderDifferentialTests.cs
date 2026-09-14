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
    /// M9-a2 诊断差分 = 双方言同报错（复用 M8-a12 口径）：无效程序 C# `GetDiagnostics().HasErrors()`
    /// ∧ 自举 `error:` 非空；有效程序两侧零诊断（误报由既有语料隐式锁定）。
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
            "function TryParse(s: string, out v: i32): bool\n{\n    v = 0\n    return true\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
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
                var selfDiagCount = SelfBinderDiagnosticCount(source);
                var csharpHasErrors = compilation.GetDiagnostics().HasErrors();

                // 符号 dump 逐字节一致；诊断"有/无"双方言一致（C# 端对全局语句+Main 语料会报
                // cannot-declare-main，自举侧同规则报 error:，口径为布尔一致而非条数一致）
                if (self != reference || (selfDiagCount > 0) != csharpHasErrors)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC#  : {reference.Replace("\n", "\\n")}\nself: {self.Replace("\n", "\\n")}\nselfDiagCount: {selfDiagCount}, csharpHasErrors: {csharpHasErrors}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string[] InvalidCorpus() => new[]
        {
            // 未定义函数调用
            "function Main(): i32\n{\n    return Foo()\n}\n",
            // 未定义变量（表达式引用）
            "function Main(): i32\n{\n    return x\n}\n",
            // 未定义变量（赋值目标）
            "function Main(): i32\n{\n    x = 1\n    return 0\n}\n",
            // 参数重名
            "function F(a: i32, a: i32): i32\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            // 全局变量重名
            "var g: i32 = 1\nvar g: i32 = 2\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            // 函数重名
            "function F(): i32\n{\n    return 0\n}\n\nfunction F(): i32\n{\n    return 1\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            // 未知类型（参数签名）
            "function F(v: Foo): i32\n{\n    return 0\n}\n\nfunction Main(): i32\n{\n    return 0\n}\n",
            // 调用 arity 不匹配
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n\nfunction Main(): i32\n{\n    return Add(1)\n}\n",
            // 体内未定义名（局部已声明另一名）
            "function Main(): i32\n{\n    let x = 1\n    return y\n}\n",
        };

        [Fact]
        public void SelfBinder_ReportsErrors_ForInvalidCorpus()
        {
            var failures = new List<string>();
            foreach (var source in InvalidCorpus())
            {
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                var csharpHasErrors = compilation.GetDiagnostics().HasErrors();
                var selfDiagCount = SelfBinderDiagnosticCount(source);
                var self = SelfBinderDump(source);

                if (!csharpHasErrors || selfDiagCount == 0)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC# HasErrors: {csharpHasErrors}\nselfDiagCount: {selfDiagCount}\nself: {self.Replace("\n", "\\n")}\n-----");
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
            return "using MiniBinder\nusing System\n\nfunction Main(): i32\n{\n    let b = MiniBinder.Binder.Create(\"" + embedded + "\")\n    b.BindCompilationUnit()\n    System.Console.WriteLine(b.DescribeSymbols())\n    var i = 0\n    while i < b.DiagnosticCount()\n    {\n        System.Console.WriteLine(b.DiagnosticAt(i))\n        i = i + 1\n    }\n\n    return 0\n}";
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string SelfBinderDump(string source)
        {
            return RunSelfBinder(source).Output;
        }

        private static int SelfBinderDiagnosticCount(string source)
        {
            return RunSelfBinder(source).DiagCount;
        }

        private static (string Output, int DiagCount) RunSelfBinder(string source)
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
                    var message = "COCOMPILE-ERROR: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message));
                    return (message, 0);
                }

                var output = writer.ToString().Replace("\r\n", "\n").TrimEnd('\n');
                var diagCount = 0;
                var symbolLines = new List<string>();
                foreach (var line in output.Split('\n'))
                {
                    if (line.StartsWith("error:", StringComparison.Ordinal))
                    {
                        diagCount = diagCount + 1;
                    }
                    else
                    {
                        symbolLines.Add(line);
                    }
                }

                return (string.Join("\n", symbolLines).TrimEnd('\n'), diagCount);
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
