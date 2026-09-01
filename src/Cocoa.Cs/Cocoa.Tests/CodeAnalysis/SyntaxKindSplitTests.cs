using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// P1-E-2 双枚举拆分契约：<see cref="CocoaSyntaxKind"/> / <see cref="CSharpSyntaxKind"/> 两套语言枚举
    /// 的成员集与值必须与共享 <see cref="SyntaxKind"/>（= 绿树 RawKind 值域）逐一对齐——
    /// 保证 <c>(int)Kind == GreenNode.RawKind</c> 跨语言恒成立，绿树存储层语言无关。
    /// </summary>
    public class SyntaxKindSplitTests
    {
        [Fact]
        public void CocoaSyntaxKind_Values_AlignWithSharedSyntaxKind()
        {
            foreach (SyntaxKind shared in Enum.GetValues(typeof(SyntaxKind)))
            {
                Assert.Equal((int)shared, (int)(CocoaSyntaxKind)shared);
            }
        }

        [Fact]
        public void CSharpSyntaxKind_Values_AlignWithSharedSyntaxKind()
        {
            foreach (SyntaxKind shared in Enum.GetValues(typeof(SyntaxKind)))
            {
                Assert.Equal((int)shared, (int)(CSharpSyntaxKind)shared);
            }
        }

        [Fact]
        public void CocoaSyntaxKind_HasSameMemberCountAsShared()
        {
            Assert.Equal(
                Enum.GetNames(typeof(SyntaxKind)).Length,
                Enum.GetNames(typeof(CocoaSyntaxKind)).Length);
        }

        [Fact]
        public void CSharpSyntaxKind_HasSameMemberCountAsShared()
        {
            Assert.Equal(
                Enum.GetNames(typeof(SyntaxKind)).Length,
                Enum.GetNames(typeof(CSharpSyntaxKind)).Length);
        }

        [Fact]
        public void CocoaSyntaxKind_RawKindRoundTripsViaGreenNode()
        {
            // 绿树 RawKind(int) 经语言枚举具名解释后值不变（回环不变量）
            var tree = SyntaxTree.Parse("function Main(): i32 { var x = 1 + 2; return x }");
            foreach (var node in tree.Root.DescendantNodesAndSelf())
            {
                var green = node.ToGreen();
                Assert.Equal((int)(CocoaSyntaxKind)green.Kind, green.RawKind);
            }
        }

        [Fact]
        public void CocoaMappings_RawKindRoundTrip()
        {
            foreach (CocoaSyntaxKind kind in Enum.GetValues(typeof(CocoaSyntaxKind)))
            {
                Assert.Equal(kind, CocoaSyntaxKindMappings.ToCocoaSyntaxKind((int)kind));
                Assert.Equal((int)kind, CocoaSyntaxKindMappings.ToRawKind(kind));
            }
        }

        [Fact]
        public void CSharpMappings_RawKindRoundTrip()
        {
            foreach (CSharpSyntaxKind kind in Enum.GetValues(typeof(CSharpSyntaxKind)))
            {
                Assert.Equal(kind, CSharpSyntaxKindMappings.ToCSharpSyntaxKind((int)kind));
                Assert.Equal((int)kind, CSharpSyntaxKindMappings.ToRawKind(kind));
            }
        }

        [Fact]
        public void CocoaMappings_UnknownRawKind_ReturnsBadToken()
        {
            Assert.Equal(CocoaSyntaxKind.BadToken, CocoaSyntaxKindMappings.ToCocoaSyntaxKind(-1));
            Assert.Equal(CocoaSyntaxKind.BadToken, CocoaSyntaxKindMappings.ToCocoaSyntaxKind(100000));
        }

        [Fact]
        public void CSharpMappings_UnknownRawKind_ReturnsBadToken()
        {
            Assert.Equal(CSharpSyntaxKind.BadToken, CSharpSyntaxKindMappings.ToCSharpSyntaxKind(-1));
            Assert.Equal(CSharpSyntaxKind.BadToken, CSharpSyntaxKindMappings.ToCSharpSyntaxKind(100000));
        }

        [Fact]
        public void CocoaKindAccessor_MatchesSharedKind()
        {
            // P1-E-2c：CocoaKind() 语言枚举访问器与共享 SyntaxKind（= RawKind 具名视图）一致
            var tree = SyntaxTree.Parse("function Main(): i32 { var x = 1 + 2; return x }");
            foreach (var node in tree.Root.DescendantNodesAndSelf())
            {
                Assert.Equal((CocoaSyntaxKind)node.Kind, node.CocoaKind());
                if (node is SyntaxToken token)
                    Assert.Equal((CocoaSyntaxKind)token.Kind, token.CocoaKind());
            }
        }

        [Fact]
        public void CSharpKindAccessor_MatchesSharedKind()
        {
            // P1-E-2c：CSharpKind() 语言枚举访问器与共享 SyntaxKind 一致
            var tree = SyntaxTree.ParseCs("class P { static void Main() { var x = 1 + 2; } }");
            foreach (var node in tree.Root.DescendantNodesAndSelf())
            {
                Assert.Equal((CSharpSyntaxKind)node.Kind, node.CSharpKind());
                if (node is SyntaxToken token)
                    Assert.Equal((CSharpSyntaxKind)token.Kind, token.CSharpKind());
            }
        }
    }
}