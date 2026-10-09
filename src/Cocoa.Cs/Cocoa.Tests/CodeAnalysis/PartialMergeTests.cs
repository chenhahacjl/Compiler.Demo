using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// partial 部分类：多段 `partial class C { … }` 合并为同一符号、各段成员分别绑定
    /// （`CocoaBinder.Declarations.cs` CreateClassSymbols 合并）。锁住「跨段成员互调可用」。
    /// </summary>
    public class PartialMergeTests
    {
        [Fact]
        public void Evaluator_Partial_SegmentsMergeAndCallAcross()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, SyntaxTree.Parse(@"using System

public partial class Widget
{
    public function A(): i32 { return 1 }
}

public partial class Widget
{
    public function B(): i32 { return A() + 1 }
}

function Main(): i32
{
    var w = new Widget()
    Console.WriteLine(w.A())
    Console.WriteLine(w.B())
    return 0
}"));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal("1\n2\n", writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}