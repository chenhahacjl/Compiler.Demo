using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 泛型类型实参列表（6e-M20）：`&lt;int, string&gt;`（调用/成员调用/对象创建的显式实参，首版仅显式）。
    /// </summary>
    public sealed partial class TypeArgumentListSyntax : CSharpSyntaxNode
    {
        internal TypeArgumentListSyntax(SyntaxTree syntaxTree, SyntaxToken lessThanToken, ImmutableArray<TypeClauseSyntax> arguments, SyntaxToken greaterThanToken)
            : base(syntaxTree)
        {
            LessThanToken = lessThanToken;
            Arguments = arguments;
            GreaterThanToken = greaterThanToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.TypeArgumentList;

        public SyntaxToken LessThanToken { get; }
        public ImmutableArray<TypeClauseSyntax> Arguments { get; }
        public SyntaxToken GreaterThanToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LessThanToken;
            foreach (var child in Arguments)
            {
                yield return child;
            }
            yield return GreaterThanToken;
        }
    }
}

