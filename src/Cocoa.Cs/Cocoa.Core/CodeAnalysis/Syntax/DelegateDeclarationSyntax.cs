using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// delegate 声明（6e-M22）：`.co` `delegate void H(Object,string)` / `.cs` `public delegate void H(object,string);`
    /// Binder 合成为 sealed class extends MulticastDelegate + Invoke 方法（复用全部类机制）。
    /// </summary>
    public sealed partial class DelegateDeclarationSyntax : MemberSyntax
    {
        internal DelegateDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken delegateKeyword, TypeClauseSyntax returnType, SyntaxToken identifier, SeparatedSyntaxList<ParameterSyntax> parameters)
            : base(syntaxTree, modifiers)
        {
            DelegateKeyword = delegateKeyword;
            ReturnType = returnType;
            Identifier = identifier;
            Parameters = parameters;
        }

        public override SyntaxKind Kind => SyntaxKind.DelegateDeclaration;

        public SyntaxToken DelegateKeyword { get; }

        /// <summary>返回类型。</summary>
        public TypeClauseSyntax ReturnType { get; }

        public SyntaxToken Identifier { get; }

        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
    }
}
