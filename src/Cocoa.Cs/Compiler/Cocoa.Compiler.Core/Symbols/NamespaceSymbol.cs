using System;
using System.Collections.Immutable;
using System.Text;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 命名空间符号（Phase 1-5 起点，对齐 Roslyn <see cref="Microsoft.CodeAnalysis.INamespaceSymbol"/>）。
    /// 以不可变树描述类型/子命名空间的包含关系；实例为编译期对象（每个 Compilation 各建一棵）。
    /// 现有符号的 <c>Namespace</c> 字符串模型保持不变（本类为新增视图，非替换）。
    /// </summary>
    public sealed class NamespaceSymbol : Symbol
    {
        private ImmutableArray<NamespaceSymbol> _namespaces = ImmutableArray<NamespaceSymbol>.Empty;
        private ImmutableArray<TypeSymbol> _typeMembers = ImmutableArray<TypeSymbol>.Empty;
        private ImmutableArray<FunctionSymbol> _functionMembers = ImmutableArray<FunctionSymbol>.Empty;

        private NamespaceSymbol(string name, NamespaceSymbol? containingNamespace)
            : base(name)
        {
            ContainingNamespace = containingNamespace;
        }

        /// <summary>全局命名空间根工厂（每 Compilation 一棵，避免跨编译状态污染）。</summary>
        internal static NamespaceSymbol CreateGlobal() => new NamespaceSymbol("_global_", containingNamespace: null);

        public override SymbolKind Kind => SymbolKind.Namespace;

        /// <summary>父命名空间（全局根为 null）。</summary>
        public NamespaceSymbol? ContainingNamespace { get; }

        /// <summary>是否全局命名空间根。</summary>
        public bool IsGlobal => ContainingNamespace == null;

        /// <summary>点分全名（全局根为 ""，区别于 <see cref="Symbol.Name"/> 的根显示名）。</summary>
        public string FullName
        {
            get
            {
                if (IsGlobal)
                {
                    return "";
                }

                var builder = new StringBuilder(Name);
                for (var ns = ContainingNamespace; ns != null && !ns.IsGlobal; ns = ns.ContainingNamespace)
                {
                    builder.Insert(0, ns.Name + ".");
                }

                return builder.ToString();
            }
        }

        public ImmutableArray<NamespaceSymbol> GetNamespaceMembers() => _namespaces;

        public ImmutableArray<TypeSymbol> GetTypeMembers() => _typeMembers;

        /// <summary>按点分全名在本命名空间树中定位命名类型（不含泛型元数/实例化 mangle）；未命中返回 null。
        /// 统一「命名空间.简单名」解析（Binder 全名定位 / Compilation.GetTypeByMetadataName 共用）。</summary>
        public TypeSymbol? TryGetType(string fullName)
        {
            var dotIndex = fullName.LastIndexOf('.');
            NamespaceSymbol? ns;
            string simpleName;
            if (dotIndex < 0)
            {
                ns = this;
                simpleName = fullName;
            }
            else
            {
                ns = GetNamespace(fullName.Substring(0, dotIndex));
                if (ns == null)
                {
                    return null;
                }

                simpleName = fullName.Substring(dotIndex + 1);
            }

            foreach (var member in ns.GetTypeMembers())
            {
                if (member.Name == simpleName)
                {
                    return member;
                }
            }

            return null;
        }

        public ImmutableArray<FunctionSymbol> GetFunctionMembers() => _functionMembers;

        /// <summary>按点分全名查找命名空间成员（相对本节点；空名返回本节点，未命中返回 null）。</summary>
        public NamespaceSymbol? GetNamespace(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return this;
            }

            var parts = fullName.Split('.');
            NamespaceSymbol? current = this;
            foreach (var part in parts)
            {
                NamespaceSymbol? next = null;
                foreach (var child in current?._namespaces ?? default)
                {
                    if (child.Name == part)
                    {
                        next = child;
                        break;
                    }
                }

                if (next == null)
                {
                    return null;
                }

                current = next;
            }

            return current;
        }

        /// <summary>安全创建/获取指定全名下的命名空间（在编译内构建树；空名返回全局根）。</summary>
        public static NamespaceSymbol GetOrCreateNamespace(NamespaceSymbol root, string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return root;
            }

            var parts = fullName.Split('.');
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                current = GetOrCreateChildNamespace(current, parts[i]);
            }

            return current;
        }

        private static NamespaceSymbol GetOrCreateChildNamespace(NamespaceSymbol parent, string name)
        {
            var children = parent._namespaces;
            foreach (var child in children)
            {
                if (child.Name == name)
                {
                    return child;
                }
            }

            var created = new NamespaceSymbol(name, parent);
            parent._namespaces = children.Add(created);
            return created;
        }

        internal void AddTypeMember(TypeSymbol type)
        {
            _typeMembers = _typeMembers.Add(type);
        }

        public void AddFunctionMember(FunctionSymbol function)
        {
            _functionMembers = _functionMembers.Add(function);
        }
    }
}