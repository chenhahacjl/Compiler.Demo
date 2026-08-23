using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M18 `using` 增强：`using static <类>` / `using <别名> = <名>` / `using System;` + 短名。
    /// </summary>
    public class UsingDirectiveTests
    {
        private static EvaluationResult Evaluate(string text)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(text));
            return compilation.Evaluate(new Dictionary<VariableSymbol, object>());
        }

        private static void AssertValue(string text, object expected)
        {
            var result = Evaluate(text);
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
            Assert.Equal(expected, result.Value);
        }

        [Fact]
        public void UsingSystem_BareConsoleWriteLine_Compiles()
        {
            // Console 为静态容器类（System.Core.cod），using System 后短名经 using 前缀类解析
            var result = Evaluate(@"
using System

function Main()
{
    Console.WriteLine(7)
}
");
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void UsingSystem_MathMax_ByShortName()
        {
            AssertValue(@"
using System

function Main(): i32
{
    return Math.Max(3, 5)
}
", 5);
        }

        [Fact]
        public void UsingSystem_MathMaxDoubleOverload_Resolved()
        {
            AssertValue(@"
using System

function Pick(): f64
{
    return Math.Max(3.5, 2.5)
}

function Main(): i32
{
    if Pick() == 3.5
    {
        return 1
    }
    return 0
}
", 1);
        }

        [Fact]
        public void UsingStaticMath_BareMax_Resolves()
        {
            AssertValue(@"
using static System.Math

function Main(): i32
{
    return Max(3, 5)
}
", 5);
        }

        [Fact]
        public void UsingStaticMath_BareMaxDoubleOverload_Resolved()
        {
            AssertValue(@"
using static System.Math

function Pick(): f64
{
    return Max(3.5, 2.5)
}

function Main(): i32
{
    if Pick() == 3.5
    {
        return 1
    }
    return 0
}
", 1);
        }

        [Fact]
        public void UsingStaticRuntime_BareRandom_Resolves()
        {
            // Runtime 为 syscall 容器类：using static 导入其静态原语为裸名
            var result = Evaluate(@"
using static System.Runtime

function Main(): i32
{
    var r = Random(100)
    if r >= 0 && r < 100
    {
        return 1
    }
    return 0
}
");
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
            Assert.Equal(1, result.Value);
        }

        [Fact]
        public void UsingAliasToClass_QualifiedAccess()
        {
            AssertValue(@"
using M = System.Math

function Main(): i32
{
    return M.Max(3, 5)
}
", 5);
        }

        [Fact]
        public void UsingAliasToConsole_WriteLine_Compiles()
        {
            var result = Evaluate(@"
using C = System.Console

function Main()
{
    C.WriteLine(42)
}
");
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void UsingAliasToRuntime_WriteLine_Resolves()
        {
            var result = Evaluate(@"
using R = System.Runtime

function Main()
{
    R.WriteLine(99)
}
");
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void UsingStatic_OnNamespace_ReportsTargetNotClass()
        {
            // System 是命名空间（含各静态类），不是类 → using static 目标必须是类
            var compilation = Compilation.Create(SyntaxTree.Parse(@"
using static System

function Main()
{
}
"));
            Assert.Contains(compilation.GlobalScope.Diagnostics, d => d.Message.Contains("using static 的目标 'System' 必须是类"));
        }

        [Fact]
        public void UsingStatic_OnUnknown_ReportsTargetNotClass()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(@"
using static Foo.Bar

function Main()
{
}
"));
            Assert.Contains(compilation.GlobalScope.Diagnostics, d => d.Message.Contains("using static 的目标 'Foo.Bar' 必须是类"));
        }

        [Fact]
        public void UsingAlias_UnknownTarget_ReportsUnresolved()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(@"
using F = Foo.Bar

function Main()
{
}
"));
            Assert.Contains(compilation.GlobalScope.Diagnostics, d => d.Message.Contains("could not be resolved"));
        }

        [Fact]
        public void CsDialect_UsingStatic_SemicolonRequired()
        {
            // .cs 方言：using 必须以分号结尾
            var syntaxTree = SyntaxTree.ParseCs("using static System.Math\nfunction Main() {}");
            Assert.NotEmpty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void CsDialect_UsingAlias_SemicolonRequired()
        {
            var syntaxTree = SyntaxTree.ParseCs("using M = System.Math\nfunction Main() {}");
            Assert.NotEmpty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void CsDialect_UsingStatic_WithSemicolon_NoDiagnostics()
        {
            var syntaxTree = SyntaxTree.ParseCs("using static System.Math;");
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void CsDialect_UsingAlias_WithSemicolon_NoDiagnostics()
        {
            var syntaxTree = SyntaxTree.ParseCs("using M = System.Math;");
            Assert.Empty(syntaxTree.Diagnostics);
        }
    }
}
