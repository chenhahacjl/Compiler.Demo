using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class FunctionSymbol : Symbol
    {
        internal FunctionSymbol(string name, ImmutableArray<ParameterSymbol> parameters, TypeSymbol returnType, FunctionDeclarationSyntax? declaration = null, bool isExtern = false, string? dllName = null, CallingConvention callingConvention = CallingConvention.Winapi, ClassTypeSymbol? containingClass = null, SyntaxNode? syntax = null, bool isPublic = true)
            : base(name)
        {
            Parameters = parameters;
            ReturnType = returnType;
            Declaration = declaration;
            IsExtern = isExtern;
            DllName = dllName;
            CallingConvention = callingConvention;
            ContainingClass = containingClass;
            Syntax = syntax;
            IsPublic = isPublic;
        }

        public override SymbolKind Kind => SymbolKind.Function;

        public ImmutableArray<ParameterSymbol> Parameters { get; }
        public TypeSymbol ReturnType { get; }
        public FunctionDeclarationSyntax? Declaration { get; }
        public bool IsExtern { get; }
        public string? DllName { get; }
        public CallingConvention CallingConvention { get; }

        /// <summary>所属类（null = 顶层函数）。</summary>
        public ClassTypeSymbol? ContainingClass { get; }

        /// <summary>声明语法（类方法/构造函数也指向其语法节点）。</summary>
        public SyntaxNode? Syntax { get; }

        /// <summary>可见性（仅类方法/构造有意义）。</summary>
        public bool IsPublic { get; }
    }
}