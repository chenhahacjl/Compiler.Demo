using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 泛型约束子句（6e-M20）：`where T: IComparable&lt;T&gt;` / `where T: Base, IDisposable, new()`。
    /// </summary>
    public sealed partial class WhereClauseSyntax : SyntaxNode
    {
        internal WhereClauseSyntax(SyntaxTree syntaxTree, SyntaxToken whereKeyword, SyntaxToken identifier, SyntaxToken colonToken, ImmutableArray<TypeClauseSyntax> constraintTypes)
            : base(syntaxTree)
        {
            WhereKeyword = whereKeyword;
            Identifier = identifier;
            ColonToken = colonToken;
            ConstraintTypes = constraintTypes;
        }

        public override SyntaxKind Kind => SyntaxKind.WhereClause;

        public SyntaxToken WhereKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken ColonToken { get; }
        public ImmutableArray<TypeClauseSyntax> ConstraintTypes { get; }
    }
}
