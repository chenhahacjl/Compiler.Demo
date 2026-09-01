using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Y §6.7 A0：双 <see cref="Compilation"/> 子类分派锁定——
    /// <c>Compilation.Create</c> 按首棵语法树语言返回 <see cref="CocoaCompilation"/> / <see cref="CSharpCompilation"/>（行为等价）。
    /// P1-B：<see cref="Language.CreateBinder"/> 工厂按语言分派 <see cref="CocoaBinder"/> / <see cref="CSharpBinder"/>。
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

        [Fact]
        public void CocoaLanguage_LivesInCocoaCoreCocoaAssembly()
        {
            // Y-A3-4：CocoaLanguage 随 CO L1 迁入 Cocoa.Core.Cocoa；核心经 Language.Cocoa 反射装载解析
            var cocoa = Language.Cocoa;
            Assert.Equal("Cocoa.Core.Cocoa", cocoa.GetType().Assembly.GetName().Name);
            Assert.NotEqual(typeof(SyntaxTree).Assembly.GetName().Name, cocoa.GetType().Assembly.GetName().Name);
        }

        [Fact]
        public void LanguageCocoa_Resolves_AndParses()
        {
            // Y-A3-4 接缝：Language.Cocoa 解析到新程序集且默认 CO 解析路径可用
            var cocoa = Language.Cocoa;
            var tree = SyntaxTree.Parse("function Main(): i32 { return 0 }");
            Assert.NotNull(tree.Root);
            Assert.Same(cocoa, tree.Language);
        }

        [Fact]
        public void CocoaLanguage_CreateBinder_ReturnsCocoaBinder()
        {
            // P1-B：Language.CreateBinder 工厂按语言分派——CO 语言产出 CocoaBinder
            var binder = Language.Cocoa.CreateBinder(
                isScript: false, parent: null, function: null,
                references: ImmutableArray<string>.Empty, usingNamespaces: ImmutableArray<string>.Empty,
                Language.Cocoa.LookupBuiltinType);
            Assert.IsType<CocoaBinder>(binder);
        }

        [Fact]
        public void CSharpLanguage_CreateBinder_ReturnsCSharpBinder()
        {
            // P1-B：Language.CreateBinder 工厂按语言分派——C# 语言产出 CSharpBinder
            var binder = Language.CSharp.CreateBinder(
                isScript: false, parent: null, function: null,
                references: ImmutableArray<string>.Empty, usingNamespaces: ImmutableArray<string>.Empty,
                Language.CSharp.LookupBuiltinType);
            Assert.IsType<CSharpBinder>(binder);
        }
    }
}