using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 璧嬪€艰〃杈惧紡璇硶锛堢洰鏍囦负鍚嶇О/鏁扮粍绱㈠紩/鎴愬憳琛ㄨ揪寮忥級
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

