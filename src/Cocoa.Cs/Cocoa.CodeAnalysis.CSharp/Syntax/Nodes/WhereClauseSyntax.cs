using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 娉涘瀷绾︽潫瀛愬彞锛?e-M20锛夛細`where T: IComparable&lt;T&gt;` / `where T: Base, IDisposable, new()`銆?
    /// </summary>
    public sealed partial class WhereClauseSyntax : CSharpSyntaxNode
    {
        internal WhereClauseSyntax(SyntaxTree syntaxTree, SyntaxToken whereKeyword, SyntaxToken identifier, SyntaxToken colonToken, ImmutableArray<TypeClauseSyntax> constraintTypes)
            : base(syntaxTree)
        {
            WhereKeyword = whereKeyword;
            Identifier = identifier;
            ColonToken = colonToken;
            ConstraintTypes = constraintTypes;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.WhereClause;

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

