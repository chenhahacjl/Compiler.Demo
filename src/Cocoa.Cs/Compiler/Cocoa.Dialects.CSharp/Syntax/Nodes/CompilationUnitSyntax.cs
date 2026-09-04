using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 编译单元语法
    /// </summary>
    public sealed partial class CompilationUnitSyntax : CSharpSyntaxNode
    {
        internal CompilationUnitSyntax(SyntaxTree syntaxTree, ImmutableArray<MemberSyntax> members, SyntaxToken endOfFileToken)
            : base(syntaxTree)
        {
            Members = members;
            EndOfFileToken = endOfFileToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.CompilationUnit;

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

