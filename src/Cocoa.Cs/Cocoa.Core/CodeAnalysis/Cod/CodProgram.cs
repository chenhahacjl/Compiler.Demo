using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Cod
{
    /// <summary>
    /// `.cod` 程序集的内存模型：符号表 + 函数体（语义层 BoundProgram 片段）+ 依赖清单。
    /// </summary>
    internal sealed class CodProgram
    {
        public CodProgram(
            ImmutableArray<FunctionSymbol> functions,
            ImmutableArray<GlobalVariableSymbol> globals,
            ImmutableArray<NamedTypeSymbol> enums,
            ImmutableArray<NamedTypeSymbol> classes,
            ImmutableDictionary<FunctionSymbol, BoundBlockStatement> bodies,
            CodRequirement requires,
            ImmutableArray<string> platforms,
            ImmutableArray<string> dotnetReferences,
            ImmutableArray<string> nativeImports,
            ImmutableArray<string> codReferences,
            ImmutableArray<string> namespaces,
            ImmutableArray<NamedTypeSymbol> genericDefinitions = default,
            ImmutableDictionary<FunctionSymbol, BoundBlockStatement>? genericOpenBodies = null)
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

        /// <summary>函数体（语义层 BoundProgram 片段，已降级）。</summary>
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> Bodies { get; }

        /// <summary>后端要求。</summary>
        public CodRequirement Requires { get; }

        /// <summary>空 = 平台无关；否则仅列出的平台可消费。</summary>
        public ImmutableArray<string> Platforms { get; }

        /// <summary>依赖的 .NET 程序集引用（依赖清单传递）。</summary>
        public ImmutableArray<string> DotnetReferences { get; }

        /// <summary>依赖的 native DLL（import 声明）。</summary>
        public ImmutableArray<string> NativeImports { get; }

        /// <summary>依赖的被引用 `.cod`（递归加载）。</summary>
        public ImmutableArray<string> CodReferences { get; }

        /// <summary>库声明的命名空间。</summary>
        public ImmutableArray<string> Namespaces { get; }

        /// <summary>
        /// 库的程序集名（文件基名，如 "MyLib"）。Load 时由文件名回填、EmitCocoa 构造时为模块名；
        /// 动态链接（阶段 A2/A3）据此合成 AssemblyRef/TypeRef/MemberRef 指向同名托管 dll。
        /// </summary>
        public string Name { get; internal set; } = "";

        /// <summary>库文件位置（动态链接 CopyLocal：定位同名 dll 随消费方产物部署）。</summary>
        public string SourcePath { get; internal set; } = "";
    }
}
