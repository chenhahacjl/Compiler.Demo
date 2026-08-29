using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Y §6.7 A0：双 <see cref="Compilation"/> 子类分派锁定——
    /// <c>Compilation.Create</c> 按首棵语法树语言返回 <see cref="CocoaCompilation"/> / <see cref="CSharpCompilation"/>（行为等价）。
    /// </summary>
    public class CompilationLanguageDispatchTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void Create_CocoaTree_ReturnsCocoaCompilation()
        {
            var co = SyntaxTree.Parse("function Main(): i32 { return 0 }");
            var compilation = Compilation.Create("Main", References(), co);
            Assert.IsType<CocoaCompilation>(compilation);
        }

        [Fact]
        public void Create_CsTree_ReturnsCSharpCompilation()
        {
            var cs = SyntaxTree.ParseCs("class Program { static void Main() { } }");
            var compilation = Compilation.Create("Main", References(), cs);
            Assert.IsType<CSharpCompilation>(compilation);
        }

        [Fact]
        public void Create_EmptyTrees_FallsBackToCocoa()
        {
            var compilation = Compilation.Create("Main", References());
            Assert.IsType<CocoaCompilation>(compilation);
        }

        [Fact]
        public void GetSemanticModel_WorksOnSubclass()
        {
            var co = SyntaxTree.Parse("function Main(): i32 { return 0 }");
            var compilation = Compilation.Create("Main", References(), co);
            var model = compilation.GetSemanticModel(co);
            Assert.NotNull(model);
            Assert.IsType<CocoaCompilation>(compilation);
        }

        [Fact]
        public void Evaluate_WorksOnCocoaSubclass()
        {
            var co = SyntaxTree.Parse("var x = 21 * 2");
            var compilation = Compilation.Create("Main", References(), co);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.True(!result.Diagnostics.HasErrors());
        }
    }
}