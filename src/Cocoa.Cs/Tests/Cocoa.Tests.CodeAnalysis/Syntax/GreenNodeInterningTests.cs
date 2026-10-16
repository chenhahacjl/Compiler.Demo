using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    /// <summary>
    /// 绿节点 DAG 规范化（2.1）：会话级 intern 池使同一源码中重复的空参数列表/空块/运算符共享同一
    /// GreenNode 实例（引用相等）；红物化 Span 仍正确。位置不参与绿节点相等性——不同位置的结构相同子树共享。
    /// </summary>
    public class GreenNodeInterningTests
    {
        private static GreenNode RootOf(string source) => SyntaxTree.Parse(source).GreenRoot;

        private static IEnumerable<GreenNode> Descendants(GreenNode root)
        {
            for (var i = 0; i < root.SlotCount; i++)
            {
                var slot = root.GetSlot(i);
                if (slot == null)
                {
                    continue;
                }

                yield return slot;
                foreach (var child in Descendants(slot))
                {
                    yield return child;
                }
            }
        }

        private static IEnumerable<GreenToken> DescendantTokens(GreenNode root)
        {
            foreach (var node in Descendants(root))
            {
                if (node is GreenToken token)
                {
                    yield return token;
                }
            }
        }

        [Fact]
        public void RepeatedEmptyArgumentLists_ShareOneGreenNode()
        {
            var root = RootOf("function F() { return 1 } function G() { return 2 }");
            // Cocoa 无独立 ArgumentList 绿节点：`()` 是 Call 的 `(`/`)` token。
            // 空参数列表的共享体现在重复的括号 token（同 kind+text）与空块（见下）。
            var openParens = DescendantTokens(root).Where(t => t.Kind == SyntaxKind.OpenParenthesisToken).ToArray();
            Assert.Equal(2, openParens.Length);
            Assert.Same(openParens[0], openParens[1]);
        }

        [Fact]
        public void RepeatedEmptyBlocks_ShareOneGreenNode()
        {
            // 两个 `{ }` 必须 trivia 上下文完全一致才共享——放在同一父块、均非树尾：
            var root = RootOf("function F() { { } { } }");
            var emptyBlocks = Descendants(root).OfType<GreenNodeWithChildren>()
                .Where(n => n.Kind == SyntaxKind.BlockStatement && n.SlotCount == 2)
                .ToArray();

            Assert.Equal(2, emptyBlocks.Length);
            Assert.Same(emptyBlocks[0], emptyBlocks[1]);
        }

        [Fact]
        public void RepeatedIdenticalSubtrees_ShareOneGreenNode()
        {
            // `(1 + 2) + (1 + 2)`：外二元（x = ...）不同；两个内层 `1 + 2` 含相同 token 与 trivia → 引用共享
            var root = RootOf("var x = (1 + 2) + (1 + 2)");
            var binaries = Descendants(root).OfType<GreenNodeWithChildren>()
                .Where(n => n.Kind == SyntaxKind.BinaryExpression)
                .ToArray();

            Assert.Equal(3, binaries.Length); // 外二元 + 两个内层 1+2
            Assert.Same(binaries[1], binaries[2]);
            Assert.NotSame(binaries[0], binaries[1]);
        }

        [Fact]
        public void RepeatedOperatorTokens_ShareOneGreenToken()
        {
            var root = RootOf("var a = 1 + 2 + 3 + 4");
            var plusTokens = DescendantTokens(root)
                .Where(t => t.Kind == SyntaxKind.PlusToken)
                .ToArray();

            Assert.Equal(3, plusTokens.Length);
            Assert.True(plusTokens.All(t => ReferenceEquals(t, plusTokens[0])));
        }

        [Fact]
        public void RedMaterialization_StillComputesCorrectSpans_FromSharedGreens()
        {
            var tree = SyntaxTree.Parse("function F() { } function G() { } var x = F()");
            var green = tree.GreenRoot;
            var red = green.CreateTypedRed(tree);

            // 复用红节点文本定位（共享绿不影响位置/内容）
            var funcs = red.DescendantNodes().OfType<FunctionDeclarationSyntax>().ToArray();
            var names = funcs.Select(f => f.Identifier.Text).ToArray();
            Assert.Equal(new[] { "F", "G" }, names);
            Assert.Equal(green.ToString(), tree.Text.ToString());
        }

        [Fact]
        public void DifferentText_Tokens_AreNotShared()
        {
            var root = RootOf("var apple = 1 + banana");
            var appleTokens = DescendantTokens(root).Where(t => t.Text == "apple").ToArray();
            var bananaTokens = DescendantTokens(root).Where(t => t.Text == "banana").ToArray();

            Assert.Single(appleTokens);
            Assert.Single(bananaTokens);
            Assert.NotSame(appleTokens[0], bananaTokens[0]);
        }
    }
}