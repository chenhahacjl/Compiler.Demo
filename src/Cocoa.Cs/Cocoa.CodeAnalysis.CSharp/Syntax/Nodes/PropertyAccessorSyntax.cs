using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 灞炴€ц闂櫒锛歚get { ... }` / `set { ... }`锛坄;` = 鑷姩锛夛紝鍙甫鍙鎬т慨楗扮锛坄private set;`锛夈€?
    /// </summary>
    public sealed partial class PropertyAccessorSyntax : CSharpSyntaxNode
    {
        internal PropertyAccessorSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken keyword, BlockStatementSyntax? body, SyntaxToken? semicolonToken)
            : base(syntaxTree)
        {
            Modifiers = modifiers;
            Keyword = keyword;
            Body = body;
            SemicolonToken = semicolonToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.PropertyAccessor;

        public ImmutableArray<SyntaxToken> Modifiers { get; }
        public SyntaxToken Keyword { get; }
        public BlockStatementSyntax? Body { get; }
        public SyntaxToken? SemicolonToken { get; }

        public bool IsGet => Keyword.Kind == (SyntaxKind)CSharpSyntaxKind.GetKeyword;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return Keyword;
            if (Body != null)
            {
                yield return Body;
            }
            if (SemicolonToken != null)
            {
                yield return SemicolonToken;
            }
        }
    }
}


