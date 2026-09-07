using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class ParameterSyntax : CocoaSyntaxNode
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

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.Parameter;

        public SyntaxToken? Modifier { get; }
        public bool IsByRef => Modifier != null;

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }

        /// <summary>可选参数默认值（语言后置件）：`x: i32 = 10` 的 `= 10` 部分。</summary>
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? DefaultValue { get; }
        public bool HasDefaultValue => DefaultValue != null;

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

            if (EqualsToken != null)
            {
                slots.Add(EqualsToken.ToGreen());
                if (DefaultValue != null)
                {
                    slots.Add(DefaultValue.ToGreen());
                }
            }

            return new GreenNodeWithChildren((SyntaxKind)Kind, slots.ToImmutable());
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

