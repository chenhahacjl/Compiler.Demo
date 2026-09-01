using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 浜嬩欢澹版槑锛?e-M22 C5+锛夛細`event Click: (Object, string) -&gt; void`锛?co锛? `event Action&lt;...&gt; Click;`锛?cs锛夈€?
    /// 缁戝畾鏈熼檷绾т负闅愯棌鍑芥暟鍊兼暟缁?+ add/remove 鏂规硶瀵癸紱瑙﹀彂 = 绫诲唴瑁稿悕璋冪敤銆?
    /// </summary>
    public sealed partial class EventDeclarationSyntax : MemberSyntax
    {
        internal EventDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken eventKeyword, SyntaxToken identifier, TypeClauseSyntax handlerType)
            : base(syntaxTree, modifiers)
        {
            Modifiers = modifiers;
            EventKeyword = eventKeyword;
            Identifier = identifier;
            HandlerType = handlerType;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.EventDeclaration;

        public ImmutableArray<SyntaxToken> Modifiers { get; }

        public SyntaxToken EventKeyword { get; }

        public SyntaxToken Identifier { get; }

        /// <summary>澶勭悊鍣ㄧ被鍨嬶紙鍑芥暟绫诲瀷 / Func 瀹舵棌 / delegate 鍒悕锛夈€?/summary>
        public TypeClauseSyntax HandlerType { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return EventKeyword;
            yield return Identifier;
            yield return HandlerType;
        }
    }
}

