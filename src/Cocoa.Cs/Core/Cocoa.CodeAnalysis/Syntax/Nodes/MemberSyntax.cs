using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax.Green;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    public abstract class MemberSyntax : SyntaxNode
    {
        private protected MemberSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers)
            : base(syntaxTree)
        {
            Modifiers = modifiers;
        }

        /// <summary>声明修饰符（public/private/stdcall/cdecl 等，顺序无关）。/summary>
        public ImmutableArray<SyntaxToken> Modifiers { get; }
    }
}

