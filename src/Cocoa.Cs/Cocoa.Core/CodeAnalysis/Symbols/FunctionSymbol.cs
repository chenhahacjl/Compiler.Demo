using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class FunctionSymbol : Symbol
    {
        internal FunctionSymbol(string name, ImmutableArray<ParameterSymbol> parameters, TypeSymbol returnType, FunctionDeclarationSyntax? declaration = null, bool isExtern = false, string? dllName = null, CallingConvention callingConvention = CallingConvention.Winapi, NamedTypeSymbol? containingClass = null, SyntaxNode? syntax = null, Visibility visibility = Visibility.Public, BuiltinKind? builtinKind = null, string @namespace = "", string? entryPoint = null, CharSet? charSet = null)
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
            Namespace = @namespace ?? "";
            EntryPoint = entryPoint;
            CharSet = charSet;
        }

        public override SymbolKind Kind => SymbolKind.Function;

        public ImmutableArray<ParameterSymbol> Parameters { get; }
        public TypeSymbol ReturnType { get; }
        public FunctionDeclarationSyntax? Declaration { get; }
        public bool IsExtern { get; }
        public string? DllName { get; }
        public CallingConvention CallingConvention { get; }

        /// <summary>DLL 导出名（≠ Cocoa 名时的别名映射，`extern(entry=…)`）；null = 用函数名。6e-M17 Step 5。</summary>
        public string? EntryPoint { get; }

        /// <summary>extern 编码格式（`extern(charset=…)` 函数级 / import 块级配置）；null = unicode。6e-M17 Step 5。</summary>
        public CharSet? CharSet { get; }

        /// <summary>所属类（null = 顶层函数）。</summary>
        public NamedTypeSymbol? ContainingClass { get; }

        /// <summary>声明语法（类方法/构造函数也指向其语法节点）。</summary>
        public SyntaxNode? Syntax { get; }

        /// <summary>所属属性（索引器 get_Item/set_Item 反查；用于赋值表达式识别索引器）。6e-M24。</summary>
        public PropertySymbol? ContainingProperty { get; internal set; }

        /// <summary>可见性（仅类方法/构造有意义）。</summary>
        public Visibility Visibility { get; }

        /// <summary>内置函数种类（功能层原语，三后端按此分发）；非内置为 null。</summary>
        public BuiltinKind? BuiltinKind { get; }

        /// <summary>所属命名空间（顶层函数；类方法由 <see cref="ContainingClass"/> 承载）。</summary>
        public string Namespace { get; }

        /// <summary>发射名：命名空间限定（`ns.name`），IL 元数据方法名用此保证唯一。</summary>
        public string EmitName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public bool IsVirtual { get; internal set; }

        public bool IsOverride { get; internal set; }

        public bool IsAbstract { get; internal set; }

        public bool IsSealed { get; internal set; }

        public bool IsStatic { get; internal set; }

        /// <summary>构造函数（显式或隐式默认构造）。</summary>
        public bool IsConstructor { get; internal set; }

        /// <summary>override 方法在基类中的对应虚方法（沿继承链）。</summary>
        public FunctionSymbol? OverriddenMethod { get; internal set; }

        /// <summary>泛型方法类型参数（6e-M20；空 = 非泛型方法。实例化后的具体方法此列表为空）。</summary>
        public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = ImmutableArray<TypeParameterSymbol>.Empty;

        /// <summary>是否为泛型方法定义（模板）。</summary>
        public bool IsGenericMethod => TypeParameters.Length > 0;

        /// <summary>
        /// 本函数体内被 lambda 捕获的局部变量/参数（6e-M22 C5）——
        /// 非空即表示该函数需要环境对象承载这些变量的规范存储。
        /// </summary>
        internal System.Collections.Generic.List<VariableSymbol>? CapturedVariables { get; set; }

        /// <summary>提升 lambda 专有：其环境对象所属的宿主函数（嵌套 lambda 继承最外层非 lambda 函数）。</summary>
        internal FunctionSymbol? EnvironmentOwner { get; set; }

        /// <summary>提升 lambda 专有：调用时须把 Receiver（环境对象）压入环境栈。</summary>
        public bool IsLambdaWithEnvironment { get; internal set; }

        /// <summary>合成环境类（6e-M22 C5）：宿主函数与其体内捕获 lambda 共享同一类（发射布局用）。</summary>
        internal NamedTypeSymbol? EnvironmentClass { get; set; }
    }
}