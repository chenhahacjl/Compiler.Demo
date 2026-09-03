using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 函数调用表达式语法：`F(args)` / 泛型显式实参 `Swap&lt;int&gt;(a, b)`（6e-M20）。
    /// </summary>
    public sealed partial class CallExpressionSyntax : ExpressionSyntax
    {
        internal CallExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, TypeArgumentListSyntax? typeArguments, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            Identifier = identifier;
            TypeArguments = typeArguments;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.CallExpression;

        public SyntaxToken Identifier { get; }

        /// <summary>泛型类型实参列表（`Swap<int>(…)`；非泛型调用为 null；6e-M20 首版仅显式实参）。</summary>
        public TypeArgumentListSyntax? TypeArguments { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Identifier;
            if (TypeArguments != null)
            {
                yield return TypeArguments;
            }
            yield return OpenParenthesisToken;
            foreach (var child in Arguments.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenthesisToken;
        }
    }
}

