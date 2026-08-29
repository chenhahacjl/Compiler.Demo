using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Y A3-0：节点层语言归属契约——当前共享节点模型下，明确互斥的节点 kind 不得出现在对方方言的合法树中
    /// （CO `for i = 0 to n` → ForStatement；C# `for(;;)` → CSStyleForStatement）。
    /// 锁定现状即 A3 拆分蓝图的基线（详见 蓝图 §6.7.10）。
    /// </summary>
    public class SyntaxLanguageOwnershipTests
    {
        private static ImmutableList<SyntaxKind> KindsOf(string source, bool cs)
        {
            var tree = cs ? SyntaxTree.ParseCs(source) : SyntaxTree.Parse(source);
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("; ", tree.Diagnostics.Select(d => d.Message)));
            var root = tree.Root;
            return root.DescendantNodesAndSelf().Select(n => n.Kind).ToImmutableList();
        }

        [Fact]
        public void CoRangeFor_NotCSharpStyle()
        {
            var kinds = KindsOf("function Main(): i32 { for i = 0 to 3 { } return 0 }", cs: false);
            Assert.Contains(SyntaxKind.ForStatement, kinds);
            Assert.DoesNotContain(SyntaxKind.CSStyleForStatement, kinds);
        }

        [Fact]
        public void CsStyleFor_NotRange()
        {
            var kinds = KindsOf("class P { static void Main() { for (int i = 0; i < 3; i++) { } } }", cs: true);
            Assert.Contains(SyntaxKind.CSStyleForStatement, kinds);
            Assert.DoesNotContain(SyntaxKind.ForStatement, kinds);
        }

        [Fact]
        public void CoOwnedKeywords_RejectedInCs()
        {
            // CO 专属关键字：function / let / property / constructor / extends / facade / syscall / import / to / step
            var cs = SyntaxTree.ParseCs("function F() { let x = 1 }");
            Assert.True(cs.Diagnostics.Any(d => d.IsError), "C# 方言应拒绝 CO 专属关键字（function/let）。");
        }

        [Fact]
        public void CoParses_OwnedForms()
        {
            var tree = SyntaxTree.Parse("class Foo extends Object { public property X: i32 { get { return 0 } } public constructor() { } }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("; ", tree.Diagnostics.Select(d => d.Message)));
        }
    }
}
