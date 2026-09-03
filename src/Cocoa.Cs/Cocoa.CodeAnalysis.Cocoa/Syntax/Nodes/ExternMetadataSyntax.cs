using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// extern 鍏冩暟鎹瓙鍙ワ細`extern(entry = MessageBoxA, charset = ansi)`锛堟嫭鍙峰彲閫夛紝鍛藉悕閿€硷紝閫楀彿鍒嗛殧锛夈€?
    /// 6e-M17 Step 5锛欴LL 瀵煎嚭鍚嶅埆鍚嶏紙entry锛? 缂栫爜鏍煎紡锛坈harset锛夈€?
    /// </summary>
    public sealed partial class ExternMetadataSyntax : CocoaSyntaxNode
    {
        internal ExternMetadataSyntax(SyntaxTree syntaxTree, SyntaxToken externKeyword, SyntaxToken? openParenthesisToken, ImmutableArray<ExternMetadataArgumentSyntax> arguments, SyntaxToken? closeParenthesisToken)
            : base(syntaxTree)
        {
            ExternKeyword = externKeyword;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ExternMetadata;

        public SyntaxToken ExternKeyword { get; }

        public SyntaxToken? OpenParenthesisToken { get; }

        public ImmutableArray<ExternMetadataArgumentSyntax> Arguments { get; }

        public SyntaxToken? CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return ExternKeyword;
            if (OpenParenthesisToken != null)
            {
                yield return OpenParenthesisToken;
            }
            foreach (var child in Arguments)
            {
                yield return child;
            }
            if (CloseParenthesisToken != null)
            {
                yield return CloseParenthesisToken;
            }
        }
    }

    /// <summary>extern 鍏冩暟鎹敭鍊煎锛歚key = value`锛堝 `entry = MessageBoxA` / `charset = ansi`锛夈€?/summary>
    public sealed partial class ExternMetadataArgumentSyntax : CocoaSyntaxNode
    {
        internal ExternMetadataArgumentSyntax(SyntaxTree syntaxTree, SyntaxToken key, SyntaxToken equalsToken, SyntaxToken value)
            : base(syntaxTree)
        {
            Key = key;
            EqualsToken = equalsToken;
            Value = value;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ExternMetadataArgument;

        public SyntaxToken Key { get; }

        public SyntaxToken EqualsToken { get; }

        public SyntaxToken Value { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Key;
            yield return EqualsToken;
            yield return Value;
        }
    }
}

