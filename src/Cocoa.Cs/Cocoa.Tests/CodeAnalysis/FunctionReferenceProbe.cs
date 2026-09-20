using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 自举测试框架可行性探针（A）：co 编译器函数引用/方法组能力。
    /// </summary>
    public class FunctionReferenceProbe
    {
        [Fact]
        public void MethodGroup_Value_Callable()
        {
            const string source = @"
function Add(a: i32): i32
{
    return a + 1
}

function Main(): i32
{
    let f = Add
    return f(41)
}
";
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(42, result.Value);
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}