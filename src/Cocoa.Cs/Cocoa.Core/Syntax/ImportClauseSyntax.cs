using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// import 澹版槑鑺傜偣
    /// </summary>
    public sealed partial class ImportClauseSyntax : MemberSyntax
    {
        public ImportClauseSyntax(SyntaxTree syntaxTree, SyntaxToken importKeyword, ImmutableArray<SyntaxToken> nameTokens)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            ImportKeyword = importKeyword;
            NameTokens = nameTokens;
        }

        public override SyntaxKind Kind => SyntaxKind.ImportClause;

        public SyntaxToken ImportKeyword { get; }

        public ImmutableArray<SyntaxToken> NameTokens { get; }

        public string DllName
        {
            get
            {
                return string.Concat(NameTokens.Select(t => t.Text));
            }
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return ImportKeyword;
            foreach (var child in NameTokens)
            {
                yield return child;
            }
        }
    }
}
