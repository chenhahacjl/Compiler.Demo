using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法工厂：程序化构造绿树（对齐 Roslyn <see cref="Microsoft.CodeAnalysis.SyntaxFactory"/>）。
    /// 与现有红树/解析器并行共存，作为绿层构造入口。
    /// </summary>
    public static class SyntaxFactory
    {
        public static GreenToken Token(SyntaxKind kind)
        {
            return new GreenToken(kind, SyntaxFacts.GetText(kind) ?? kind.ToString());
        }

        public static GreenToken Token(SyntaxKind kind, string text)
        {
            return new GreenToken(kind, text);
        }

        public static GreenToken Identifier(string text)
        {
            return new GreenToken(SyntaxKind.IdentifierToken, text);
        }

        public static GreenNode Node(SyntaxKind kind, params GreenNode?[] slots)
        {
            return new GreenNodeWithChildren(kind, slots.ToImmutableArray());
        }
    }
}