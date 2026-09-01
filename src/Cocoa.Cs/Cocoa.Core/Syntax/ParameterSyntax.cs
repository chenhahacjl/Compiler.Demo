using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class ParameterSyntax : SyntaxNode
    {
        internal ParameterSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, TypeClauseSyntax type)
            : this(syntaxTree, modifier: null, identifier, type)
        {
        }

        internal ParameterSyntax(SyntaxTree syntaxTree, SyntaxToken? modifier, SyntaxToken identifier, TypeClauseSyntax type)
            : base(syntaxTree)
        {
            Modifier = modifier;
            Identifier = identifier;
            Type = type;
        }

        public override SyntaxKind Kind => SyntaxKind.Parameter;

        public SyntaxToken? Modifier { get; }
        public bool IsByRef => Modifier != null;

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }

        /// <summary>鏄惁涓?C# 鏂硅█鍙傛暟褰㈡€侊紙`绫诲瀷 鍚嶇О`锛岀被鍨嬪墠缃級锛汣ocoa 鎭掍负 `鍚嶇О: 绫诲瀷`锛堝悕绉板墠缃級銆?/summary>
        private bool IsTypeFirst => SyntaxTree.Language.ParametersAreTypeFirst;

        /// <summary>绾⑩啋缁挎簮搴忓寲锛圥0锛夛細鎸夋柟瑷€淇濈暀 `[out|ref] 绫诲瀷 鍚嶇О`锛?cs锛夋垨 `[out|ref] 鍚嶇О: 绫诲瀷`锛?co锛?
        /// 鐨勬簮鐮侀『搴忥紝淇濊瘉 `GreenRoot.ToString() == 婧愮爜`銆?/summary>
        public override GreenNode ToGreen()
        {
            var slots = ImmutableArray.CreateBuilder<GreenNode?>();

            if (Modifier != null)
            {
                slots.Add(Modifier.ToGreen());
            }

            if (IsTypeFirst)
            {
                slots.Add(Type.ToGreen());
                slots.Add(Identifier.ToGreen());
            }
            else
            {
                slots.Add(Identifier.ToGreen());
                slots.Add(Type.ToGreen());
            }

            return new GreenNodeWithChildren(Kind, slots.ToImmutable());
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (Modifier != null)
            {
                yield return Modifier;
            }
            yield return Identifier;
            yield return Type;
        }
    }
}
