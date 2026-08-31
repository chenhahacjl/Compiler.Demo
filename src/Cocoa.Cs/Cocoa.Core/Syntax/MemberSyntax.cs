using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public abstract class MemberSyntax : SyntaxNode
    {
        private protected MemberSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers)
            : base(syntaxTree)
        {
            Modifiers = modifiers;
        }

        /// <summary>声明修饰符（public/private/stdcall/cdecl 等，顺序无关）。</summary>
        public ImmutableArray<SyntaxToken> Modifiers { get; }
    }
}
