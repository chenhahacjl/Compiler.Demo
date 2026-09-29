using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 类型运算表达式：<c>typeof(T)</c> / <c>sizeof(T)</c>。
    /// 两者都是「前缀关键字 + 括号内类型实参」，故合为一个节点，用
    /// <see cref="OperatorToken"/> 区分；二者在绑定期产出不同的 Bound 节点。
    /// </summary>
    public sealed class TypeOperatorExpressionSyntax : ExpressionSyntax
    {
        public TypeOperatorExpressionSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken operatorToken,
            SyntaxToken openParenthesisToken,
            TypeClauseSyntax type,
            SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            OperatorToken = operatorToken;
            OpenParenthesisToken = openParenthesisToken;
            Type = type;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override SyntaxKind Kind => SyntaxKind.TypeOperatorExpression;

        /// <summary><c>typeof</c> 或 <c>sizeof</c>。</summary>
        public SyntaxToken OperatorToken { get; }

        public SyntaxToken OpenParenthesisToken { get; }

        /// <summary>类型实参（<c>typeof(V)</c> 中的 <c>V</c>）。</summary>
        public TypeClauseSyntax Type { get; }

        public SyntaxToken CloseParenthesisToken { get; }

        /// <summary>是否为 <c>typeof</c>。</summary>
        public bool IsTypeOf => OperatorToken.Kind == SyntaxKind.TypeofKeyword;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OperatorToken;
            yield return OpenParenthesisToken;
            yield return Type;
            yield return CloseParenthesisToken;
        }
    }
}
