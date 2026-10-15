using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    /// <summary>
    /// as 类型转换表达式（6e-M19 M5-b）：expr as TypeName → TypeName（失败得 null）。
    /// </summary>
    public sealed partial class AsExpressionSyntax : ExpressionSyntax
    {
        internal AsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken asKeyword, SyntaxToken typeName)
            : base(syntaxTree)
        {
            Expression = expression;
            AsKeyword = asKeyword;
            TypeName = typeName;
        }

        public override SyntaxKind Kind => SyntaxKind.AsExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken AsKeyword { get; }
        public SyntaxToken TypeName { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return AsKeyword;
            yield return TypeName;
        }
    }
}

