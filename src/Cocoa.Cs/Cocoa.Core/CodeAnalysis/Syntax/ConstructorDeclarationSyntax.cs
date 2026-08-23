using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 构造函数节点：`public constructor(x: int, y: int) : base(...) { ... }`
    /// </summary>
    public sealed partial class ConstructorDeclarationSyntax : MemberSyntax
    {
        internal ConstructorDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken? constructorKeyword, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken closeParenthesisToken, SyntaxToken? initializerKeyword, SeparatedSyntaxList<ExpressionSyntax> initializerArguments, BlockStatementSyntax body)
            : base(syntaxTree, modifiers)
        {
            ConstructorKeyword = constructorKeyword;
            OpenParenthesisToken = openParenthesisToken;
            Parameters = parameters;
            CloseParenthesisToken = closeParenthesisToken;
            InitializerKeyword = initializerKeyword;
            InitializerArguments = initializerArguments;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

        public SyntaxToken? ConstructorKeyword { get; }
        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        /// <summary>构造链关键字（`: base` / `: this`；null = 无显式链）。</summary>
        public SyntaxToken? InitializerKeyword { get; }

        /// <summary>构造链实参。</summary>
        public SeparatedSyntaxList<ExpressionSyntax> InitializerArguments { get; }

        public BlockStatementSyntax Body { get; }
    }
}
