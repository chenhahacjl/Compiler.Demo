using Cocoa.CodeAnalysis.Syntax;
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

        internal ClassTypeSymbol(string name, string @namespace, bool isPublic, ClassDeclarationSyntax? declaration, bool isExternal = false)
            : base(name)
        {
            Namespace = @namespace ?? "";
            IsPublic = isPublic;
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

        /// <summary>完整类型名（含命名空间）。</summary>
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public bool IsPublic { get; }

        public ClassDeclarationSyntax? Declaration { get; }

        /// <summary>基类（null = System.Object）。</summary>
        public ClassTypeSymbol? BaseType { get; set; }

        public bool IsAbstract { get; internal set; }

        public bool IsSealed { get; internal set; }

        public bool IsStatic => IsAbstract && IsSealed;

        internal void AddField(FieldSymbol field) => _fields.Add(field);

        internal void AddMethod(FunctionSymbol method) => _methods.Add(method);

        internal void AddProperty(PropertySymbol property) => _properties.Add(property);

        public ImmutableArray<FieldSymbol> Fields => _fields.ToImmutable();

        public ImmutableArray<FunctionSymbol> Methods => _methods.ToImmutable();

        public ImmutableArray<PropertySymbol> Properties => _properties.ToImmutable();

        /// <summary>属性查找（沿继承链向上）。</summary>
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

        /// <summary>方法查找（沿继承链向上）。</summary>
        public FunctionSymbol? GetMethod(string name)
        {
            for (var type = this; type != null; type = type.BaseType)
            {
                var method = type.GetDeclaredMethod(name);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>this 是否为 base（含同一类型）或其派生类型。</summary>
        public bool IsBaseOf(ClassTypeSymbol type)
        {
            for (var current = type; current != null; current = current.BaseType)
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
