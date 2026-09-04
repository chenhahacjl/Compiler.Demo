using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类类型：承载字段/方法/构造函数成员，支持单继承。
    /// 派生：<see cref="InstantiatedTypeSymbol"/>（6e-M20 泛型实例化类，惰性物化成员）。
    /// </summary>
    public class NamedTypeSymbol : TypeSymbol
    {
        /// <summary>System.Object 内建单例（6e-M19 M2-a）：所有无显式基类类的隐式根，成员面随 M2-b/M2-c 接入。</summary>
        public static readonly NamedTypeSymbol SystemObject = new NamedTypeSymbol("Object", "System", Visibility.Public, declaration: null);

        /// <summary>System.Type 内建单例（6e-M19 M2-a）：GetType() 的返回类型。</summary>
        public static readonly NamedTypeSymbol SystemType = new NamedTypeSymbol("Type", "System", Visibility.Public, declaration: null);

        /// <summary>System.Delegate 内建单例（6e-M22 C5+）：所有委托声明的间接基类。</summary>
        public static readonly NamedTypeSymbol SystemDelegate = new NamedTypeSymbol("Delegate", "System", Visibility.Public, declaration: null);

        /// <summary>System.MulticastDelegate 内建单例（6e-M22 C5+）：多播委托基类。</summary>
        public static readonly NamedTypeSymbol SystemMulticastDelegate = new NamedTypeSymbol("MulticastDelegate", "System", Visibility.Public, declaration: null);

        /// <summary>是否为内建 Delegate 根单例。</summary>
        public bool IsSystemDelegate => this == SystemDelegate;

        /// <summary>是否为内建 MulticastDelegate 单例。</summary>
        public bool IsSystemMulticastDelegate => this == SystemMulticastDelegate;

        /// <summary>delegate 声明的 Invoke 方法（TypeKind.Delegate 时存在，否则 null）。</summary>
        public FunctionSymbol? DelegateInvokeMethod => TypeKind == TypeKind.Delegate ? GetMethod("Invoke") : null;

        /// <summary>提取 delegate 类的 Invoke 方法签名对应的函数类型（非 delegate 类返回 null）。</summary>
        public FunctionTypeSymbol? DelegateSignature()
        {
            var invoke = DelegateInvokeMethod;
            if (invoke == null)
                return null;

            var paramTypes = invoke.Parameters.Select(p => p.Type).ToImmutableArray();
            return FunctionTypeSymbol.Get(paramTypes, invoke.ReturnType);
        }

        /// <summary>6e-M22 C5+：内建 Delegate/MulticastDelegate 继承链初始化。</summary>
        static NamedTypeSymbol()
        {
            SystemDelegate.BaseType = SystemObject;
            SystemMulticastDelegate.BaseType = SystemDelegate;
        }

        private readonly ImmutableArray<FieldSymbol>.Builder _fields;
        private readonly ImmutableArray<FunctionSymbol>.Builder _methods;
        private readonly ImmutableArray<PropertySymbol>.Builder _properties;
        private readonly List<EventSymbol> _events = new List<EventSymbol>();
        private readonly List<NamedTypeSymbol> _interfaces = new List<NamedTypeSymbol>();
        private readonly List<NamedTypeSymbol> _baseInterfaces = new List<NamedTypeSymbol>();

        // 枚举常量成员（TypeKind.Enum 时使用；class/struct/interface 为 null）
        private Dictionary<string, int>? _enumMembers;

        public NamedTypeSymbol(string name, string @namespace, Visibility visibility, SyntaxNode? declaration, bool isExternal = false)
            : base(name)
        {
            Namespace = @namespace ?? "";
            Visibility = visibility;
            Declaration = declaration;
            IsExternal = isExternal;
            _fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            _methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
            _properties = ImmutableArray.CreateBuilder<PropertySymbol>();
        }

        public override SymbolKind Kind => SymbolKind.NamedType;

        public string Namespace { get; }

        /// <summary>外部引用程序集类型（消费 -r 库时 true）。</summary>
        public bool IsExternal { get; }

        /// <summary>命名类型类别（6e-M26）：class/struct/interface/enum/delegate 共用同一符号，以 TypeKind 判别。</summary>
        public virtual TypeKind TypeKind { get; set; } = TypeKind.Class;

        /// <summary>是否为接口（interface 声明；不可实例化、成员无实现）。</summary>
        public virtual bool IsInterface => TypeKind == TypeKind.Interface;

        /// <summary>完整类型名（含命名空间）。</summary>
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public Visibility Visibility { get; }

        public SyntaxNode? Declaration { get; }

        /// <summary>基类（null = 接口或未落位；非接口类绑定后恒为显式基类或 <see cref="SystemObject"/>）。</summary>
        public virtual NamedTypeSymbol? BaseType
        {
            get
            {
                EnsureMembersMaterialized();
                return _baseType;
            }
            set
            {
                _baseType = value;
            }
        }

        private NamedTypeSymbol? _baseType;

        /// <summary>是否为内建 Object 根单例（区别于用户声明的类）。</summary>
        public bool IsSystemObjectRoot => this == SystemObject;

        /// <summary>
        /// 是否为 facade 类（6e-M19 M2-b，对齐 C# 基元别名模型）：System.Int32/System.String 等承载
        /// 基元/字符串类型的实例成员面。实例方法声明被编译期降级为静态（隐藏首参 this），不可 new、无字段。
        /// </summary>
        public bool IsFacadeClass { get; set; }

        /// <summary>facade 承载的类型（Int32→i32、String→string；null = 自身，用于 Object/Type facade）。</summary>
        public TypeSymbol? FacadeThisType { get; set; }

        /// <summary>facade 合并（Phase 1-3）：基元符号在类型表中以 facade 全名登记后，成员面经此委托到
        /// `<c>System.Int32</c>`` 等 facade 类（System.Core 缓存实例，进程内共享，赋值幂等）。</summary>
        public NamedTypeSymbol? FacadeCompanion { get; set; }

        public virtual bool IsAbstract { get; set; }

        /// <summary>是否为值类型（struct/enum，6e-M26）：对齐 C#，struct 与枚举都是值类型。</summary>
        public override bool IsValueType => TypeKind is TypeKind.Struct or TypeKind.Enum;

        public virtual bool IsSealed { get; set; }

        /// <summary>
        /// 成员访问前钩子（6e-M20）：泛型实例化类在首次访问时惰性物化成员（定义类可能尚未完成绑定）；
        /// 基类为空操作。
        /// </summary>
        protected virtual void EnsureMembersMaterialized()
        {
        }

        /// <summary>泛型类型参数列表（6e-M20；空 = 非泛型。实例化类的此列表恒为空——实参见 <see cref="InstantiatedTypeSymbol.TypeArguments"/>）。</summary>
        public ImmutableArray<TypeParameterSymbol> TypeParameters { get; set; } = ImmutableArray<TypeParameterSymbol>.Empty;

        /// <summary>是否为泛型定义（模板；不可直接 new，须经类型实参实例化）。</summary>
        public bool IsGenericDefinition => TypeParameters.Length > 0;

        public bool IsStatic => IsAbstract && IsSealed;

        /// <summary>是否为枚举（6e-M26 并入 NamedTypeSymbol）。</summary>
        public bool IsEnum => TypeKind == TypeKind.Enum;

        /// <summary>注入枚举常量成员（仅 TypeKind.Enum 调用）。</summary>
        public void SetEnumMembers(Dictionary<string, int> members) => _enumMembers = members;

        /// <summary>枚举成员名→常量值查找（非枚举返回 false）。</summary>
        public bool TryGetMember(string name, out int value)
        {
            if (_enumMembers != null && _enumMembers.TryGetValue(name, out value))
            {
                return true;
            }

            value = 0;
            return false;
        }

        /// <summary>枚举成员名集合（非枚举为空）。</summary>
        public IReadOnlyCollection<string> MemberNames => (IReadOnlyCollection<string>?)_enumMembers?.Keys ?? Array.Empty<string>();

        public void AddField(FieldSymbol field) => _fields.Add(field);

        public void AddMethod(FunctionSymbol method) => _methods.Add(method);

        public void AddProperty(PropertySymbol property) => _properties.Add(property);

        /// <summary>6e-M22 C5+：事件集合。</summary>
        public void AddEvent(EventSymbol eventSymbol) => _events.Add(eventSymbol);

        /// <summary>类声明的事件（含继承链查找由调用方处理）。</summary>
        public ImmutableArray<EventSymbol> Events => _events.ToImmutableArray();

        public EventSymbol? GetEvent(string name)
        {
            EnsureMembersMaterialized();
            for (var type = (NamedTypeSymbol?)this; type != null; type = type.BaseType)
            {
                type.EnsureMembersMaterialized();
                foreach (var e in type._events)
                {
                    if (e.Name == name) return e;
                }
            }
            return FacadeCompanion?.GetEvent(name);
        }

        /// <summary>类直接实现的接口（`class C: I` 的基类型列表中的接口）。</summary>
        public void AddInterface(NamedTypeSymbol interfaceType) => _interfaces.Add(interfaceType);

        /// <summary>接口直接继承的基接口（`interface IBird: IAnimal, IFlyable`）。</summary>
        public void AddBaseInterface(NamedTypeSymbol interfaceType) => _baseInterfaces.Add(interfaceType);

        /// <summary>类/接口直接列出的接口（不含继承）。</summary>
        public ImmutableArray<NamedTypeSymbol> Interfaces
        {
            get
            {
                EnsureMembersMaterialized();
                return _interfaces.ToImmutableArray();
            }
        }

        /// <summary>接口直接继承的基接口（不含递归）。</summary>
        public ImmutableArray<NamedTypeSymbol> BaseInterfaces
        {
            get
            {
                EnsureMembersMaterialized();
                return _baseInterfaces.ToImmutableArray();
            }
        }

        /// <summary>全部接口（本类直接实现 + 基类链 + 接口继承链，去重）。</summary>
        public ImmutableArray<NamedTypeSymbol> GetAllInterfaces()
        {
            EnsureMembersMaterialized();

            var seen = new HashSet<NamedTypeSymbol>();

            for (var current = this; current != null; current = current.BaseType)
            {
                foreach (var iface in current._interfaces)
                {
                    CollectInterfaceHierarchy(iface, seen);
                }
            }

            return seen.ToImmutableArray();
        }

        private static void CollectInterfaceHierarchy(NamedTypeSymbol iface, HashSet<NamedTypeSymbol> seen)
        {
            if (!seen.Add(iface))
            {
                return;
            }

            iface.EnsureMembersMaterialized();

            foreach (var baseIface in iface._baseInterfaces)
            {
                CollectInterfaceHierarchy(baseIface, seen);
            }
        }

        public ImmutableArray<FieldSymbol> Fields
        {
            get
            {
                EnsureMembersMaterialized();
                return _fields.ToImmutable();
            }
        }

        public ImmutableArray<FunctionSymbol> Methods
        {
            get
            {
                EnsureMembersMaterialized();
                return _methods.ToImmutable();
            }
        }

        public ImmutableArray<PropertySymbol> Properties
        {
            get
            {
                EnsureMembersMaterialized();
                return _properties.ToImmutable();
            }
        }

        /// <summary>本类直接声明的属性（不含基类）。</summary>
        public PropertySymbol? GetDeclaredProperty(string name)
        {
            EnsureMembersMaterialized();
            foreach (var property in _properties)
            {
                if (property.Name == name)
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>索引器查找（本类声明的第一个 IsIndexer 属性；6e-M24）。</summary>
        public PropertySymbol? GetIndexer()
        {
            EnsureMembersMaterialized();
            foreach (var property in _properties)
            {
                if (property.IsIndexer)
                {
                    return property;
                }
            }

            return FacadeCompanion?.GetIndexer();
        }

        /// <summary>属性查找（沿继承链向上，含接口继承链）。</summary>
        public PropertySymbol? GetProperty(string name)
        {
            EnsureMembersMaterialized();
            for (var type = this; type != null; type = type.BaseType)
            {
                type.EnsureMembersMaterialized();
                foreach (var property in type._properties)
                {
                    if (property.Name == name)
                    {
                        return property;
                    }
                }

                foreach (var iface in type._interfaces)
                {
                    var property = iface.GetInterfaceInheritedProperty(name);
                    if (property != null)
                    {
                        return property;
                    }
                }

                if (type.IsInterface)
                {
                    var property = type.GetInterfaceInheritedProperty(name);
                    if (property != null)
                    {
                        return property;
                    }
                }
            }

            return FacadeCompanion?.GetProperty(name);
        }

        /// <summary>本类直接声明的字段（不含基类）。</summary>
        public FieldSymbol? GetDeclaredField(string name)
        {
            EnsureMembersMaterialized();
            foreach (var field in _fields)
            {
                if (field.Name == name)
                {
                    return field;
                }
            }

            return null;
        }

        /// <summary>本类直接声明的方法（不含基类）。</summary>
        public FunctionSymbol? GetDeclaredMethod(string name)
        {
            EnsureMembersMaterialized();
            foreach (var method in _methods)
            {
                if (method.Name == name)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>字段查找（沿继承链向上）。</summary>
        public FieldSymbol? GetField(string name)
        {
            for (var type = this; type != null; type = type.BaseType)
            {
                type.EnsureMembersMaterialized();
                var field = type.GetDeclaredField(name);
                if (field != null)
                {
                    return field;
                }
            }

            return FacadeCompanion?.GetField(name);
        }

        /// <summary>方法查找（沿继承链向上，含接口继承链）。</summary>
        public FunctionSymbol? GetMethod(string name)
        {
            EnsureMembersMaterialized();
            for (var type = this; type != null; type = type.BaseType)
            {
                type.EnsureMembersMaterialized();
                var method = type.GetDeclaredMethod(name);
                if (method != null)
                {
                    return method;
                }

                foreach (var iface in type._interfaces)
                {
                    var interfaceMethod = iface.GetInterfaceInheritedMethod(name);
                    if (interfaceMethod != null)
                    {
                        return interfaceMethod;
                    }
                }

                if (type.IsInterface)
                {
                    var interfaceMethod = type.GetInterfaceInheritedMethod(name);
                    if (interfaceMethod != null)
                    {
                        return interfaceMethod;
                    }
                }
            }

            // facade 合并：基元成员面委托到 facade 类（Int32.Parse 等）
            return FacadeCompanion?.GetMethod(name);
        }

        /// <summary>全部同名方法（含重载，沿继承链向上）：静态容器类方法调用按参数类型解析重载（6e-M18）。</summary>
        public ImmutableArray<FunctionSymbol> GetMethods(string name)
        {
            EnsureMembersMaterialized();
            var builder = ImmutableArray.CreateBuilder<FunctionSymbol>();
            for (var type = this; type != null; type = type.BaseType)
            {
                type.EnsureMembersMaterialized();
                foreach (var method in type.GetDeclaredMethods(name))
                {
                    builder.Add(method);
                }

                foreach (var iface in type._interfaces)
                {
                    // 具体类已实现该接口方法时，只保留类自身的方法（避免与接口抽象方法形成“同名重载”歧义）。
                    foreach (var interfaceMethod in iface.GetInterfaceInheritedMethods(name))
                    {
                        if (!TypeChainDeclaresMatchingMethod(type, interfaceMethod))
                        {
                            builder.Add(interfaceMethod);
                        }
                    }
                }

                if (type.IsInterface)
                {
                    builder.AddRange(type.GetInterfaceInheritedMethods(name));
                }
            }

            if (builder.Count == 0 && FacadeCompanion != null)
            {
                return FacadeCompanion.GetMethods(name);
            }

            return builder.ToImmutable();
        }

        /// <summary>从 type 沿继承链向上查找是否存在与 candidate 同名且参数类型一致的方法（即已实现 candidate 的类方法）。</summary>
        private static bool TypeChainDeclaresMatchingMethod(NamedTypeSymbol? type, FunctionSymbol candidate)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var declared in t.GetDeclaredMethods(candidate.Name))
                {
                    if (declared.Parameters.Length != candidate.Parameters.Length)
                    {
                        continue;
                    }

                    var match = true;
                    for (var i = 0; i < declared.Parameters.Length; i++)
                    {
                        if (!declared.Parameters[i].Type.Equals(candidate.Parameters[i].Type))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public ImmutableArray<FunctionSymbol> GetDeclaredMethods(string name)
        {
            var builder = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (var method in _methods)
            {
                if (method.Name == name)
                {
                    builder.Add(method);
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>是否已声明同名同签名方法（重载按参数类型逐一比较，6e-M18 容器类重载支持）。</summary>
        public bool HasDeclaredMethodSignature(string name, FunctionSymbol candidate)
        {
            EnsureMembersMaterialized();
            foreach (var method in GetDeclaredMethods(name))
            {
                if (method.Parameters.Length != candidate.Parameters.Length)
                {
                    continue;
                }

                var same = true;
                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    if (method.Parameters[i].Type != candidate.Parameters[i].Type)
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return true;
                }
            }

            return false;
        }

        private ImmutableArray<FunctionSymbol> GetInterfaceInheritedMethods(string name)
        {
            var declared = GetDeclaredMethods(name);
            if (!declared.IsEmpty)
            {
                return declared;
            }

            var builder = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (var baseIface in _baseInterfaces)
            {
                builder.AddRange(baseIface.GetInterfaceInheritedMethods(name));
            }

            return builder.ToImmutable();
        }

        /// <summary>接口成员查找：本接口声明 + 基接口链（接口继承）。</summary>
        private FunctionSymbol? GetInterfaceInheritedMethod(string name)
        {
            var declared = GetDeclaredMethod(name);
            if (declared != null)
            {
                return declared;
            }

            foreach (var baseIface in _baseInterfaces)
            {
                var interfaceMethod = baseIface.GetInterfaceInheritedMethod(name);
                if (interfaceMethod != null)
                {
                    return interfaceMethod;
                }
            }

            return null;
        }

        /// <summary>接口属性查找：本接口声明 + 基接口链（接口继承）。</summary>
        private PropertySymbol? GetInterfaceInheritedProperty(string name)
        {
            foreach (var property in _properties)
            {
                if (property.Name == name)
                {
                    return property;
                }
            }

            foreach (var baseIface in _baseInterfaces)
            {
                var interfaceProperty = baseIface.GetInterfaceInheritedProperty(name);
                if (interfaceProperty != null)
                {
                    return interfaceProperty;
                }
            }

            return null;
        }

        /// <summary>this 是否为 base（含同一类型）或其派生类型（防循环继承死循环）。</summary>
        public bool IsBaseOf(NamedTypeSymbol type)
        {
            EnsureMembersMaterialized();
            var seen = new System.Collections.Generic.HashSet<NamedTypeSymbol>();
            for (var current = type; current != null && seen.Add(current); current = current.BaseType)
            {
                current.EnsureMembersMaterialized();
                if (current == this)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
