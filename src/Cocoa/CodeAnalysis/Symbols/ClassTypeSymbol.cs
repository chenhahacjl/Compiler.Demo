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

        internal ClassTypeSymbol(string name, bool isPublic, ClassDeclarationSyntax? declaration)
            : base(name)
        {
            IsPublic = isPublic;
            Declaration = declaration;
            _fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            _methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
        }

        public override SymbolKind Kind => SymbolKind.Class;

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
