using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
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

        public bool IsGet => Keyword.Kind == SyntaxKind.GetKeyword;
    }
}
