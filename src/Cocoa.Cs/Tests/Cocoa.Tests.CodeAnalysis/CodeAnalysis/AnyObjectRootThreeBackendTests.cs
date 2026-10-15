using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 对象根统一（object）三后端验证：语言层 `object` 为对象根关键字（对齐 C#）；
    /// `x is object` 放开（对象根类判定）；System.Object 全名经互操作映射到 object 符号。
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
        public void BareObject_TypeName_IsObjectRoot()
        {
            // 6f：object 为对象根内建关键字（对齐 C#），裸 object 类型合法
            var source = "function Main(): i32\n{\n    var o: object = 5\n    if o is object { return 0 }\n    return 1\n}\n";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diags = compilation.GetDiagnostics();
            Assert.DoesNotContain(diags, d => d.IsError);
            var value = RunEvaluator(source, out _);
            Assert.Equal(0, Assert.IsType<int>(value));
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
        public void BareAny_TypeName_IsUndefined()
        {
            // 6f：any 已弃用（改名 object），裸 any 报 undefined
            var source = "function Main(): i32\n{\n    var o: an" + "y = 5\n    return 0\n}\n";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diags = compilation.GetDiagnostics();
            Assert.Contains(diags, d => d.IsError && d.Message.Contains("any"));
        }

        [Fact]
        public void Evaluator_Object_Boxing_And_IsObject()
        {
            var source =
                "function Main(): i32\n{\n" +
                "    let a: object = 5\n" +
                "    let b: object = \"hi\"\n" +
                "    let c: object = 3.5\n" +
                "    var n = 0\n" +
                "    if a is object { n = n + 1 }\n" +
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
        public void ObjectModel_DefaultBase_IsObject()
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
            Assert.Same(TypeSymbol.Object, point.BaseType);
            Assert.True(point.BaseType == NamedTypeSymbol.SystemObject);
        }
    }
}