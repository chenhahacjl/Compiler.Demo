using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 泛型实例化类符号（6e-M20 编译期单态化）：`List&lt;int&gt;` = 泛型定义 List&lt;T&gt; + 实参 [int]。
    /// 成员（字段/方法/属性/基类/接口）由 <see cref="GenericTypeInstantiator"/> 经类型替换填充，
    /// 对三后端而言是独立普通类（各自 TypeDef/vtable/typeId）。
    /// <br/>
    /// <b>惰性物化</b>：实例化可能发生在定义类成员绑定完成之前（前向引用），成员访问首触发
    /// <see cref="EnsureMembersMaterialized"/>（经基类钩子）——届时定义已完整，替换快照即正确。
    /// </summary>
    public sealed class InstantiatedTypeSymbol : NamedTypeSymbol
    {
        private readonly object _gate = new();
        private bool _materialized;

        public InstantiatedTypeSymbol(string name, string @namespace, Visibility visibility, NamedTypeSymbol genericDefinition, ImmutableArray<TypeSymbol> typeArguments)
            : base(name, @namespace, visibility, genericDefinition.Declaration)
        {
            GenericDefinition = genericDefinition;
            TypeArguments = typeArguments;
        }

        /// <summary>泛型定义（模板）。</summary>
        public NamedTypeSymbol GenericDefinition { get; }

        /// <summary>类型实参（与定义的 TypeParameters 一一对应；可为具体类型或外层类型参数——嵌套泛型上下文）。</summary>
        public ImmutableArray<TypeSymbol> TypeArguments { get; }

        public override bool IsInterface
        {
            get
            {
                EnsureMembersMaterialized();
                return base.IsInterface;
            }
        }

        public override bool IsAbstract
        {
            get
            {
                EnsureMembersMaterialized();
                return base.IsAbstract;
            }
        }

        public override bool IsSealed
        {
            get
            {
                EnsureMembersMaterialized();
                return base.IsSealed;
            }
        }

        public bool IsMaterialized
        {
            get
            {
                lock (_gate)
                {
                    return _materialized;
                }
            }
        }

        /// <summary>首次成员访问时从泛型定义快照替换填充（幂等；缓存槽在创建时已预留，自引用安全）。</summary>
        protected sealed override void EnsureMembersMaterialized()
        {
            if (_materialized)
            {
                return;
            }

            lock (_gate)
            {
                if (_materialized)
                {
                    return;
                }

                GenericTypeInstantiator.Populate(this);
                _materialized = true;
            }
        }
    }
}
