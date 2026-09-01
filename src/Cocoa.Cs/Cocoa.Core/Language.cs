using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语言（M2 设计 X）：对标 Roslyn 语言前端抽象。每门语言一个实例，承载
    /// 名字 / 内建类型名词汇 / 解析器工厂 / 参数拼写策略；
    /// 语言专属实现以 <see cref="Language"/> 子类落入各自程序集。
    /// C# 方言全套位于独立程序集 Cocoa.Core.CSharp（<see cref="Cocoa.CodeAnalysis.CSharpLanguage"/>）；
    /// CO 宿主语言 CocoaLanguage（Y-A3-4）迁入独立程序集 Cocoa.Core.Cocoa，
    /// 核心经 <see cref="Cocoa"/> 反射装载并触达之（默认解析路径依赖 Cocoa.Core.Cocoa 在应用目录）。
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

        /// <summary>Cocoa 宿主语言（默认，`.co`）：实例位于 Cocoa.Core.Cocoa，此处经注册表 / 反射装载解析。</summary>
        public static Language Cocoa => _cocoa ??= CreateCocoa();

        /// <summary>C# 方言（`.cs`）：实例位于 Cocoa.Core.CSharp，此处经注册表 / 反射装载解析。</summary>
        public static Language CSharp => _csharp ??= CreateCSharp();

        private static Language CreateCocoa()
        {
            if (_registered.TryGetValue("cocoa", out var language))
            {
                return language;
            }

            // Y-A3-4：CocoaLanguage 随 CO L1 迁入 Cocoa.Core.Cocoa；反射装载并触达 Instance（静态初始化经 base("cocoa") 注册）。
            var assembly = System.Reflection.Assembly.Load("Cocoa.Core.Cocoa");
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

            var assembly = System.Reflection.Assembly.Load("Cocoa.Core.CSharp");
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
        /// 按本语言创建词法分析器（P1-E-2e Lexer 分家，对称 <see cref="CreateParser(SyntaxTree)"/>）。
        /// 基类默认返回共享 <see cref="Syntax.Lexer"/>；CO/C# 子类各自返回 <see cref="Syntax.CocoaLexer"/>/<see cref="Syntax.CSharpLexer"/>。
        /// 语法中立的分词逻辑留 Core（<see cref="Syntax.Lexer"/>），语言差异（关键字表等）经本工厂 + 子类落位语言库。
        /// </summary>
        internal virtual Syntax.Lexer CreateLexer(SyntaxTree syntaxTree)
        {
            return new Syntax.Lexer(syntaxTree);
        }

        /// <summary>从指定位置开始词法（插值洞子解析，位置须指向洞首）。</summary>
        internal virtual Syntax.Lexer CreateLexer(SyntaxTree syntaxTree, int start)
        {
            return new Syntax.Lexer(syntaxTree, start);
        }

        /// <summary>
        /// 按本语言创建绑定器（P1-B 分叉前置，对称 <see cref="CreateParser(SyntaxTree)"/>）。
        /// 基类默认返回共享 <see cref="Binding.Binder"/>；CO/C# 子类各自返回 <see cref="Binding.CocoaBinder"/>/<see cref="Binding.CSharpBinder"/>。
        /// 参数与 <see cref="Binding.Binder"/> 构造器一致；解析器（builtin type resolver）由各语言子类以自身
        /// <see cref="LookupBuiltinType"/> 提供——保持 Binder 语言中性（M2 设计 X）。
        /// </summary>
        internal virtual Binding.Binder CreateBinder(bool isScript, Binding.BoundScope? parent, Symbols.FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces, Func<string, Symbols.TypeSymbol?> builtinTypeResolver, ImmutableArray<string> usingStatics = default, ImmutableDictionary<string, string> usingAliases = null, ImmutableArray<Coa.CoaProgram> codLibraries = default, Symbols.NamespaceSymbol? globalNamespace = null)
        {
            return new Binding.Binder(isScript, parent, function, references, usingNamespaces, builtinTypeResolver, usingStatics, usingAliases, codLibraries, globalNamespace);
        }
    }
}