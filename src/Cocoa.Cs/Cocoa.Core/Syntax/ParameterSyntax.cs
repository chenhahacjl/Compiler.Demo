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

        /// <summary>是否为 C# 方言参数形态（`类型 名称`，类型前置）；Cocoa 恒为 `名称: 类型`（名称前置）。</summary>
        private bool IsTypeFirst => SyntaxTree.Language.ParametersAreTypeFirst;

        /// <summary>红→绿源序化（P0）：按方言保留 `[out|ref] 类型 名称`（.cs）或 `[out|ref] 名称: 类型`（.co）
        /// 的源码顺序，保证 `GreenRoot.ToString() == 源码`。</summary>
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
    }
}
