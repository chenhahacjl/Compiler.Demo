using System.Collections.Generic;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>extern 元数据键值对：`key = value`（如 `entry = MessageBoxA` / `charset = ansi`）。</summary>
    public sealed partial class ExternMetadataArgumentSyntax : SyntaxNode
    {
        internal ExternMetadataArgumentSyntax(SyntaxTree syntaxTree, SyntaxToken key, SyntaxToken equalsToken, SyntaxToken value)
            : base(syntaxTree)
        {
            Key = key;
            EqualsToken = equalsToken;
            Value = value;
        }

        public override SyntaxKind Kind => SyntaxKind.ExternMetadataArgument;

        public SyntaxToken Key { get; }

        public SyntaxToken EqualsToken { get; }

        public SyntaxToken Value { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Key;
            yield return EqualsToken;
            yield return Value;
        }
    }
}