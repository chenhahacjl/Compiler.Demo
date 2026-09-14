using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public abstract class MemberSyntax : CocoaSyntaxNode
    {
        private protected MemberSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers)
            : base(syntaxTree)
        {
            Modifiers = modifiers;
        }

        /// <summary>澹版槑淇グ绗︼紙public/private/stdcall/cdecl 绛夛紝椤哄簭鏃犲叧锛夈€?/summary>
        public ImmutableArray<SyntaxToken> Modifiers { get; }
    }
}

