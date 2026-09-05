using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 泛型类型参数个体（6e-M22 delegate 真实类型化）：`T`，可带型变注解 `in T` / `out T`。
    /// <see cref="VarianceKeyword"/> 为空 = 不变（Invariant）。类/方法类型参数沿用此节点（类处型变注解报诊断）。
    /// </summary>
    public sealed partial class TypeParameterSyntax : CSharpSyntaxNode
    {
        internal TypeParameterSyntax(SyntaxTree syntaxTree, SyntaxToken? varianceKeyword, SyntaxToken identifier)
            : base(syntaxTree)
        {
            VarianceKeyword = varianceKeyword;
            Identifier = identifier;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.TypeParameter;

        /// <summary>型变注解关键字（<c>in</c> / <c>out</c>；空 = 不变）。</summary>
        public SyntaxToken? VarianceKeyword { get; }

        public SyntaxToken Identifier { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (VarianceKeyword != null)
            {
                yield return VarianceKeyword;
            }

            yield return Identifier;
        }
    }
}