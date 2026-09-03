using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// is 绫诲瀷娴嬭瘯琛ㄨ揪寮忥紙6e-M19 M5-b锛夛細expr is TypeName 鈫?bool
    /// </summary>
    public sealed partial class IsExpressionSyntax : ExpressionSyntax
    {
        internal IsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken isKeyword, SyntaxToken typeName)
            : base(syntaxTree)
        {
            Expression = expression;
            IsKeyword = isKeyword;
            TypeName = typeName;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.IsExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken IsKeyword { get; }
        public SyntaxToken TypeName { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return IsKeyword;
            yield return TypeName;
        }
    }
}

