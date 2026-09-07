using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 命名参数（语言后置件）：实参位 `名: 值`。绑定按形参名把实参重排到对应位置。
    /// 语法形态显式保存（identifier + colon + value）以保留 `ToString() == 源码`。
    /// </summary>
    public sealed partial class NamedArgumentExpressionSyntax : ExpressionSyntax
    {
        internal NamedArgumentExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, SyntaxToken colonToken, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Identifier = identifier;
            ColonToken = colonToken;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.NamedArgument;

        public SyntaxToken Identifier { get; }
        public SyntaxToken ColonToken { get; }
        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Identifier;
            yield return ColonToken;
            yield return Expression;
        }
    }
}