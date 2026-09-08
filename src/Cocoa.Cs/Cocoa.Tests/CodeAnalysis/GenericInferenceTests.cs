using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>阶段 1：泛型方法类型推断（无显式类型实参，从实参类型反推；嵌套泛型/多参/数组）。</summary>
    public class GenericInferenceTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string Run(string text)
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(text));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                return writer.ToString().Replace("\r\n", "\n");
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void InfersTypeArgument_Simple()
        {
            var output = Run(@"using System

function Identity<T>(x: T): T { return x }

function Main(): i32
{
    var s = Identity(""hi"")
    System.Console.WriteLine(s == ""hi"")
    var n = Identity(42)
    System.Console.WriteLine(n == 42)
    var b = Identity(true)
    System.Console.WriteLine(b)
    return 0
}");
            Assert.Equal("True\nTrue\nTrue\n", output);
        }

        [Fact]
        public void InfersMultiTypeArguments()
        {
            var output = Run(@"using System

function PickSecond<K, V>(a: K, b: V): V { return b }

function Main(): i32
{
    var v = PickSecond(1, ""x"")
    System.Console.WriteLine(v == ""x"")
    return 0
}");
            Assert.Equal("True\n", output);
        }

        [Fact]
        public void InfersFromArrayArgument()
        {
            var output = Run(@"using System

function ArrayFirst<T>(arr: T[]): T { return arr[0] }

function Main(): i32
{
    var arr = new i32[2]
    arr[0] = 7
    arr[1] = 8
    var f = ArrayFirst(arr)
    System.Console.WriteLine(f == 7)
    var words = new string[1]
    words[0] = ""ok""
    var w = ArrayFirst(words)
    System.Console.WriteLine(w == ""ok"")
    return 0
}");
            Assert.Equal("True\nTrue\n", output);
        }

        [Fact]
        public void ExplicitTypeArgumentsStillWork()
        {
            // 显式实参路径保持（C1 回归）。
            var output = Run(@"using System

function Identity<T>(x: T): T { return x }

function Main(): i32
{
    var s = Identity<i32>(5)
    System.Console.WriteLine(s == 5)
    return 0
}");
            Assert.Equal("True\n", output);
        }

        [Fact]
        public void UninferableReportsDiagnostic()
        {
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

function Noumena<T, U>(x: T): T { return x }

function Main(): i32
{
    var r = Noumena(1)
    return 0
}"));
            Assert.True(compilation.GetDiagnostics().Any(d => d.Message.Contains("无法从实参推断") || d.Message.Contains("类型实参")), "应报告无法推断。");
        }
    }
}