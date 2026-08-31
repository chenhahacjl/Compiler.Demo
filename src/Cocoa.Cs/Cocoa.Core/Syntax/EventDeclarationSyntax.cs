using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 事件声明（6e-M22 C5+）：`event Click: (Object, string) -&gt; void`（.co）/ `event Action&lt;...&gt; Click;`（.cs）。
    /// 绑定期降级为隐藏函数值数组 + add/remove 方法对；触发 = 类内裸名调用。
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

        public override SyntaxKind Kind => SyntaxKind.EventDeclaration;

        public ImmutableArray<SyntaxToken> Modifiers { get; }

        public SyntaxToken EventKeyword { get; }

        public SyntaxToken Identifier { get; }

        /// <summary>处理器类型（函数类型 / Func 家族 / delegate 别名）。</summary>
        public TypeClauseSyntax HandlerType { get; }
    }
}
