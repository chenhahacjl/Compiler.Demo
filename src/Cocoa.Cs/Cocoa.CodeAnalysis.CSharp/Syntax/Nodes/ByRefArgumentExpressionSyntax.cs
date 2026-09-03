using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// byref 瀹炲弬琛ㄨ揪寮忥細`out x` / `ref arr[i]`锛?e-M23 R1锛涗粎璋冪敤瀹炲弬浣嶅悎娉曪紝缁戝畾灞傛牎楠岋級銆?
    /// </summary>
    public sealed partial class ByRefArgumentExpressionSyntax : ExpressionSyntax
    {
        internal ByRefArgumentExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ByRefArgument;

        public SyntaxToken Keyword { get; }
        public bool IsRef => Keyword.Kind == (SyntaxKind)CSharpSyntaxKind.RefKeyword;

        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Expression;
        }
    }
}


