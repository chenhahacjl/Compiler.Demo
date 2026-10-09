using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 事件声明（6e-M22 C5+）：`event Click: (Object, string) -&gt; void`（.co）； `event Action&lt;...&gt; Click;`（.cs）。
    /// 绑定期降级为隐藏函数值数组 + add/remove 方法对；触发 = 类内裸名调用。
    /// 支持访问器式自定义实现：`event E: T { add { … } remove { … } }`（AddBody/RemoveBody 非空）。
    /// </summary>
    public sealed partial class EventDeclarationSyntax : MemberSyntax
    {
        internal EventDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken eventKeyword, SyntaxToken identifier, TypeClauseSyntax handlerType, BlockStatementSyntax? addBody = null, BlockStatementSyntax? removeBody = null)
            : base(syntaxTree, modifiers)
        {
            Modifiers = modifiers;
            EventKeyword = eventKeyword;
            Identifier = identifier;
            HandlerType = handlerType;
            AddBody = addBody;
            RemoveBody = removeBody;
        }

        public override SyntaxKind Kind => SyntaxKind.EventDeclaration;

        public ImmutableArray<SyntaxToken> Modifiers { get; }

        public SyntaxToken EventKeyword { get; }

        public SyntaxToken Identifier { get; }

        /// <summary>处理器类型（函数类型 / Func 装载 / delegate 别名）。</summary>
        public TypeClauseSyntax HandlerType { get; }

        /// <summary>自定义 add 访问器体（`event E: T { add { … } }`；null = 字段式自动 add/remove）。</summary>
        public BlockStatementSyntax? AddBody { get; }

        /// <summary>自定义 remove 访问器体（`event E: T { … remove { … } }`；null = 字段式自动 add/remove）。</summary>
        public BlockStatementSyntax? RemoveBody { get; }

        /// <summary>是否访问器式事件（含 add/remove 自定义体）。</summary>
        public bool HasCustomAccessors => AddBody != null || RemoveBody != null;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return EventKeyword;
            yield return Identifier;
            yield return HandlerType;
            if (AddBody != null)
            {
                yield return AddBody;
            }
            if (RemoveBody != null)
            {
                yield return RemoveBody;
            }
        }
    }
}

