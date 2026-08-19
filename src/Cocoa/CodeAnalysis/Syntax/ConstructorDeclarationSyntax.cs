using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 构造函数节点：`public constructor(x: int, y: int) { ... }`
    /// </summary>
    public sealed partial class ConstructorDeclarationSyntax : MemberSyntax
    {
        internal ConstructorDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken constructorKeyword, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken closeParenthesisToken, BlockStatementSyntax body)
            : base(syntaxTree, modifiers)
        {
            ConstructorKeyword = constructorKeyword;
            OpenParenthesisToken = openParenthesisToken;
            Parameters = parameters;
            CloseParenthesisToken = closeParenthesisToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

        public SyntaxToken ConstructorKeyword { get; }
        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public BlockStatementSyntax Body { get; }
    }
}
