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

        /// <summary>C# 基准：符号表规范化输出（声明序；局部行缩进 2 空格，仅绑定无错时输出）。</summary>
        public static string SymbolDump(Compilation compilation)
        {
            var sb = new StringBuilder();
            var program = compilation.GetProgram();
            var hasErrors = compilation.GetDiagnostics().HasErrors();
            foreach (var function in compilation.Functions)
            {
                var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name}: {p.Type.Name}"));
                sb.AppendLine($"function {function.Name}({parameters}): {function.ReturnType.Name}");

                // M9-a3：局部符号（绑定树 BoundVariableDeclaration，语法声明序；有错时绑定不完整，双方言约定跳过）
                if (!hasErrors && program.Functions.TryGetValue(function, out var body))
                {
                    foreach (var (name, type) in CollectLocals(body))
                    {
                        sb.AppendLine($"  local {name}: {type}");
                    }
                }
            }

            foreach (var variable in compilation.Variables)
            {
                sb.AppendLine($"var {variable.Name}: {variable.Type.Name}");
            }

            sb.AppendLine($"main: {compilation.MainFunction?.Name ?? "<none>"}");
            return sb.ToString();
        }

        private static List<(string Name, string Type)> CollectLocals(Cocoa.CodeAnalysis.Binding.BoundNode node)
        {
            var acc = new List<(string, string)>();
            Collect(node, acc);
            return acc;

            static void Collect(Cocoa.CodeAnalysis.Binding.BoundNode current, List<(string, string)> sink)
            {
                if (current is Cocoa.CodeAnalysis.Binding.BoundVariableDeclaration declaration)
                {
                    sink.Add((declaration.Variable.Name, declaration.Variable.Type.Name));
                }

                foreach (var child in Compilation.BoundChildren(current))
                {
                    Collect(child, sink);
                }
            }
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
            // M9-a3：局部符号
            "function Main(): i32\n{\n    let x = 1\n    let y = x + 1\n    return y\n}\n",
            "function F(): i32\n{\n    if true\n    {\n        let t = 2\n        return t\n    }\n\n    return 0\n}\n\nfunction Main(): i32\n{\n    return F()\n}\n",
            "function Main(): i32\n{\n    let d = 1.5\n    let s = \"a\"\n    let c = 'c'\n    let b = true\n    var n: i32 = 3\n    return n\n}\n",
            "var g: i32 = 5\n\nfunction Sum(a: i32, b: i32): i32\n{\n    let t = a + b\n    return t\n}\n\nfunction Main(): i32\n{\n    let r = Sum(g, 2)\n    return r\n}\n",
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
        public void SelfBinder_Diagnostics_Match_CSharp_ForInvalidCorpus()
        {
            // M9-a4：诊断消息逐字节对齐（排序消除顺序差异；条数+文本完全一致）
            var failures = new List<string>();
            foreach (var source in InvalidCorpus())
            {
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                var csharp = compilation.GetDiagnostics()
                    .Select(d => d.Message)
                    .OrderBy(m => m, StringComparer.Ordinal)
                    .ToList();
                var self = SelfBinderDiagnostics(source)
                    .OrderBy(m => m, StringComparer.Ordinal)
                    .ToList();
                var csharpText = string.Join(" || ", csharp);
                var selfText = string.Join(" || ", self);

                if (csharpText != selfText)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC#  : {csharpText}\nself: {selfText}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string[] BoundCorpus() => new[]
        {
            "function Main(): i32\n{\n    let x = 1\n    let y = x + 2 * 3\n    return y\n}\n",
            "function Main(): i32\n{\n    let a = -5\n    let b = !(1 == 2)\n    return a\n}\n",
            "function Add(a: i32, b: i32): i32\n{\n    return a + b\n}\n\nfunction Main(): i32\n{\n    return Add(1, 2)\n}\n",
            // 控制流（if/while）在 C# 绑定器已降级为 label/goto/conditionalgoto——与 Lowering dump 一并随增量四差分
        };

        [Fact]
        public void SelfBinder_BoundTree_Match_CSharp_ForCorpus()
        {
            // M9-a5（增量四前哨）：绑定树规范化 dump 差分（(Kind ...) 单行树形，无优先级括号；
            // 语料限首片节点集：块/return/表达式语句/局部声明/if/while/赋值/字面量/变量/一元/二元/调用）
            var failures = new List<string>();
            foreach (var source in BoundCorpus())
            {
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                var program = compilation.GetProgram();
                var csharpParts = new List<string>();
                foreach (var function in compilation.Functions)
                {
                    if (program.Functions.TryGetValue(function, out var body))
                    {
                        csharpParts.Add(function.Name + ": " + BoundTreeDump(body));
                    }
                }

                var reference = string.Join("\n", csharpParts);
                var self = SelfBinderBoundDump(source);

                if (self != reference)
                {
                    failures.Add($"SOURCE: {source.Replace("\n", "\\n")}\nC#  : {reference.Replace("\n", "\\n")}\nself: {self.Replace("\n", "\\n")}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        /// <summary>绑定树规范化 dump（首片）：(Kind ...) 单行空格分隔；载荷内嵌；无优先级括号。</summary>
        private static string BoundTreeDump(Cocoa.CodeAnalysis.Binding.BoundNode node)
        {
            var sb = new StringBuilder();
            DumpBound(node, sb);
            return sb.ToString();
        }

        private static void DumpBound(Cocoa.CodeAnalysis.Binding.BoundNode node, StringBuilder sb)
        {
            switch (node.Kind)
            {
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.SequencePointStatement:
                    // 调试序列点为绑定器实现细节（语句+源位置包装），不属语义树形 → 透传
                    DumpBound(((Cocoa.CodeAnalysis.Binding.BoundSequencePointStatement)node).Statement, sb);
                    break;
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.BlockStatement:
                {
                    sb.Append("(BlockStatement");
                    foreach (var statement in ((Cocoa.CodeAnalysis.Binding.BoundBlockStatement)node).Statements)
                    {
                        sb.Append(' ');
                        DumpBound(statement, sb);
                    }

                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.ReturnStatement:
                {
                    var r = (Cocoa.CodeAnalysis.Binding.BoundReturnStatement)node;
                    sb.Append("(ReturnStatement");
                    if (r.Expression != null)
                    {
                        sb.Append(' ');
                        DumpBound(r.Expression, sb);
                    }

                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.ExpressionStatement:
                {
                    sb.Append("(ExpressionStatement ");
                    DumpBound(((Cocoa.CodeAnalysis.Binding.BoundExpressionStatement)node).Expression, sb);
                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.VariableDeclaration:
                {
                    var d = (Cocoa.CodeAnalysis.Binding.BoundVariableDeclaration)node;
                    sb.Append("(VariableDeclaration ").Append(d.Variable.IsReadOnly ? "let" : "var").Append(' ').Append(d.Variable.Name).Append(' ');
                    DumpBound(d.Initializer, sb);
                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.IfStatement:
                {
                    var i = (Cocoa.CodeAnalysis.Binding.BoundIfStatement)node;
                    sb.Append("(IfStatement ");
                    DumpBound(i.Condition, sb);
                    sb.Append(' ');
                    DumpBound(i.ThenStatement, sb);
                    if (i.ElseStatement != null)
                    {
                        sb.Append(' ');
                        DumpBound(i.ElseStatement, sb);
                    }

                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.WhileStatement:
                {
                    var w = (Cocoa.CodeAnalysis.Binding.BoundWhileStatement)node;
                    sb.Append("(WhileStatement ");
                    DumpBound(w.Condition, sb);
                    sb.Append(' ');
                    DumpBound(w.Body, sb);
                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.LiteralExpression:
                {
                    var l = (Cocoa.CodeAnalysis.Binding.BoundLiteralExpression)node;
                    sb.Append("(LiteralExpression ").Append(FormatLiteral(l)).Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.VariableExpression:
                {
                    sb.Append("(VariableExpression ").Append(((Cocoa.CodeAnalysis.Binding.BoundVariableExpression)node).Variable.Name).Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.AssignmentExpression:
                {
                    var a = (Cocoa.CodeAnalysis.Binding.BoundAssignmentExpression)node;
                    sb.Append("(AssignmentExpression ").Append(a.Variable.Name).Append(' ');
                    DumpBound(a.Expression, sb);
                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.UnaryExpression:
                {
                    var u = (Cocoa.CodeAnalysis.Binding.BoundUnaryExpression)node;
                    sb.Append("(UnaryExpression ").Append(Cocoa.CodeAnalysis.Binding.BoundOperatorText.UnaryGlyph(u.Op.Kind)).Append(' ');
                    DumpBound(u.Operand, sb);
                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.BinaryExpression:
                {
                    var b = (Cocoa.CodeAnalysis.Binding.BoundBinaryExpression)node;
                    sb.Append("(BinaryExpression ").Append(Cocoa.CodeAnalysis.Binding.BoundOperatorText.BinaryGlyph(b.Op.Kind)).Append(' ');
                    DumpBound(b.Left, sb);
                    sb.Append(' ');
                    DumpBound(b.Right, sb);
                    sb.Append(')');
                    break;
                }
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.CallExpression:
                {
                    var c = (Cocoa.CodeAnalysis.Binding.BoundCallExpression)node;
                    sb.Append("(CallExpression ").Append(c.Function.Name);
                    foreach (var argument in c.Arguments)
                    {
                        sb.Append(' ');
                        DumpBound(argument, sb);
                    }

                    sb.Append(')');
                    break;
                }
                default:
                    throw new Exception($"Bound dump: unsupported node kind '{node.Kind}' (extend BoundCorpus/printer together)");
            }
        }

        private static string FormatLiteral(Cocoa.CodeAnalysis.Binding.BoundLiteralExpression node)
        {
            if (node.Value == null)
            {
                return "null";
            }

            if (node.Type == Cocoa.CodeAnalysis.Symbols.TypeSymbol.Boolean)
            {
                return (bool)node.Value ? "true" : "false";
            }

            if (node.Type == Cocoa.CodeAnalysis.Symbols.TypeSymbol.Double)
            {
                return ((double)node.Value).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (node.Type == Cocoa.CodeAnalysis.Symbols.TypeSymbol.String)
            {
                return "\"" + node.Value.ToString()!.Replace("\"", "\"\"") + "\"";
            }

            if (node.Type == Cocoa.CodeAnalysis.Symbols.TypeSymbol.Char)
            {
                var text = ((char)node.Value).ToString().Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("'", "\\'");
                return "'" + text + "'";
            }

            return node.Value.ToString()!;
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
            return "using MiniBinder\nusing System\n\nfunction Main(): i32\n{\n    let b = MiniBinder.Binder.Create(\"" + embedded + "\")\n    b.BindCompilationUnit()\n    System.Console.WriteLine(b.DescribeSymbols())\n    var i = 0\n    while i < b.DiagnosticCount()\n    {\n        System.Console.WriteLine(b.DiagnosticAt(i))\n        i = i + 1\n    }\n\n    System.Console.WriteLine(\"#BOUND\")\n    System.Console.WriteLine(b.DescribeBoundTrees())\n    return 0\n}";
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

        private static IReadOnlyList<string> SelfBinderDiagnostics(string source)
        {
            return RunSelfBinder(source).Diags;
        }

        private static string SelfBinderBoundDump(string source)
        {
            return RunSelfBinder(source).Bound;
        }

        private static (string Output, int DiagCount, IReadOnlyList<string> Diags, string Bound) RunSelfBinder(string source)
        {
            var embedded = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var root = RepoRoot();
            var lexerCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Lexer.co"));
            var tokenCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Token.co"));
            var parserCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Parser.co"));
            var nodeCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Node.co"));
            var functionSymbolCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Symbols", "FunctionSymbol.co"));
            var variableSymbolCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Symbols", "VariableSymbol.co"));
            var binderCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Binding", "Binder.co"));
            var localSymbolCo = File.ReadAllText(Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler", "Binding", "LocalSymbol.co"));
            var trees = ImmutableArray.Create(
                SyntaxTree.Parse(lexerCo),
                SyntaxTree.Parse(tokenCo),
                SyntaxTree.Parse(parserCo),
                SyntaxTree.Parse(nodeCo),
                SyntaxTree.Parse(functionSymbolCo),
                SyntaxTree.Parse(variableSymbolCo),
                SyntaxTree.Parse(binderCo),
                SyntaxTree.Parse(localSymbolCo),
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
                    return (message, 0, Array.Empty<string>(), "");
                }

                var output = writer.ToString().Replace("\r\n", "\n").TrimEnd('\n');
                var diagCount = 0;
                var diags = new List<string>();
                var symbolLines = new List<string>();
                var boundLines = new List<string>();
                var inBound = false;
                foreach (var line in output.Split('\n'))
                {
                    if (line == "#BOUND")
                    {
                        inBound = true;
                    }
                    else if (inBound)
                    {
                        boundLines.Add(line);
                    }
                    else if (line.StartsWith("error:", StringComparison.Ordinal))
                    {
                        diagCount = diagCount + 1;
                        diags.Add(line.Substring("error:".Length).Trim());
                    }
                    else
                    {
                        symbolLines.Add(line);
                    }
                }

                return (string.Join("\n", symbolLines).TrimEnd('\n'), diagCount, diags, string.Join("\n", boundLines).TrimEnd('\n'));
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
