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
    }
}
