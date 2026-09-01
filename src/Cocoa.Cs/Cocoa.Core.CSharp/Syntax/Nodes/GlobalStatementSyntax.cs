using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class GlobalStatementSyntax : MemberSyntax
    {
        internal GlobalStatementSyntax(SyntaxTree syntaxTree, StatementSyntax statement)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            Statement = statement;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.GlobalStatement;

        public StatementSyntax Statement { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return Statement;
        }
    }
}

