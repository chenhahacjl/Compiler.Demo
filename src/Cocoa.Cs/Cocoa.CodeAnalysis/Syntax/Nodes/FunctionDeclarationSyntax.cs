using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class FunctionDeclarationSyntax : MemberSyntax
    {
        internal FunctionDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers, SyntaxToken? functionKeyword, SyntaxToken identifier, TypeParameterListSyntax? typeParameters, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken closeParenthesisToken, TypeClauseSyntax? type, BlockStatementSyntax? body, ExternMetadataSyntax? externMetadata = null, ImmutableArray<WhereClauseSyntax>? whereClauses = null)
            : base(syntaxTree, modifiers)
        {
            Attributes = attributes;
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

        /// <summary>声明前 attribute 列表（6e-M32 Tier-2：函数级 `[Test]` 等）。</summary>
        public ImmutableArray<AttributeSyntax> Attributes { get; }

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

        /// <summary>运算符重载/转换运算符声明（`operator +` / `implicit operator T` / `explicit operator T`）。
        /// 判定依据：<see cref="FunctionKeyword"/> 槽存的是 operator/implicit/explicit 而非 `function`。</summary>
        public bool IsOperatorDeclaration =>
            FunctionKeyword?.Kind == SyntaxKind.OperatorKeyword ||
            FunctionKeyword?.Kind == SyntaxKind.ImplicitKeyword ||
            FunctionKeyword?.Kind == SyntaxKind.ExplicitKeyword;

        /// <summary>隐式转换运算符声明（`implicit operator T(v: S)`）。</summary>
        public bool IsImplicitConversion => FunctionKeyword?.Kind == SyntaxKind.ImplicitKeyword;

        /// <summary>显式转换运算符声明（`explicit operator T(v: S)`）。</summary>
        public bool IsExplicitConversion => FunctionKeyword?.Kind == SyntaxKind.ExplicitKeyword;

        /// <summary>转换运算符声明（implicit 或 explicit）——单参、返回目标类型。</summary>
        public bool IsConversionOperator => IsImplicitConversion || IsExplicitConversion;

        /// <summary>非转换运算符声明时为运算符 token（`+`/`-`/…）；转换运算符与普通函数为 null。</summary>
        public SyntaxToken? OperatorToken => IsOperatorDeclaration && !IsConversionOperator ? Identifier : null;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var attribute in Attributes)
            {
                yield return attribute;
            }
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

