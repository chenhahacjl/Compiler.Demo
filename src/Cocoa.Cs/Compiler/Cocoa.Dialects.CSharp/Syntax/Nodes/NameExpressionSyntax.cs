using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 名称表达式语法
    /// </summary>
    public sealed partial class NameExpressionSyntax : ExpressionSyntax
    {
        internal NameExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken identifierToken)
            : base(syntaxTree)
        {
            IdentifierToken = identifierToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.NameExpression;

        public SyntaxToken IdentifierToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return IdentifierToken;
        }
    }
}

