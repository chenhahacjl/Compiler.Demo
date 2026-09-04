using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 泛型约束子句（6e-M20）：`where T: IComparable&lt;T&gt;` / `where T: Base, IDisposable, new()`。
    /// </summary>
    public sealed partial class WhereClauseSyntax : CocoaSyntaxNode
    {
        internal WhereClauseSyntax(SyntaxTree syntaxTree, SyntaxToken whereKeyword, SyntaxToken identifier, SyntaxToken colonToken, ImmutableArray<TypeClauseSyntax> constraintTypes)
            : base(syntaxTree)
        {
            WhereKeyword = whereKeyword;
            Identifier = identifier;
            ColonToken = colonToken;
            ConstraintTypes = constraintTypes;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.WhereClause;

        public SyntaxToken WhereKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken ColonToken { get; }
        public ImmutableArray<TypeClauseSyntax> ConstraintTypes { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return WhereKeyword;
            yield return Identifier;
            yield return ColonToken;
            foreach (var child in ConstraintTypes)
            {
                yield return child;
            }
        }
    }
}

