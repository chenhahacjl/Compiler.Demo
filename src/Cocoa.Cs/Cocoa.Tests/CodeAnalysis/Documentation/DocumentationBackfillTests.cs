using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Documentation
{
    public class DocumentationBackfillTests
    {
        private const string Source = @"using System

/// <summary>顶层函数</summary>
function Top(): i32 { return 0 }

/// <summary>类</summary>
public class C
{
    /// <summary>字段</summary>
    public field F: i32

    /// <summary>属性</summary>
    public property P: i32 { get; set; }

    /// <summary>方法</summary>
    public function M(): i32 { return 0 }

    /// <summary>构造</summary>
    public constructor() { }

    /// <summary>事件</summary>
    public event onHit: () -> void
}

/// <summary>枚举</summary>
public enum E { A, B }

/// <summary>委托</summary>
public delegate void D(x: i32)";

        private static Compilation Compile() => Compilation.Create(SyntaxTree.Parse(Source));

        [Fact]
        public void TopLevelFunction_DocumentationText()
        {
            var fn = Compile().GlobalScope.Functions.Single(f => f.Name == "Top");
            Assert.NotNull(fn.DocumentationText);
            Assert.Contains("顶层函数", fn.DocumentationText);
        }

        [Fact]
        public void ClassAndMembers_DocumentationText()
        {
            var calc = Compile().GlobalScope.Classes.Single(c => c.Name == "C");

            Assert.Contains("类", calc.DocumentationText);
            Assert.Contains("字段", calc.GetDeclaredField("F")!.DocumentationText);
            Assert.Contains("属性", calc.GetDeclaredProperty("P")!.DocumentationText);
            Assert.Contains("方法", calc.GetDeclaredMethod("M")!.DocumentationText);
            Assert.Contains("构造", calc.Methods.Single(m => m.IsConstructor && m.Parameters.Length == 0).DocumentationText);
            Assert.Contains("事件", calc.Events.Single(e => e.Name == "onHit").DocumentationText);
        }

        [Fact]
        public void EnumAndDelegate_DocumentationText()
        {
            var compilation = Compile();

            var en = compilation.GlobalScope.Enums.Single(e => e.Name == "E");
            Assert.Contains("枚举", en.DocumentationText);

            var del = compilation.GlobalScope.Classes.Single(c => c.Name == "D");
            Assert.Contains("委托", del.DocumentationText);
        }
    }
}
