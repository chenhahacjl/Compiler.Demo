using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    /// <summary>
    /// 6e-M15 双前端拆分：`.cs` 严格 C# 方言（CSharpParser）与 `.co` 宽松主方言（CocoaParser）。
    /// 覆盖：扩展名分派、严格接受/拒绝矩阵、分号必选、文件作用域命名空间、using 未解析警告。
    /// </summary>
    public class CSharpDialectTests
    {
        private static ImmutableArray<Diagnostic> ParseCsDiagnostics(string text)
        {
            return SyntaxTree.ParseCs(text).Diagnostics;
        }

        private static ImmutableArray<Diagnostic> ParseCocoaDiagnostics(string text)
        {
            return SyntaxTree.Parse(text).Diagnostics;
        }

        // ---- 扩展名分派：Cocoa 专属拼写在 .cs 报错、.co 不报错 ----

        [Theory]
        [InlineData("function Add(a: int, b: int): int { return a + b }", "function 关键字")]
        [InlineData("let x = 5", "let")]
        [InlineData("var x: int = 5", "类型后置")]
        [InlineData("print(1)", "缺少分号")]
        public void Cs_RejectsCocoaSyntax_CoAccepts(string text, string _)
        {
            var csDiagnostics = ParseCsDiagnostics(text);
            Assert.True(csDiagnostics.Any(), $"`.cs` 应对 Cocoa 拼写报错: {text}");
            Assert.All(csDiagnostics, d => Assert.True(d.IsError, $"`.cs` 诊断应为错误: {d.Message}"));

            var coDiagnostics = ParseCocoaDiagnostics(text);
            Assert.False(coDiagnostics.HasErrors(), $"`.co` 不应报错: {text}");
        }

        // ---- 严格接受：合法 C# 子集无诊断 ----

        [Theory]
        [InlineData("public static void Main() { print(1); }")]
        [InlineData("public int Add(int a, int b) { return a + b; }")]
        [InlineData("public int[] GetNums() { return new int[] { 1, 2, 3 }; }")]
        [InlineData("public static void Main() { var x = 10; int y = 20; const int z = 30; print(x + y + z); }")]
        [InlineData("public static void Main() { foreach (var x in new int[] { 1, 2 }) { print(x); } }")]
        [InlineData("public static void Main() { for (int i = 0; i < 3; i++) { print(i); } }")]
        [InlineData("public static void Main() { for (var i = 0; i < 3; i++) { print(i); } }")]
        [InlineData("public static void Main() { string s = \"a\"; print(s.Length); }")]
        [InlineData("public static void Main() { print($\"{1 + 2}\"); }")]
        [InlineData("public static void Main() { switch (1) { case 1: { print(1); break; } default: { print(2); break; } } }")]
        [InlineData("public class Point { private int _x; public int X { get; set; } = 10; public Point(int x) { _x = x; } }")]
        [InlineData("namespace Foo; public class Bar { }")]
        public void Cs_AcceptsValidSubset(string text)
        {
            Assert.False(ParseCsDiagnostics(text).HasErrors(), $"`.cs` 合法子集不应报错: {text}");
        }

        // ---- extends 继承关键字拒绝 ----

        [Theory]
        [InlineData("public class Foo extends Bar { }")]
        [InlineData("public interface IB extends IA { }")]
        [InlineData("public class Foo { public constructor() extends base() { } }")]
        public void Cs_RejectsExtendsKeyword(string text)
        {
            var diagnostics = ParseCsDiagnostics(text);
            Assert.True(diagnostics.Any(d => d.Message.Contains("extends")), $".cs 应拒绝 extends: {text}");

            Assert.False(ParseCocoaDiagnostics(text).HasErrors(), $".co 应接受 extends: {text}");
        }

        [Fact]
        public void Cs_AcceptsColonInheritance()
        {
            var text = "public class Foo : Bar { public Foo() : base() { } } public interface IB : IA { }";
            Assert.False(ParseCsDiagnostics(text).HasErrors());
        }

        // ---- 条件括号强制（if/while/switch/foreach/do-while） ----

        [Theory]
        [InlineData("if a < 10 { print(1); }")]
        [InlineData("while a < 10 { print(1); }")]
        [InlineData("switch 1 { case 1: { print(1); break; } }")]
        [InlineData("foreach var x in arr { }")]
        [InlineData("do { print(1); } while a < 10;")]
        public void Cs_RequiresConditionParentheses(string body)
        {
            var text = $"public static void Main() {{ {body} }}";

            var csDiagnostics = ParseCsDiagnostics(text);
            Assert.True(csDiagnostics.Any(d => d.Message.Contains("括号")), $".cs 应要求条件括号: {body}");

            // .co 括号可选：用合法 Cocoa 入口包裹（C# 式 `public static void Main()` 在 .co 已被拒绝）
            var coText = $"function Main() {{ {body} }}";
            Assert.False(ParseCocoaDiagnostics(coText).HasErrors(), $".co 括号可选不应报错: {body}");
        }

        [Fact]
        public void Cs_AcceptsParenthesizedConditions()
        {
            var text = "public static void Main() { if (a < 10) { print(1); } while (a < 10) { print(1); } }";
            Assert.False(ParseCsDiagnostics(text).HasErrors());
        }

        // ---- 分号必选 ----

        [Theory]
        [InlineData("public static void Main() { print(1) }")]
        [InlineData("public static void Main() { var x = 5 }")]
        [InlineData("public static void Main() { return 1 }")]
        public void Cs_RequiresSemicolon(string text)
        {
            var csDiagnostics = ParseCsDiagnostics(text);
            Assert.True(csDiagnostics.Any(d => d.Message.Contains("分号")), $".cs 应要求分号: {text}");

            // .co 分号可选：用合法 Cocoa 入口包裹（C# 式 `public static void Main()` 在 .co 已被拒绝）
            var coText = text.Replace("public static void Main()", "function Main()");
            Assert.False(ParseCocoaDiagnostics(coText).HasErrors(), $".co 分号可选不应报错: {text}");
        }

        // ---- 参数/局部类型前置 ----

        [Fact]
        public void Cs_RejectsCocoaParameter()
        {
            var diagnostics = ParseCsDiagnostics("public int Add(x: int) { return x; }");
            Assert.True(diagnostics.Any(d => d.Message.Contains("参数") && d.Message.Contains("类型")));
        }

        [Fact]
        public void Cs_RejectsVarWithoutInitializer()
        {
            var diagnostics = ParseCsDiagnostics("public static void Main() { var x; }");
            Assert.True(diagnostics.Any(d => d.Message.Contains("初始化器")));
        }

        // ---- 文件作用域命名空间 ----

        [Fact]
        public void FileScopedNamespace_WrapsRestOfFile()
        {
            var tree = SyntaxTree.ParseCs("namespace Foo; public class Bar { } public static void Main() { print(1); }");
            Assert.False(tree.Diagnostics.HasErrors());

            var ns = Assert.Single(tree.Root.Members.OfType<NamespaceDeclarationSyntax>());
            Assert.Equal("Foo", ns.Name);
            Assert.Equal(2, ns.Members.Length); // class Bar + Main
        }

        [Fact]
        public void FileScopedNamespace_AlsoWorksInCocoa()
        {
            var tree = SyntaxTree.Parse("namespace Foo; public class Bar { }");
            Assert.False(tree.Diagnostics.HasErrors());
            Assert.Single(tree.Root.Members.OfType<NamespaceDeclarationSyntax>());
        }

        // ---- 嵌套 using 在文件作用域命名空间内可收集 ----

        [Fact]
        public void FileScopedNamespace_UsingsCollected()
        {
            // using 在文件作用域命名空间内：应被 Binder 收集（6e-M15），Bogus 未解析 → 警告
            var tree = SyntaxTree.ParseCs("namespace Foo; using Bogus.X; public static void Main() { print(1); }");
            var compilation = Compilation.Create(tree);
            var diagnostics = compilation.GlobalScope.Diagnostics;

            Assert.True(diagnostics.Any(d => d.IsWarning && d.Message.Contains("Using namespace 'Bogus.X'")));
        }

        // ---- using 未解析警告（两态） ----

        [Fact]
        public void Using_Unresolved_Warns()
        {
            var tree = SyntaxTree.ParseCs("using Bogus.NotFound; public static void Main() { print(1); }");
            var compilation = Compilation.Create(tree);

            Assert.True(compilation.GlobalScope.Diagnostics.Any(d => d.IsWarning && d.Message.Contains("Bogus.NotFound")));
        }

        [Fact]
        public void Using_ResolvedByProgramNamespace_NoWarning()
        {
            var tree = SyntaxTree.ParseCs("namespace MyLib; public class Util { } using MyLib; public static void Main() { print(1); }");
            var compilation = Compilation.Create(tree);

            Assert.False(compilation.GlobalScope.Diagnostics.Any(d => d.IsWarning && d.Message.Contains("Using namespace")));
        }

        [Fact]
        public void Using_ResolvedByReferenceAssembly_NoWarning()
        {
            // BCL 引用含 System 命名空间 → `using System;` 不警告
            var references = new[] { typeof(object).Assembly.Location };
            var tree = SyntaxTree.ParseCs("using System; public static void Main() { print(1); }");
            var compilation = Compilation.Create(references, tree);

            Assert.False(compilation.GlobalScope.Diagnostics.Any(d => d.IsWarning && d.Message.Contains("Using namespace")));
        }

        // ---- 插值洞子解析诊断并入主 bag ----

        [Fact]
        public void InterpolationHole_DiagnosticsMerged()
        {
            var diagnostics = ParseCsDiagnostics("public static void Main() { print($\"{1 + }\"); }");
            Assert.True(diagnostics.Any(), "插值洞内的解析错误应并入主诊断");
        }
    }
}
