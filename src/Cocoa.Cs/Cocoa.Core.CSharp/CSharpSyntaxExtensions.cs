using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// C# 侧语言枚举访问器（P1-E-2c）：经共享 <see cref="SyntaxNode.Kind"/>（= RawKind 具名视图）
    /// 与 <see cref="CSharpSyntaxKindMappings"/> 得到 C# 语言枚举。当前行为等价于强转；
    /// 未来节点类迁入语言库后，这些扩展成为 C# 侧 kind 的唯一出口。
    /// </summary>
    public static class CSharpSyntaxExtensions
    {
        /// <summary>节点（含 token/trivia）的 C# 语法类型。</summary>
        public static CSharpSyntaxKind CSharpKind(this SyntaxNode node)
            => CSharpSyntaxKindMappings.ToCSharpSyntaxKind(node.Kind);

        /// <summary>token 的 C# 语法类型。</summary>
        public static CSharpSyntaxKind CSharpKind(this SyntaxToken token)
            => CSharpSyntaxKindMappings.ToCSharpSyntaxKind(token.Kind);
    }
}