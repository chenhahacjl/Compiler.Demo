using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// Cocoa 侧语言枚举访问器（P1-E-2c）：经共享 <see cref="SyntaxNode.Kind"/>（= RawKind 具名视图）
    /// 与 <see cref="CocoaSyntaxKindMappings"/> 得到 CO 语言枚举。当前行为等价于强转；
    /// 未来节点类迁入语言库后，这些扩展成为 CO 侧 kind 的唯一出口。
    /// </summary>
    public static class CocoaSyntaxExtensions
    {
        /// <summary>节点（含 token/trivia）的 Cocoa 语法类型。</summary>
        public static CocoaSyntaxKind CocoaKind(this SyntaxNode node)
            => CocoaSyntaxKindMappings.ToCocoaSyntaxKind((SyntaxKind)node.RawKind);

        /// <summary>token 的 Cocoa 语法类型。</summary>
        public static CocoaSyntaxKind CocoaKind(this SyntaxToken token)
            => CocoaSyntaxKindMappings.ToCocoaSyntaxKind(token.Kind);
    }
}