using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类类型：承载字段/方法/构造函数成员。
    /// </summary>
    public sealed class ClassTypeSymbol : TypeSymbol
    {
        private readonly ImmutableArray<FieldSymbol>.Builder _fields;
        private readonly ImmutableArray<FunctionSymbol>.Builder _methods;

        internal ClassTypeSymbol(string name, string @namespace, bool isPublic, ClassDeclarationSyntax? declaration, bool isExternal = false)
            : base(name)
        {
            Namespace = @namespace ?? "";
            IsPublic = isPublic;
            Declaration = declaration;
            IsExternal = isExternal;
            _fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            _methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
        }

        public override SymbolKind Kind => SymbolKind.Class;

        public string Namespace { get; }

        /// <summary>外部引用程序集类型（消费 -r 库时 true）。</summary>
        public bool IsExternal { get; }

        /// <summary>完整类型名（含命名空间）。</summary>
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public bool IsPublic { get; }

        public ClassDeclarationSyntax? Declaration { get; }

        internal void AddField(FieldSymbol field) => _fields.Add(field);

        internal void AddMethod(FunctionSymbol method) => _methods.Add(method);

        public ImmutableArray<FieldSymbol> Fields => _fields.ToImmutable();

        public ImmutableArray<FunctionSymbol> Methods => _methods.ToImmutable();

        public FieldSymbol? GetField(string name)
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

        public FunctionSymbol? GetMethod(string name)
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
    }
}
