using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class FunctionDeclarationSyntax : MemberSyntax
    {
        internal FunctionDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken? functionKeyword, SyntaxToken identifier, TypeParameterListSyntax? typeParameters, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken closeParenthesisToken, TypeClauseSyntax? type, BlockStatementSyntax? body, ExternMetadataSyntax? externMetadata = null, ImmutableArray<WhereClauseSyntax>? whereClauses = null)
            : base(syntaxTree, modifiers)
        {
            FunctionKeyword = functionKeyword;
            Identifier = identifier;
            TypeParameters = typeParameters;
            OpenParenthesisToken = openParenthesisToken;
            Parameters = parameters;
            CloseParenthesisToken = closeParenthesisToken;
            Type = type;
            Body = body;
            ExternMetadata = externMetadata;
            WhereClauses = whereClauses ?? ImmutableArray<WhereClauseSyntax>.Empty;
        }

        public override SyntaxKind Kind => SyntaxKind.FunctionDeclaration;

        public SyntaxToken? FunctionKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>娉涘瀷鏂规硶绫诲瀷鍙傛暟鍒楄〃 `&lt;T&gt;`锛?e-M20锛涢潪娉涘瀷鏂规硶涓?null锛夈€?/summary>
        public TypeParameterListSyntax? TypeParameters { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public TypeClauseSyntax? Type { get; }
        public BlockStatementSyntax? Body { get; }

        /// <summary>extern 鍏冩暟鎹瓙鍙ワ紙`extern(entry=鈥? charset=鈥?`锛?e-M17 Step 5锛夛紱闈?extern 鍑芥暟涓?null銆?/summary>
        public ExternMetadataSyntax? ExternMetadata { get; }

        /// <summary>娉涘瀷绾︽潫瀛愬彞鍒楄〃锛坄where T: ...`锛?e-M20锛夈€?/summary>
        public ImmutableArray<WhereClauseSyntax> WhereClauses { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            if (FunctionKeyword != null)
            {
                yield return FunctionKeyword;
            }
            yield return Identifier;
            if (TypeParameters != null)
            {
                yield return TypeParameters;
            }
            yield return OpenParenthesisToken;
            foreach (var child in Parameters.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenthesisToken;
            if (Type != null)
            {
                yield return Type;
            }
            if (Body != null)
            {
                yield return Body;
            }
            if (ExternMetadata != null)
            {
                yield return ExternMetadata;
            }
            foreach (var child in WhereClauses)
            {
                yield return child;
            }
        }
    }
}
