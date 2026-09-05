using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Serialization
{
    /// <summary>
    /// `.coa` 程序集的内存模型：符号表 + 函数体（语义层 BoundProgram 片段）+ 依赖清单。
    /// </summary>
    public sealed class CoaProgram
    {
        public CoaProgram(
            ImmutableArray<FunctionSymbol> functions,
            ImmutableArray<GlobalVariableSymbol> globals,
            ImmutableArray<NamedTypeSymbol> enums,
            ImmutableArray<NamedTypeSymbol> classes,
            ImmutableDictionary<FunctionSymbol, BoundBlockStatement> bodies,
            CoaRequirement requires,
            ImmutableArray<string> platforms,
            ImmutableArray<string> dotnetReferences,
            ImmutableArray<string> nativeImports,
            ImmutableArray<string> codReferences,
            ImmutableArray<string> namespaces,
            ImmutableArray<NamedTypeSymbol> genericDefinitions = default,
            ImmutableDictionary<FunctionSymbol, BoundBlockStatement>? genericOpenBodies = null,
            ImmutableDictionary<string, FunctionSymbol>? functionKeys = null,
            ImmutableDictionary<string, TypeSymbol>? typesByName = null)
        {
            Functions = functions;
            Globals = globals;
            Enums = enums;
            Classes = classes;
            Bodies = bodies;
            Requires = requires;
            Platforms = platforms;
            DotnetReferences = dotnetReferences;
            NativeImports = nativeImports;
            CodReferences = codReferences;
            Namespaces = namespaces;
            GenericDefinitions = genericDefinitions.IsDefault ? ImmutableArray<NamedTypeSymbol>.Empty : genericDefinitions;
            GenericOpenBodies = genericOpenBodies ?? ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty;
            FunctionKeys = functionKeys ?? ImmutableDictionary<string, FunctionSymbol>.Empty;
            TypesByName = typesByName ?? ImmutableDictionary<string, TypeSymbol>.Empty;
        }

        /// <summary>库的顶层函数（含 extern 声明，无入口点）。</summary>
        public ImmutableArray<FunctionSymbol> Functions { get; }

        /// <summary>库的全局变量。</summary>
        public ImmutableArray<GlobalVariableSymbol> Globals { get; }

        /// <summary>库的枚举类型。</summary>
        public ImmutableArray<NamedTypeSymbol> Enums { get; }

        /// <summary>库的纯容器类（6e-M17：仅 syscall/extern 静态方法，无实例成员/构造/字段/属性/继承）。</summary>
        public ImmutableArray<NamedTypeSymbol> Classes { get; }

        /// <summary>泛型定义类（6e-G7 S1：模板壳 + 开放类型参数；消费方实例化展开）。</summary>
        public ImmutableArray<NamedTypeSymbol> GenericDefinitions { get; }

        /// <summary>泛型定义方法的开放绑定体（6e-G7 S2：T 保持开放；消费方替换展开）。</summary>
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> GenericOpenBodies { get; }

        /// <summary>函数体（语义层 BoundProgram 片段）。S-7：raw 结构化 HIR（for/while/if 保留，未 Lower）；
        /// 消费方（链接内联 / 动态 dll 发射）在进入各自消费边界前统一 Lower 为 MIR。</summary>
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> Bodies { get; }

        /// <summary>后端要求。</summary>
        public CoaRequirement Requires { get; }

        /// <summary>空 = 平台无关；否则仅列出的平台可消费。</summary>
        public ImmutableArray<string> Platforms { get; }

        /// <summary>依赖的 .NET 程序集引用（依赖清单传递）。</summary>
        public ImmutableArray<string> DotnetReferences { get; }

        /// <summary>依赖的 native DLL（import 声明）。</summary>
        public ImmutableArray<string> NativeImports { get; }

        /// <summary>依赖的被引用 `.coa`（递归加载）。</summary>
        public ImmutableArray<string> CodReferences { get; }

        /// <summary>库声明的命名空间。</summary>
        public ImmutableArray<string> Namespaces { get; }

        /// <summary>
        /// 函数键（含库维度前缀）→ 函数符号索引。读侧构建，供跨库符号合并（external 库复用实例）与
        /// 消费方发射定位；写侧序列化用。6e 跨库里程碑。
        /// </summary>
        public ImmutableDictionary<string, FunctionSymbol> FunctionKeys { get; }

        /// <summary>
        /// 库内命名类型表（类/枚举/泛型定义 全名 → 符号）。读侧构建，供跨库类型解析
        /// （external 库的 `TypesByName` 并入消费方类型表）。6e 跨库里程碑。
        /// </summary>
        public ImmutableDictionary<string, TypeSymbol> TypesByName { get; }

        /// <summary>
        /// 库的程序集名（文件基名，如 "MyLib"）。Load 时由文件名回填、EmitCocoa 构造时为模块名；
        /// 动态链接（阶段 A2/A3）据此合成 AssemblyRef/TypeRef/MemberRef 指向同名托管 dll。
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>库文件位置（动态链接 CopyLocal：定位同名 dll 随消费方产物部署）。</summary>
        public string SourcePath { get; internal set; } = "";
    }
}
