using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 对象根统一（any）三后端验证：语言层无 object 关键字；
    /// `x is any` 放开（对象根类判定）；System.Object 全名经互操作映射到 any。
    /// </summary>
    public class AnyObjectRootThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static object? RunEvaluator(string source, out string output)
        {
            var original = Console.Out;
            try
            {
                using var writer = new System.IO.StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                output = writer.ToString();
                return result.Value;
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void BareObject_TypeName_IsUndefined()
        {
            var source = "function Main(): i32\n{\n    var o: object = 5\n    return 0\n}\n";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diags = compilation.GetDiagnostics();
            Assert.Contains(diags, d => d.IsError && d.Message.Contains("object"));
        }

        [Fact]
        public void BareCapitalObject_TypeName_IsUndefined()
        {
            var source = "function Main(): i32\n{\n    var o: Object = 5\n    return 0\n}\n";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diags = compilation.GetDiagnostics();
            Assert.Contains(diags, d => d.IsError && d.Message.Contains("Object"));
        }

        [Fact]
        public void Evaluator_Any_Boxing_And_IsAny()
        {
            var source =
                "function Main(): i32\n{\n" +
                "    let a: any = 5\n" +
                "    let b: any = \"hi\"\n" +
                "    let c: any = 3.5\n" +
                "    var n = 0\n" +
                "    if a is any { n = n + 1 }\n" +
                "    if a is i32 { n = n + 1 }\n" +
                "    if !(b is i32) { n = n + 1 }\n" +
                "    if c is f64 { n = n + 1 }\n" +
                "    let back: i32 = (i32)a\n" +
                "    if back == 5 { n = n + 1 }\n" +
                "    return n\n" +
                "}\n";
            var value = RunEvaluator(source, out _);
            Assert.Equal(5, Assert.IsType<int>(value));
        }

        [Fact]
        public void ObjectModel_DefaultBase_IsAny()
        {
            var syntaxTree = SyntaxTree.Parse(@"using System
public class Point
{
    public function Get(): i32
    {
        return 1
    }
}");
            var compilation = Compilation.Create(syntaxTree);
            var point = compilation.GlobalScope.Classes.Single(c => c.Name == "Point");
            Assert.Same(TypeSymbol.Any, point.BaseType);
            Assert.True(point.BaseType == NamedTypeSymbol.SystemObject);
        }
    }
}