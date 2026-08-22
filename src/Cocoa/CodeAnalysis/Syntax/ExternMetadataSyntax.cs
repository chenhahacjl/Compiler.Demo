using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// extern 元数据子句：`extern(entry = MessageBoxA, charset = ansi)`（括号可选，命名键值，逗号分隔）。
    /// 6e-M17 Step 5：DLL 导出名别名（entry）+ 编码格式（charset）。
    /// </summary>
    public sealed partial class ExternMetadataSyntax : SyntaxNode
    {
        internal ExternMetadataSyntax(SyntaxTree syntaxTree, SyntaxToken externKeyword, SyntaxToken? openParenthesisToken, ImmutableArray<ExternMetadataArgumentSyntax> arguments, SyntaxToken? closeParenthesisToken)
            : base(syntaxTree)
        {
            ExternKeyword = externKeyword;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ExternMetadata;

        public SyntaxToken ExternKeyword { get; }

        public SyntaxToken? OpenParenthesisToken { get; }

        public ImmutableArray<ExternMetadataArgumentSyntax> Arguments { get; }

        public SyntaxToken? CloseParenthesisToken { get; }
    }

    /// <summary>extern 元数据键值对：`key = value`（如 `entry = MessageBoxA` / `charset = ansi`）。</summary>
    public sealed partial class ExternMetadataArgumentSyntax : SyntaxNode
    {
        internal ExternMetadataArgumentSyntax(SyntaxTree syntaxTree, SyntaxToken key, SyntaxToken equalsToken, SyntaxToken value)
            : base(syntaxTree)
        {
            Key = key;
            EqualsToken = equalsToken;
            Value = value;
        }

        public override SyntaxKind Kind => SyntaxKind.ExternMetadataArgument;

        public SyntaxToken Key { get; }

        public SyntaxToken EqualsToken { get; }

        public SyntaxToken Value { get; }
    }
}
