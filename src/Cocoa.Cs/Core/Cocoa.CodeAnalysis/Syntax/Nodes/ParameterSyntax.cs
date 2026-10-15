using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax.Green;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    public sealed partial class ParameterSyntax : SyntaxNode
    {
        internal ParameterSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, TypeClauseSyntax type)
            : this(syntaxTree, modifier: null, identifier, type)
        {
        }

        internal ParameterSyntax(SyntaxTree syntaxTree, SyntaxToken? modifier, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken? equalsToken = null, ExpressionSyntax? defaultValue = null)
            : base(syntaxTree)
        {
            Modifier = modifier;
            Identifier = identifier;
            Type = type;
            EqualsToken = equalsToken;
            DefaultValue = defaultValue;
        }

        public override SyntaxKind Kind => SyntaxKind.Parameter;

        public SyntaxToken? Modifier { get; }
        public bool IsByRef => Modifier?.Kind is SyntaxKind.OutKeyword or SyntaxKind.RefKeyword;
        public bool IsParams => Modifier?.Kind == SyntaxKind.ParamsKeyword;
        public bool IsThis => Modifier?.Kind == SyntaxKind.ThisKeyword;

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }

        /// <summary>可选参数默认值（语言后置件）：`x: i32 = 10` 的 `= 10` 部分。</summary>
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? DefaultValue { get; }
        public bool HasDefaultValue => DefaultValue != null;

        /// <summary>红→绿源序化（P0）：`[out|ref] 名称: 类型` 的源码顺序，保证 `GreenRoot.ToString() == 源码`。</summary>
        public override GreenNode ToGreen()
        {
            return ToGreen(new GreenNodeInterner());
        }

        /// <summary>红→绿（2.1 DAG 规范化）：参数槽序与 <see cref="ToGreen()"/> 一致，经 intern 池规范化。</summary>
        internal override GreenNode ToGreen(GreenNodeInterner interner)
        {
            var slots = ImmutableArray.CreateBuilder<GreenNode?>();

            if (Modifier != null)
            {
                slots.Add(Modifier.ToGreen(interner));
            }

            slots.Add(Identifier.ToGreen(interner));
            slots.Add(Type.ToGreen(interner));

            if (EqualsToken != null)
            {
                slots.Add(EqualsToken.ToGreen(interner));
                if (DefaultValue != null)
                {
                    slots.Add(DefaultValue.ToGreen(interner));
                }
            }

            return interner.InternNode((SyntaxKind)Kind, slots.ToImmutable());
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (Modifier != null)
            {
                yield return Modifier;
            }
            yield return Identifier;
            yield return Type;
            if (EqualsToken != null)
            {
                yield return EqualsToken;
            }
            if (DefaultValue != null)
            {
                yield return DefaultValue;
            }
        }
    }
}

