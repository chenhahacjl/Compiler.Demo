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
    /// <summary>D：泛型约束 where T: class/struct（绑定 + 实例化检查）验证。</summary>
    public class GenericConstraintTests
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
        public void StructConstraint_AcceptsValueType()
        {
            var output = Run(@"using System

public class BoxS<T> where T: struct
{
    public field value: T
    public constructor(v: T) { value = v }
}

function Main(): i32
{
    var si = new BoxS<i32>(5)
    System.Console.WriteLine(si.value == 5)
    var sb = new BoxS<bool>(true)
    System.Console.WriteLine(sb.value)
    return 0
}");
            Assert.Equal("True\nTrue\n", output);
        }

        [Fact]
        public void ClassConstraint_AcceptsReferenceType()
        {
            var output = Run(@"using System

public class BoxR<T> where T: class
{
    public field value: T
    public constructor(v: T) { value = v }
}

function Main(): i32
{
    var ss = new BoxR<string>(""hi"")
    System.Console.WriteLine(ss.value == ""hi"")
    return 0
}");
            Assert.Equal("True\n", output);
        }

        [Fact]
        public void StructConstraint_RejectsReferenceType()
        {
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

public class BoxS<T> where T: struct
{
    public field value: T
    public constructor(v: T) { value = v }
}

function Main(): i32
{
    var bad = new BoxS<string>(""nope"")
    return 0
}"));
            Assert.True(compilation.GetDiagnostics().Any(d => d.Message.Contains("要求值类型") || d.Message.Contains("struct")), "应报告 struct 约束违反。");
        }

        [Fact]
        public void ClassConstraint_RejectsValueType()
        {
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

public class BoxR<T> where T: class
{
    public field value: T
    public constructor(v: T) { value = v }
}

function Main(): i32
{
    var bad = new BoxR<i32>(1)
    return 0
}"));
            Assert.True(compilation.GetDiagnostics().Any(d => d.Message.Contains("要求引用类型") || d.Message.Contains("class")), "应报告 class 约束违反。");
        }
    }
}