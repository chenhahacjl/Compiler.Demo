using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 娉涘瀷绫诲瀷璇硶锛?e-M20锛夛細`List&lt;int&gt;` / `List&lt;List&lt;int&gt;&gt;`銆?
    /// 鍩虹被 Identifier 鎵胯浇绫诲瀷鍚嶏紙涓?ArrayTypeClauseSyntax 鍚屾瀯锛夛紝TypeArguments 涓哄疄鍙傚垪琛ㄣ€?
    /// </summary>
    public sealed partial class GenericTypeClauseSyntax : TypeClauseSyntax
    {
        internal GenericTypeClauseSyntax(SyntaxTree syntaxTree, SyntaxToken? colonToken, SyntaxToken identifier, SyntaxToken lessThanToken, ImmutableArray<TypeClauseSyntax> typeArguments, SyntaxToken greaterThanToken)
            : base(syntaxTree, colonToken, identifier)
        {
            LessThanToken = lessThanToken;
            TypeArguments = typeArguments;
            GreaterThanToken = greaterThanToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.GenericTypeClause;

        public SyntaxToken LessThanToken { get; }
        public ImmutableArray<TypeClauseSyntax> TypeArguments { get; }
        public SyntaxToken GreaterThanToken { get; }

        /// <summary>璋冭瘯鏄剧ず鍚嶏細`List<int>`锛堝惈宓屽/鏁扮粍锛夈€?/summary>
        public string DisplayName
        {
            get
            {
                var builder = new System.Text.StringBuilder();
                builder.Append(Identifier.Text);
                builder.Append('<');
                for (var i = 0; i < TypeArguments.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Format(TypeArguments[i]));
                }

                builder.Append('>');
                return builder.ToString();
            }
        }

        private static string Format(TypeClauseSyntax type)
        {
            if (type is GenericTypeClauseSyntax generic)
            {
                return generic.DisplayName;
            }

            if (type is ArrayTypeClauseSyntax array)
            {
                return Format(array.ElementType) + "[]";
            }

            return type.Identifier.Text;
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (ColonToken != null)
            {
                yield return ColonToken;
            }
            yield return Identifier;
            yield return LessThanToken;
            foreach (var child in TypeArguments)
            {
                yield return child;
            }
            yield return GreaterThanToken;
        }
    }
}

