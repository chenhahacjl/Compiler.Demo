using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class FunctionSymbol : Symbol
    {
        internal FunctionSymbol(string name, ImmutableArray<ParameterSymbol> parameters, TypeSymbol returnType, FunctionDeclarationSyntax? declaration = null, bool isExtern = false, string? dllName = null, CallingConvention callingConvention = CallingConvention.Winapi, ClassTypeSymbol? containingClass = null, SyntaxNode? syntax = null, Visibility visibility = Visibility.Public, BuiltinKind? builtinKind = null)
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
            Visibility = visibility;
            BuiltinKind = builtinKind;
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
        public Visibility Visibility { get; }

        /// <summary>内置函数种类（功能层原语，三后端按此分发）；非内置为 null。</summary>
        public BuiltinKind? BuiltinKind { get; }

        public bool IsVirtual { get; internal set; }

        public bool IsOverride { get; internal set; }

        public bool IsAbstract { get; internal set; }

        public bool IsSealed { get; internal set; }

        public bool IsStatic { get; internal set; }

        /// <summary>构造函数（显式或隐式默认构造）。</summary>
        public bool IsConstructor { get; internal set; }

        /// <summary>override 方法在基类中的对应虚方法（沿继承链）。</summary>
        public FunctionSymbol? OverriddenMethod { get; internal set; }
    }
}