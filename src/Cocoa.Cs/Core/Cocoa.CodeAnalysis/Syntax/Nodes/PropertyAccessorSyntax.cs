using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax.Green;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    /// <summary>
    /// 属性访问器：`get { ... }` / `set { ... }`（`;` = 自动），可带可见性修饰符（`private set;`）。
    /// </summary>
    public sealed partial class PropertyAccessorSyntax : SyntaxNode
    {
        internal PropertyAccessorSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken keyword, BlockStatementSyntax? body, SyntaxToken? semicolonToken)
            : base(syntaxTree)
        {
            Modifiers = modifiers;
            Keyword = keyword;
            Body = body;
            SemicolonToken = semicolonToken;
        }

        public override SyntaxKind Kind => SyntaxKind.PropertyAccessor;

        public ImmutableArray<SyntaxToken> Modifiers { get; }
        public SyntaxToken Keyword { get; }
        public BlockStatementSyntax? Body { get; }
        public SyntaxToken? SemicolonToken { get; }

        public bool IsGet => Keyword.Kind == (SyntaxKind)SyntaxKind.GetKeyword;
        public bool IsInit => Keyword.Kind == (SyntaxKind)SyntaxKind.InitKeyword;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return Keyword;
            if (Body != null)
            {
                yield return Body;
            }
            if (SemicolonToken != null)
            {
                yield return SemicolonToken;
            }
        }
    }
}


