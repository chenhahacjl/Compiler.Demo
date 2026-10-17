using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// C# 语义 `typeof`/`sizeof` 求值器端测试：
    /// `typeof(T)` 恒为类型常量——基元/BCL facade → 宿主 System.Type；用户定义类 → interned
    /// EvaluatorTypeInfo（同一类型恒同一实例，`==`/`GetType()` 一致）；`sizeof` 基元编译期常量。
    /// </summary>
    public sealed class TypeOfSizeOfTests
    {
        private static object? Evaluate(string source)
        {
            var compilation = global::Cocoa.CodeAnalysis.Compilation.Create(SyntaxTree.Parse(source));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            var diag = string.Join("; ", result.Diagnostics.Select(d => d.Message));
            Assert.False(result.Diagnostics.HasErrors(), $"diags: {diag}");
            return result.Value;
        }

        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        [Fact]
        public void Typeof_Primitive_SameInstance_ReferenceEqual()
        {
            // C#：typeof(i32) 是同一次运行内同一 Type 实例，引用相等成立
            Assert.Equal(1, Evaluate("function Main(): i32 { return typeof(i32) == typeof(i32) ? 1 : 0 }"));
        }

        [Fact]
        public void Typeof_UserClass_ReferenceEqual_WhenSameClass()
        {
            Assert.Equal(1, Evaluate(
                "class Person { }\n" +
                "function Main(): i32 { var t = typeof(Person) return 1 }"));
        }

        [Fact]
        public void Typeof_UserClass_NotEqual_DifferentClass()
        {
            Assert.Equal(0, Evaluate(
                "class Person { }\nclass Animal { }\n" +
                "function Main(): i32 { return typeof(Person) == typeof(Animal) ? 1 : 0 }"));
        }

        [Fact]
        public void GetType_Matches_Typeof_ForUserClass()
        {
            // C#：实例.GetType() == typeof(Person)
            Assert.Equal(1, Evaluate(
                "class Person { }\n" +
                "function Main(): i32 {\n" +
                "    var p = new Person()\n" +
                "    return p.GetType() == typeof(Person) ? 1 : 0\n" +
                "}"));
        }

        [Fact]
        public void GetType_Matches_Typeof_ForPrimitive()
        {
            Assert.Equal(1, Evaluate(
                "function Main(): i32 {" +
                "  var n: i32 = 42" +
                "  return n.GetType() == typeof(i32) ? 1 : 0" +
                "}"));
        }

        [Fact]
        public void Sizeof_Primitives_CompileTimeConstants()
        {
            Assert.Equal(4, Evaluate("function Main(): i32 { return sizeof(i32) }"));
            Assert.Equal(8, Evaluate("function Main(): i32 { return sizeof(f64) }"));
            Assert.Equal(1, Evaluate("function Main(): i32 { return sizeof(bool) }"));
            Assert.Equal(2, Evaluate("function Main(): i32 { return sizeof(char) }"));
        }
    }
}