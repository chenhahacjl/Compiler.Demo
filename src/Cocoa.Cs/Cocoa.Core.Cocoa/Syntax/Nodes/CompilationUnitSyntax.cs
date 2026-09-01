using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 缂栬瘧鍗曞厓璇硶
    /// </summary>
    public sealed partial class CompilationUnitSyntax : CocoaSyntaxNode
    {
        internal CompilationUnitSyntax(SyntaxTree syntaxTree, ImmutableArray<MemberSyntax> members, SyntaxToken endOfFileToken)
            : base(syntaxTree)
        {
            Members = members;
            EndOfFileToken = endOfFileToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.CompilationUnit;

        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken EndOfFileToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Members)
            {
                yield return child;
            }
            yield return EndOfFileToken;
        }
    }
}

