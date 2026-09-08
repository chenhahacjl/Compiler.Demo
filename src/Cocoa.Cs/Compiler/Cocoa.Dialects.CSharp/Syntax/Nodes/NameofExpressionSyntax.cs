using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// nameof(X) 编译期字符串常量表达式。
    /// </summary>
    public sealed partial class NameofExpressionSyntax : ExpressionSyntax
    {
        internal NameofExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken nameofKeyword, SyntaxToken openParenthesis, ExpressionSyntax argument, SyntaxToken closeParenthesis)
            : base(syntaxTree)
        {
            NameofKeyword = nameofKeyword;
            OpenParenthesis = openParenthesis;
            Argument = argument;
            CloseParenthesis = closeParenthesis;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.NameofExpression;

        public SyntaxToken NameofKeyword { get; }
        public SyntaxToken OpenParenthesis { get; }
        public ExpressionSyntax Argument { get; }
        public SyntaxToken CloseParenthesis { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return NameofKeyword;
            yield return OpenParenthesis;
            yield return Argument;
            yield return CloseParenthesis;
        }
    }
}
