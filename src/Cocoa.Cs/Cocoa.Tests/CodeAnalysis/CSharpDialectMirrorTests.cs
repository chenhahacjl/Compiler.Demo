using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>B3：C# 方言 parser 镜像——switch 表达式降级 + record 展开在 C# 方言可用。</summary>
    public class CSharpDialectMirrorTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string Run(string text)
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var tree = SyntaxTree.ParseCs(text);
                var compilation = Compilation.Create("Main", References(), tree);
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                return writer.ToString().Replace("\r\n", "\n");
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void CSharpSwitchExpression()
        {
            var output = Run(@"using System;
class Program
{
    static void Main()
    {
        var x = 2;
        var label = x switch { 1 => ""one"", 2 => ""two"", _ => ""other"" };
        Console.WriteLine(label);
        var v = x switch { 1 => 5, _ => 3 };
        Console.WriteLine(v);
    }
}");
            Assert.Equal("two\n3\n", output);
        }

        [Fact]
        public void CSharpRecordExpandsToClass()
        {
            var tree = SyntaxTree.ParseCs("record Person(name: string, city: string);");
            var text = tree.Root.ToString();
            Assert.True(text.Contains("ClassDeclaration", StringComparison.Ordinal), $"record 未展开为类声明。actual='{text}'");
        }
    }
}