using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>命名空间限定访问 + 函数重载（6e-M14 标准库的语言前置特性）。</summary>
    public class NamespaceOverloadTests
    {
        // ---------------------------------------------------------------- 命名空间访问

        [Fact]
        public void Evaluator_NamespaceQualified_FullPath()
        {
            AssertValue(@"
namespace Foo
{
    namespace Bar
    {
        function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }
    }
}

function Main(): int
{
    return Foo.Bar.Max(3, 5)
}
", 5);
        }

        [Fact]
        public void Evaluator_NamespaceQualified_UsingRootNamespace_ModuleName()
        {
            AssertValue(@"
namespace System
{
    namespace Math
    {
        function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }
    }
}

using System

function Main(): int
{
    return Math.Max(3, 5)
}
", 5);
        }

        [Fact]
        public void Evaluator_NamespaceQualified_UsingModuleNamespace_BareName()
        {
            AssertValue(@"
namespace System
{
    namespace Math
    {
        function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }
    }
}

using System.Math

function Main(): int
{
    return Max(3, 5)
}
", 5);
        }

        // ---------------------------------------------------------------- 重载

        [Fact]
        public void Evaluator_Overload_IntChosenForIntArgs()
        {
            AssertValue(@"
function Max(a: int, b: int): int
{
    if (a > b) return a
    return b
}

function Max(a: double, b: double): double
{
    if (a > b) return a
    return b
}

function Main(): int
{
    return Max(3, 5)
}
", 5);
        }

        [Fact]
        public void Evaluator_Overload_DoubleChosenForDoubleArgs()
        {
            AssertValue(@"
function Max(a: int, b: int): int
{
    if (a > b) return a
    return b
}

function Max(a: double, b: double): double
{
    if (a > b) return a
    return b
}

function Main(): int
{
    if Max(3.0, 5.0) == 5.0
    {
        return 1
    }
    return 0
}
", 1);
        }

        [Fact]
        public void Evaluator_Overload_IntAndDoubleOverloadsInNamespace()
        {
            AssertValue(@"
namespace System
{
    namespace Math
    {
        function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }

        function Max(a: double, b: double): double
        {
            if (a > b) return a
            return b
        }
    }
}

function Main(): int
{
    var i = System.Math.Max(3, 5)
    var d = System.Math.Max(1.5, 0.5)
    return i
}
", 5);
        }

        [Fact]
        public void Evaluator_Overload_Ambiguous_ReportsDiagnostic()
        {
            var text = @"
                function G(a: int): int
                {
                    return a
                }

                function G(a: double): int
                {
                    return int(a)
                }

                function Main(): int
                {
                    return [G](byte(3))
                }
            ";

            var diagnostics = @"
                The call to 'G' is ambiguous between multiple overloads.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Overload_NoMatchingOverload_ReportsDiagnostic()
        {
            var text = @"
                function Max(a: int, b: int): int
                {
                    if (a > b) return a
                    return b
                }

                function Max(a: double, b: double): double
                {
                    if (a > b) return a
                    return b
                }

                function Main(): int
                {
                    return [Max](""x"", 5)
                }
            ";

            var diagnostics = @"
                Function 'Max' has no overload that matches the argument types.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Overload_DuplicateSignature_ReportsDiagnostic()
        {
            var text = @"
                function F(a: int): int
                {
                    return a
                }

                function [F](a: int): string
                {
                    return """"
                }
            ";

            var diagnostics = @"
                'F' is already declared.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        // ---------------------------------------------------------------- IL e2e

        [Fact]
        public void IlE2e_Overload_NamespaceQualified_Runs()
        {
            var source = @"
namespace System
{
    namespace Math
    {
        function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }

        function Max(a: double, b: double): double
        {
            if (a > b) return a
            return b
        }
    }
}

using System.Math

function Main()
{
    print(Max(3, 5))
    print(Max(1.5, 0.5))
    print(System.Math.Max(10, 2))
}
";
            var (exitCode, stdout) = IlEmitAndRun(source, "ns-overload");
            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n1.5\r\n10\r\n", stdout);
        }

        // ---------------------------------------------------------------- 辅助

        private static void AssertValue(string text, object expectedValue)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
            Assert.Equal(expectedValue, result.Value);
        }

        private static void AssertDiagnostics(string text, string diagnosticText)
        {
            var annotatedText = AnnotatedText.Parse(text);
            var syntaxTree = SyntaxTree.Parse(annotatedText.Text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            var expectedDiagnostics = AnnotatedText.UnindentLines(diagnosticText);
            if (annotatedText.Spans.Length == 0)
            {
                Assert.Equal(expectedDiagnostics, result.Diagnostics.Select(d => d.ToString()));
            }
            else
            {
                Assert.Equal(expectedDiagnostics.Length, annotatedText.Spans.Length);
                Assert.Equal(expectedDiagnostics, result.Diagnostics.Select(d => d.Message));
                Assert.Equal(annotatedText.Spans, result.Diagnostics.Select(d => d.Location.Span));
            }
        }

        private static (int ExitCode, string Stdout) IlEmitAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var references = new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, syntaxTree);
            var exePath = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N") + ".exe");
            var diagnostics = compilation.Emit(name, references, exePath, IlTarget.Parse("net9.0"));

            Assert.True(diagnostics.Length == 0, string.Join("\n", diagnostics));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            File.Delete(exePath);
            return (process.ExitCode, stdout + (stderr.Length == 0 ? "" : "\nSTDERR: " + stderr));
        }
    }
}
