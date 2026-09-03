using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语言（M2 设计 X）：对标 Roslyn 语言前端抽象。每门语言一个实例，承载
    /// 名字 / 内建类型名词汇 / 解析器工厂 / 参数拼写策略；
    /// 语言专属实现以 <see cref="Language"/> 子类落入各自程序集。
    /// C# 方言全套位于独立程序集 Cocoa.CodeAnalysis.CSharp（<see cref="Cocoa.CodeAnalysis.CSharpLanguage"/>）；
    /// CO 宿主语言 CocoaLanguage（Y-A3-4）迁入独立程序集 Cocoa.CodeAnalysis.Cocoa，
    /// 核心经 <see cref="Cocoa"/> 反射装载并触达之（默认解析路径依赖 Cocoa.CodeAnalysis.Cocoa 在应用目录）。
    /// 新语言 = 新增 Language 子类（含解析器），核心零改动（设计 X §6.3）。
    /// </summary>
    public abstract class Language
    {
        private static readonly Dictionary<string, Language> _registered = new();
        private static Language? _cocoa;
        private static Language? _csharp;

        protected Language(string name)
        {
            Name = name;
            _registered[name] = this;
        }

        public string Name { get; }

        /// <summary>Cocoa 宿主语言（默认，`.co`）：实例位于 Cocoa.CodeAnalysis.Cocoa，此处经注册表 / 反射装载解析。</summary>
        public static Language Cocoa => _cocoa ??= CreateCocoa();

        /// <summary>C# 方言（`.cs`）：实例位于 Cocoa.CodeAnalysis.CSharp，此处经注册表 / 反射装载解析。</summary>
        public static Language CSharp => _csharp ??= CreateCSharp();

        private static Language CreateCocoa()
        {
            if (_registered.TryGetValue("cocoa", out var language))
            {
                return language;
            }

            // Y-A3-4：CocoaLanguage 随 CO L1 迁入 Cocoa.CodeAnalysis.Cocoa；反射装载并触达 Instance（静态初始化经 base("cocoa") 注册）。
            var assembly = System.Reflection.Assembly.Load("Cocoa.CodeAnalysis.Cocoa");
            var instance = assembly.GetType("Cocoa.CodeAnalysis.CocoaLanguage")!
                .GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .GetValue(null);
            return (Language)instance!;
        }

        private static Language CreateCSharp()
        {
            if (_registered.TryGetValue("csharp", out var language))
            {
                return language;
            }

            var assembly = System.Reflection.Assembly.Load("Cocoa.CodeAnalysis.CSharp");
            var instance = assembly.GetType("Cocoa.CodeAnalysis.CSharpLanguage")!
                .GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .GetValue(null);
            return (Language)instance!;
        }

        public static bool TryGet(string name, out Language language)
        {
            return _registered.TryGetValue(name, out language!);
        }

        /// <summary>按名取已注册语言；未注册（对应语言程序集未装载）抛明确错误。</summary>
        public static Language GetOrThrow(string name)
        {
            return _registered.TryGetValue(name, out var language)
                ? language
                : throw new NotSupportedException($"语言 '{name}' 未注册：请装载对应语言程序集并触达其 Language 实例" +
                    $"（当前已注册：{(_registered.Count == 0 ? "(空)" : string.Join(", ", _registered.Keys))})。");
        }

        /// <summary>共享内建类型名（两语言同义：any/bool/char/string/void），未命中回落语言专属词汇。
        /// 原 <see cref="Binding.Binder.LookupBuiltinType"/> 的方言分流收敛于此。</summary>
        public TypeSymbol? LookupBuiltinType(string name) => name switch
        {
            "any" => TypeSymbol.Any,
            "bool" => TypeSymbol.Boolean,
            "char" => TypeSymbol.Char,
            "string" => TypeSymbol.String,
            "void" => TypeSymbol.Void,
            _ => LookupSpecificBuiltinType(name),
        };

        protected abstract TypeSymbol? LookupSpecificBuiltinType(string name);

        /// <summary>参数拼写：true = 类型前置（`.cs` `int x`）；false = 名称前置（`.co` `x: i32`）。
        /// 供参数绿往返源序化（ParameterSyntax.IsTypeFirst）判别。</summary>
        public virtual bool ParametersAreTypeFirst => false;

        /// <summary>
        /// 关键字识别（P1-A 词法分家）：文本 → 关键字 kind，未命中返回 <see cref="SyntaxKind.IdentifierToken"/>。
        /// 基类 = 共享关键字表（<see cref="SyntaxFacts.GetKeywordKind"/>）；
        /// 语言专属表经 override 排除对方语言独占词（C# 侧 CO 词在 P1-A(ii) 回落标识符）。
        /// </summary>
        public virtual SyntaxKind GetKeywordKind(string text)
        {
            return SyntaxFacts.GetKeywordKind(text);
        }

        /// <summary>按本语言创建解析器（完整树）。</summary>
        internal abstract IParser CreateParser(SyntaxTree syntaxTree);

        /// <summary>按本语言创建解析器（预词法 token，插值洞子解析用）。</summary>
        internal abstract IParser CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens);

        /// <summary>
        /// 按本语言创建词法分析器（S-2 Lexer 分家，对称 <see cref="CreateParser(SyntaxTree)"/>）。
        /// 共享 <see cref="SyntaxKind"/>（token 存储层留 Core）；CO/C# 各自实现
        /// <see cref="Syntax.CocoaLexer"/>/<see cref="Syntax.CSharpLexer"/> 落位语言库。
        /// </summary>
        internal abstract ILexer CreateLexer(SyntaxTree syntaxTree);

        /// <summary>从指定位置开始词法（插值洞子解析，位置须指向洞首）。</summary>
        internal abstract ILexer CreateLexer(SyntaxTree syntaxTree, int start);

        /// <summary>
        /// 按本语言创建编译对象（S-4.2 Compilation 分家，对称 <see cref="CreateParser(SyntaxTree)"/>）。
        /// CO/C# 子类各自返回 <see cref="CocoaCompilation"/>/<see cref="CSharpCompilation"/>（语言库内），
        /// <see cref="Compilation.Create"/> 经此工厂分派，Core 不再直接实例化语言 Compilation 子类。
        /// </summary>
        internal abstract Compilation CreateCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees);

        /// <summary>
        /// 按本语言创建绑定器（S-4.3b/c 分派：返回窄接口 <see cref="IBinder"/>，Core 共享服务经接口消费；
        /// CO/C# 子类各自返回语言库 Binder 副本）。
        /// </summary>
        internal abstract IBinder CreateBinder(bool isScript, Binding.BoundScope? parent, Symbols.FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces, Func<string, Symbols.TypeSymbol?> builtinTypeResolver, ImmutableArray<string> usingStatics = default, ImmutableDictionary<string, string> usingAliases = null!, ImmutableArray<Cocoa.CodeAnalysis.Serialization.CoaProgram> codLibraries = default, Symbols.NamespaceSymbol? globalNamespace = null);

        /// <summary>
        /// 按本语言构建单态化重绑函数体（S-4.3b 分派：Core <see cref="Binder.Monomorphizer"/> 经此调用，
        /// 语言子类委托各自语言库 Binder 的静态 <c>BuildFunctionBodyForMonomorphization</c>）。
        /// </summary>
        internal abstract (Binding.BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, Binding.BoundScope parentScope, Symbols.FunctionSymbol function, Binding.BoundGlobalScope globalScope, ImmutableArray<Cocoa.CodeAnalysis.Serialization.CoaProgram> codLibraries, Dictionary<string, Symbols.TypeSymbol> typeArgumentsByName);

        /// <summary>
        /// 绿→类型化红节点（P1-3 钩子预备）：语言库各自持有一份类型化红节点构建器
        /// （P2-4 落地；P1 委托共享 <see cref="GreenNode.CreateTypedRed"/> 保持行为不变）。
        /// </summary>
        internal abstract SyntaxNode CreateTypedRed(GreenNode green, SyntaxTree syntaxTree, int position);

        /// <summary>
        /// 泛型用法扫描（P1-3 钩子预备）：返回语言中性的 (类型名, 实参列表) 对，共享
        /// <see cref="Binder.Monomorphizer"/> 保持单实现（P1 委托共享扫描；P2-5 切语言节点后由语言库自持）。
        /// </summary>
        internal abstract IEnumerable<(SyntaxToken Identifier, ImmutableArray<SyntaxNode> Arguments)> CollectGenericUsages(Binding.BoundGlobalScope globalScope);

        /// <summary>
        /// 声明的命名空间名集合（P1-3 钩子预备，P2-6 消费者适配用）。
        /// </summary>
        internal abstract ImmutableArray<string> GetDeclaredNamespaceNames(SyntaxTree syntaxTree);

        /// <summary>根成员集合（P1-3 钩子预备，P2-6 Repl/测试消费用）。</summary>
        internal abstract ImmutableArray<SyntaxNode> GetRootMembers(SyntaxTree syntaxTree);

        /// <summary>
        /// 按本语言创建语义模型（P1-3 钩子预备；P1-5 落地 CocoaSemanticModel/CSharpSemanticModel 分派）。
        /// </summary>
        internal abstract SemanticModel CreateSemanticModel(Compilation compilation, SyntaxTree syntaxTree);

        /// <summary>
        /// 不可达代码位置解析（P1-3 钩子预备；P1-4 供 <see cref="DiagnosticBag.ReportUnreachableCode(SyntaxNode)"/>
        /// 分派，P2-5 切语言节点后由语言库自持）。
        /// </summary>
        internal abstract TextLocation? GetUnreachableCodeLocation(SyntaxNode node);

        /// <summary>
        /// 声明名 token 位置（P2-6 钩子）：供共享 Core 消费者（<c>Compilation</c>/<c>NativeImportValidator</c>）
        /// 语言中性获取函数/类等声明的 <c>Identifier.Location</c>；语言库按语言节点实现。
        /// </summary>
        internal abstract TextLocation? GetDeclarationNameLocation(SyntaxNode? declaration);

        /// <summary>
        /// 类声明是否带 facade 修饰符（P2-6 钩子）：共享 Core <c>Compilation.DeclaredFacade</c> 经此分派。
        /// </summary>
        internal abstract bool HasDeclaredFacadeModifier(SyntaxNode? declaration);
    }
}