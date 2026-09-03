using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 对象创建表达式：`new Foo(args)` / 泛型 `new List&lt;int&gt;(args)`（6e-M20）。
    /// </summary>
    public sealed partial class ObjectCreationExpressionSyntax : ExpressionSyntax
    {
        internal ObjectCreationExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken newKeyword, SyntaxToken identifier, TypeArgumentListSyntax? typeArguments, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            NewKeyword = newKeyword;
            Identifier = identifier;
            TypeArguments = typeArguments;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ObjectCreationExpression;

        public SyntaxToken NewKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>泛型类型实参列表（`new List<int>(…)`；非泛型创建为 null；6e-M20）。</summary>
        public TypeArgumentListSyntax? TypeArguments { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return NewKeyword;
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

