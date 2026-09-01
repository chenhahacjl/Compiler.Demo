using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 绫诲瓧娈靛０鏄庤妭鐐癸細`private _x: int`锛堟垨 C# 寮?`private int _x;`锛屽彲甯﹀垵濮嬪寲鍣?`= expr`锛夈€?
    /// </summary>
    public sealed partial class ClassFieldDeclarationSyntax : MemberSyntax
    {
        internal ClassFieldDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken? equalsToken = null, ExpressionSyntax? initializer = null)
            : base(syntaxTree, modifiers)
        {
            Identifier = identifier;
            Type = type;
            EqualsToken = equalsToken;
            Initializer = initializer;
        }

        public override SyntaxKind Kind => SyntaxKind.ClassFieldDeclaration;

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Initializer { get; }

        public bool HasInitializer => Initializer != null;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return Identifier;
            yield return Type;
            if (EqualsToken != null)
            {
                yield return EqualsToken;
            }
            if (Initializer != null)
            {
                yield return Initializer;
            }
        }
    }
}
