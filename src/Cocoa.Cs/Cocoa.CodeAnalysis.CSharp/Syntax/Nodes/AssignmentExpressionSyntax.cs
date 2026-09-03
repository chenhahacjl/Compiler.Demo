using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 赋值表达式语法（目标为名称/数组索引/成员表达式）
    /// </summary>
    public sealed partial class AssignmentExpressionSyntax : ExpressionSyntax
    {
        internal AssignmentExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax target, SyntaxToken assignmentToken, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Target = target;
            AssignmentToken = assignmentToken;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.AssignmentExpression;

        public ExpressionSyntax Target { get; }
        public SyntaxToken AssignmentToken { get; }
        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Target;
            yield return AssignmentToken;
            yield return Expression;
        }
    }
}

