using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语言（M2 设计 X）：对标 Roslyn 语言前端抽象。每门语言一个实例，承载
    /// 名字 / 内建类型名词汇 / 解析器工厂 / 参数拼写策略；
    /// 语言专属实现以 <see cref="Language"/> 子类落入各自程序集。
    /// CO 作为宿主默认语言内置于核心（<see cref="CocoaLanguage"/>）；
    /// C# 方言全套位于独立程序集 Cocoa.Core.CSharp（<see cref="Cocoa.CodeAnalysis.CSharpLanguage"/>）。
    /// 新语言 = 新增 Language 子类（含解析器），核心零改动（设计 X §6.3）。
    /// </summary>
    public abstract class Language
    {
        private static readonly Dictionary<string, Language> _registered = new();
        private static Language? _cocoa;

        protected Language(string name)
        {
            Name = name;
            _registered[name] = this;
        }

        public string Name { get; }

        /// <summary>Cocoa 宿主语言（默认，`.co`；核心内置，供 <see cref="Syntax.SyntaxTree.Parse(string)"/> 等默认路径）。</summary>
        public static Language Cocoa => _cocoa ??= CocoaLanguage.Instance;

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

        /// <summary>按本语言创建解析器（完整树）。</summary>
        internal abstract ParserCore CreateParser(SyntaxTree syntaxTree);

        /// <summary>按本语言创建解析器（预词法 token，插值洞子解析用）。</summary>
        internal abstract ParserCore CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens);
    }

    /// <summary>
    /// Cocoa 宿主语言（默认，`.co`）：内建类型简写 i8/u8/i16/u16/i32/u32/i64/u64/f32/f64
    /// （+ i128/u128/f128 占位；C# 原名映射见 <see cref="Cocoa.CodeAnalysis.CSharpLanguage"/>）。
    /// 保留于核心 = 核心即 CO 工具链本体（承载默认 Language 语义，避免默认解析依赖外部程序集注册空窗）。
    /// </summary>
    public sealed class CocoaLanguage : Language
    {
        public static readonly CocoaLanguage Instance = new CocoaLanguage();

        private CocoaLanguage()
            : base("cocoa")
        {
        }

        protected override TypeSymbol? LookupSpecificBuiltinType(string name) => name switch
        {
            "i8" => TypeSymbol.Int8,
            "u8" => TypeSymbol.UInt8,
            "i16" => TypeSymbol.Int16,
            "u16" => TypeSymbol.UInt16,
            "i32" => TypeSymbol.Int32,
            "u32" => TypeSymbol.UInt32,
            "i64" => TypeSymbol.Int64,
            "u64" => TypeSymbol.UInt64,
            "f32" => TypeSymbol.Float,
            "f64" => TypeSymbol.Double,
            "i128" => TypeSymbol.Int128,
            "u128" => TypeSymbol.UInt128,
            "f128" => TypeSymbol.Float128,
            _ => null,
        };

        internal override ParserCore CreateParser(SyntaxTree syntaxTree) => new CocoaParser(syntaxTree);

        internal override ParserCore CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
            => new CocoaParser(syntaxTree, tokens);
    }
}