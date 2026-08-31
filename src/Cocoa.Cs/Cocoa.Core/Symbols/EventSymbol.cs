using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 事件符号（6e-M22 C5+）：`event Click: HandlerType` —— 声明类成员级多播回调点。
    /// HandlerType 为函数类型（结构化或经 delegate 别名解析后的 FunctionTypeSymbol）。
    /// 运行期由发射器合成隐藏函数值数组承载订阅者列表。
    /// </summary>
    public sealed class EventSymbol : Symbol
    {
        internal EventSymbol(string name, FunctionTypeSymbol handlerType, Visibility visibility, NamedTypeSymbol containingClass)
            : base(name)
        {
            HandlerType = handlerType;
            Visibility = visibility;
            ContainingClass = containingClass;
        }

        public override SymbolKind Kind => SymbolKind.Event;

        /// <summary>处理器函数类型。</summary>
        public FunctionTypeSymbol HandlerType { get; }

        /// <summary>可见性。</summary>
        public Visibility Visibility { get; }

        /// <summary>所属类。</summary>
        public NamedTypeSymbol ContainingClass { get; }

        /// <summary>是否为静态事件。</summary>
        public bool IsStatic { get; internal set; }
    }
}
