namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 属性访问器：`get { ... }` / `set { ... }`（`;` = 自动）。
    /// </summary>
    public sealed partial class PropertyAccessorSyntax : SyntaxNode
    {
        internal PropertyAccessorSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, BlockStatementSyntax? body, SyntaxToken? semicolonToken)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Body = body;
            SemicolonToken = semicolonToken;
        }

        public override SyntaxKind Kind => SyntaxKind.PropertyAccessor;

        public SyntaxToken Keyword { get; }
        public BlockStatementSyntax? Body { get; }
        public SyntaxToken? SemicolonToken { get; }

        public bool IsGet => Keyword.Kind == SyntaxKind.GetKeyword;
    }
}
