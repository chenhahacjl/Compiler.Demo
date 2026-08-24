namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// is 类型测试表达式（6e-M19 M5-b）：expr is TypeName → bool
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

        public override SyntaxKind Kind => SyntaxKind.IsExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken IsKeyword { get; }
        public SyntaxToken TypeName { get; }
    }
}
