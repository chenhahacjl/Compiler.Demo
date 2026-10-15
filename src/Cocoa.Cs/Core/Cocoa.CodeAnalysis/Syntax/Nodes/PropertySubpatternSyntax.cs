using System.Collections.Generic;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 属性子模式：PropertyName: pattern
    /// </summary>
    public sealed class PropertySubpatternSyntax : SyntaxNode
    {
        public PropertySubpatternSyntax(SyntaxTree syntaxTree, SyntaxToken nameToken, SyntaxToken colonToken, PatternSyntax pattern)
            : base(syntaxTree)
        {
            NameToken = nameToken;
            ColonToken = colonToken;
            Pattern = pattern;
        }

        public override SyntaxKind Kind => SyntaxKind.PropertySubpattern;
        public override int RawKind => (int)Kind;

        public SyntaxToken NameToken { get; }
        public SyntaxToken ColonToken { get; }
        public PatternSyntax Pattern { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return NameToken;
            yield return ColonToken;
            yield return Pattern;
        }
    }
}