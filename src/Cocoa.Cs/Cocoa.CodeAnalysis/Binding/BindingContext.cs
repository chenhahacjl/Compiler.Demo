using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定期只读上下文（P5 步骤 1）：跨绑定分片共享的只读状态聚合——诊断袋、引用、using、
    /// 内建类型解析器、运算符重载索引、扩展方法索引、全局命名空间树、cod 库、环境/元组缓存。
    /// 只读（无 setter）：可变集合（_usingNamespaces 等）作为容器整体持有，内容仍由绑定期各阶段填充。
    /// 字段经转发属性由 <see cref="CocoaBinder"/> 暴露，方法体零改动（对齐 4.1 IlEmitter→MethodBodyBuilder 模式）。
    /// </summary>
    internal sealed class BindingContext
    {
        public BindingContext(
            bool isScript,
            string[] references,
            Func<string, TypeSymbol?> builtinTypeResolver,
            ImmutableArray<string> usingNamespaces,
            ImmutableArray<string> usingStatics,
            ImmutableDictionary<string, string> usingAliases,
            ImmutableArray<CoaProgram> codLibraries,
            NamespaceSymbol? globalNamespace,
            OperatorRegistry operators,
            DiagnosticBag diagnostics)
        {
            IsScript = isScript;
            References = references;
            BuiltinTypeResolver = builtinTypeResolver;
            UsingNamespaces = new List<string>(usingNamespaces);
            if (!usingStatics.IsDefaultOrEmpty)
            {
                UsingStatics = new List<string>(usingStatics);
            }

            if (usingAliases != null)
            {
                UsingAliases = new Dictionary<string, string>(usingAliases);
            }

            CodLibraries = codLibraries.IsDefault ? ImmutableArray<CoaProgram>.Empty : codLibraries;
            AmbiguousCodTypeNames = CocoaBinder.ComputeAmbiguousCodTypeNames(CodLibraries);
            GlobalNamespace = globalNamespace;
            Operators = operators;
            Diagnostics = diagnostics;
        }

        /// <summary>诊断袋（绑定期共享）。</summary>
        public DiagnosticBag Diagnostics { get; }

        /// <summary>脚本模式（REPL 链语义）。</summary>
        public bool IsScript { get; }

        /// <summary>引用程序集路径。只读快照（ToArray）。</summary>
        public string[] References { get; }

        /// <summary>内建类型名解析器（"int"→TypeSymbol）。</summary>
        public Func<string, TypeSymbol?> BuiltinTypeResolver { get; }

        /// <summary>using 命名空间列表（可变容器，绑定期填充）。</summary>
        public List<string> UsingNamespaces { get; } = new List<string>();

        /// <summary>using static 列表（可变容器）。</summary>
        public List<string> UsingStatics { get; } = new List<string>();

        /// <summary>using 别名表（可变容器）。</summary>
        public Dictionary<string, string> UsingAliases { get; } = new Dictionary<string, string>();

        /// <summary>cod 库集合（跨库符号解析）。</summary>
        public ImmutableArray<CoaProgram> CodLibraries { get; }

        /// <summary>跨用户库同名类型全名集（非限定使用拒绝解析）。</summary>
        public ImmutableHashSet<string> AmbiguousCodTypeNames { get; }

        /// <summary>全局命名空间树（声明阶段后可用；函数体绑定注入）。</summary>
        public NamespaceSymbol? GlobalNamespace { get; }

        /// <summary>运算符重载索引（跨 binder 实例共享）。</summary>
        public OperatorRegistry Operators { get; }

        /// <summary>环境类缓存（每宿主函数一个合成 __Env_&lt;fn&gt; 类）。</summary>
        public Dictionary<FunctionSymbol, NamedTypeSymbol> EnvironmentClasses { get; } = new();

        /// <summary>元组类型缓存（值元组 → 合成类）。</summary>
        public Dictionary<string, NamedTypeSymbol> TupleTypes { get; } = new();

        /// <summary>元组构造体缓存（合成类 .ctor 体）。</summary>
        public Dictionary<FunctionSymbol, Bound.BoundBlockStatement> TupleCtorBodies { get; } = new();

        /// <summary>扩展方法索引（BindFunctionDeclaration 登记，成员调用失败回退）。</summary>
        public HashSet<FunctionSymbol> ExtensionMethods { get; } = new();
    }
}