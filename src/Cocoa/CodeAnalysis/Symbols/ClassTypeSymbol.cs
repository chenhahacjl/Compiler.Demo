using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类类型：承载字段/方法/构造函数成员，支持单继承。
    /// </summary>
    public sealed class ClassTypeSymbol : TypeSymbol
    {
        private readonly ImmutableArray<FieldSymbol>.Builder _fields;
        private readonly ImmutableArray<FunctionSymbol>.Builder _methods;
        private readonly ImmutableArray<PropertySymbol>.Builder _properties;
        private readonly List<ClassTypeSymbol> _interfaces = new List<ClassTypeSymbol>();
        private readonly List<ClassTypeSymbol> _baseInterfaces = new List<ClassTypeSymbol>();

        internal ClassTypeSymbol(string name, string @namespace, Visibility visibility, ClassDeclarationSyntax? declaration, bool isExternal = false)
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

        public override SymbolKind Kind => SymbolKind.Class;

        public string Namespace { get; }

        /// <summary>外部引用程序集类型（消费 -r 库时 true）。</summary>
        public bool IsExternal { get; }

        /// <summary>是否为接口（interface 声明；不可实例化、成员无实现）。</summary>
        public bool IsInterface { get; internal set; }

        /// <summary>完整类型名（含命名空间）。</summary>
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public Visibility Visibility { get; }

        public ClassDeclarationSyntax? Declaration { get; }

        /// <summary>基类（null = System.Object）。</summary>
        public ClassTypeSymbol? BaseType { get; set; }

        public bool IsAbstract { get; internal set; }

        public bool IsSealed { get; internal set; }

        public bool IsStatic => IsAbstract && IsSealed;

        internal void AddField(FieldSymbol field) => _fields.Add(field);

        internal void AddMethod(FunctionSymbol method) => _methods.Add(method);

        internal void AddProperty(PropertySymbol property) => _properties.Add(property);

        /// <summary>类直接实现的接口（`class C: I` 的基类型列表中的接口）。</summary>
        internal void AddInterface(ClassTypeSymbol interfaceType) => _interfaces.Add(interfaceType);

        /// <summary>接口直接继承的基接口（`interface IBird: IAnimal, IFlyable`）。</summary>
        internal void AddBaseInterface(ClassTypeSymbol interfaceType) => _baseInterfaces.Add(interfaceType);

        /// <summary>类/接口直接列出的接口（不含继承）。</summary>
        public ImmutableArray<ClassTypeSymbol> Interfaces => _interfaces.ToImmutableArray();

        /// <summary>接口直接继承的基接口（不含递归）。</summary>
        public ImmutableArray<ClassTypeSymbol> BaseInterfaces => _baseInterfaces.ToImmutableArray();

        /// <summary>全部接口（本类直接实现 + 基类链 + 接口继承链，去重）。</summary>
        public ImmutableArray<ClassTypeSymbol> GetAllInterfaces()
        {
            var seen = new HashSet<ClassTypeSymbol>();

            for (var current = this; current != null; current = current.BaseType)
            {
                foreach (var iface in current._interfaces)
                {
                    CollectInterfaceHierarchy(iface, seen);
                }
            }

            return seen.ToImmutableArray();
        }

        private static void CollectInterfaceHierarchy(ClassTypeSymbol iface, HashSet<ClassTypeSymbol> seen)
        {
            if (!seen.Add(iface))
            {
                return;
            }

            foreach (var baseIface in iface._baseInterfaces)
            {
                CollectInterfaceHierarchy(baseIface, seen);
            }
        }

        public ImmutableArray<FieldSymbol> Fields => _fields.ToImmutable();

        public ImmutableArray<FunctionSymbol> Methods => _methods.ToImmutable();

        public ImmutableArray<PropertySymbol> Properties => _properties.ToImmutable();

        /// <summary>本类直接声明的属性（不含基类）。</summary>
        public PropertySymbol? GetDeclaredProperty(string name)
        {
            foreach (var property in _properties)
            {
                if (property.Name == name)
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>属性查找（沿继承链向上，含接口继承链）。</summary>
        public PropertySymbol? GetProperty(string name)
        {
            for (var type = this; type != null; type = type.BaseType)
            {
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

            return null;
        }

        /// <summary>本类直接声明的字段（不含基类）。</summary>
        public FieldSymbol? GetDeclaredField(string name)
        {
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
                var field = type.GetDeclaredField(name);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        /// <summary>方法查找（沿继承链向上，含接口继承链）。</summary>
        public FunctionSymbol? GetMethod(string name)
        {
            for (var type = this; type != null; type = type.BaseType)
            {
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

            return null;
        }

        /// <summary>全部同名方法（含重载，沿继承链向上）：静态容器类方法调用按参数类型解析重载（6e-M18）。</summary>
        public ImmutableArray<FunctionSymbol> GetMethods(string name)
        {
            var builder = ImmutableArray.CreateBuilder<FunctionSymbol>();
            for (var type = this; type != null; type = type.BaseType)
            {
                foreach (var method in type.GetDeclaredMethods(name))
                {
                    builder.Add(method);
                }

                foreach (var iface in type._interfaces)
                {
                    builder.AddRange(iface.GetInterfaceInheritedMethods(name));
                }

                if (type.IsInterface)
                {
                    builder.AddRange(type.GetInterfaceInheritedMethods(name));
                }
            }

            return builder.ToImmutable();
        }

        private ImmutableArray<FunctionSymbol> GetDeclaredMethods(string name)
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
        internal bool HasDeclaredMethodSignature(string name, FunctionSymbol candidate)
        {
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
        public bool IsBaseOf(ClassTypeSymbol type)
        {
            var seen = new System.Collections.Generic.HashSet<ClassTypeSymbol>();
            for (var current = type; current != null && seen.Add(current); current = current.BaseType)
            {
                if (current == this)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
