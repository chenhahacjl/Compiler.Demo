using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
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

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.FunctionDeclaration;

        public SyntaxToken? FunctionKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>泛型方法类型参数列表 `&lt;T&gt;`（6e-M20；非泛型方法为 null）。</summary>
        public TypeParameterListSyntax? TypeParameters { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public TypeClauseSyntax? Type { get; }
        public BlockStatementSyntax? Body { get; }

        /// <summary>extern 元数据子句（`extern(entry=…, charset=…)`，6e-M17 Step 5）；非 extern 函数为 null。</summary>
        public ExternMetadataSyntax? ExternMetadata { get; }

        /// <summary>泛型约束子句列表（`where T: ...`，6e-M20）。</summary>
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

