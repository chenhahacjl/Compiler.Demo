using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ConstructorDeclaration;

        public SyntaxToken? ConstructorKeyword { get; }
        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        /// <summary>鏋勯€犻摼鍏抽敭瀛楋紙`: base` / `: this`锛沶ull = 鏃犳樉寮忛摼锛夈€?/summary>
        public SyntaxToken? InitializerKeyword { get; }

        /// <summary>鏋勯€犻摼瀹炲弬銆?/summary>
        public SeparatedSyntaxList<ExpressionSyntax> InitializerArguments { get; }

        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            if (ConstructorKeyword != null)
            {
                yield return ConstructorKeyword;
            }
            yield return OpenParenthesisToken;
            foreach (var child in Parameters.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenthesisToken;
            if (InitializerKeyword != null)
            {
                yield return InitializerKeyword;
            }
            foreach (var child in InitializerArguments.GetWithSeparators())
            {
                yield return child;
            }
            yield return Body;
        }
    }
}

