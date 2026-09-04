using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    public abstract partial class Compilation
    {
        private BoundGlobalScope? _globalScope;
        private readonly string _entryPointName;
        private readonly string[] _references;
        private readonly ImmutableArray<MetadataReference> _metadataReferences;
        private readonly ImmutableArray<CoaProgram> _codLibraries;

        /// <summary>动态链接（阶段 A2）：dotnet 后端消费 `.coa` 时不内联库体，发射外部 Ref 指向各库 dll。</summary>
        private readonly bool _linkCodDynamically;

        /// <summary>
        /// managed（dotnet/IL）后端发射委托（拆分后由 <c>Cocoa.CodeGen.IL</c> 经 <see cref="RegisterManagedEmitter"/> 注入；
        /// Core 不引用后端，发射能力经此委托接入）。volatile：注册发生在宿主启动、读取在编译线程（重构阶段 1a/A7）。
        /// </summary>
        private static volatile Func<BoundProgram, string, string[], string, IlTarget, bool, ImmutableDictionary<object, string>?, bool, ImmutableArray<Diagnostic>>? _managedEmitter;

        /// <summary>native 后端发射委托（由 <c>Cocoa.CodeGen.Native</c> 经 <see cref="RegisterNativeEmitter"/> 注入，含后端专属校验）。</summary>
        private static volatile Func<Compilation, string, string, TargetPlatform, ImmutableArray<Diagnostic>>? _nativeEmitter;

        /// <summary>注册 managed（dotnet/IL）后端发射实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        internal static void RegisterManagedEmitter(Func<BoundProgram, string, string[], string, IlTarget, bool, ImmutableDictionary<object, string>?, bool, ImmutableArray<Diagnostic>> emitter)
            => _managedEmitter = emitter;

        /// <summary>注册 native 后端发射实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        internal static void RegisterNativeEmitter(Func<Compilation, string, string, TargetPlatform, ImmutableArray<Diagnostic>> emitter)
            => _nativeEmitter = emitter;

        /// <summary>
        /// 解释器求值委托（4.1）：由 <c>Cocoa.CodeGen.Interpreter</c> 经 <see cref="RegisterInterpreterEvaluator"/> 注册；
        /// Core 自身不引用后端。args 为 null 表示无参 REPL 求值，否则为 Main(string[]) 形态。
        /// </summary>
        private static volatile Func<BoundProgram, string[]?, Dictionary<VariableSymbol, object>, object?>? _interpreterEvaluator;

        /// <summary>注册解释器求值实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        internal static void RegisterInterpreterEvaluator(Func<BoundProgram, string[]?, Dictionary<VariableSymbol, object>, object?> evaluator)
            => _interpreterEvaluator = evaluator;

        public abstract Language Language { get; }

        /// <summary>
        /// 按本语言绑定全局作用域（S-4.3 Compilation 驱动 Binder：对齐 Roslyn
        /// <c>CSharpCompilation</c> 驱动 <c>CSharpBinder</c>）。语言子类调用各自语言库的 Binder 静态编排。
        /// </summary>
        internal abstract BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName, string[]? references, ImmutableArray<CoaProgram> codLibraries);

        /// <summary>按本语言绑定程序（含单态化/降级；见 <see cref="BindGlobalScope"/>）。</summary>
        internal abstract BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CoaProgram> codLibraries, Language dialect, bool linkCodDynamically, NamespaceSymbol? globalNamespace);

        protected Compilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically = false, params SyntaxTree[] syntaxTrees)
        {
            IsScript = isScript;
            Previous = previous;
            _entryPointName = entryPointName;
            _linkCodDynamically = linkCodDynamically;
            _references = (references ?? Array.Empty<string>())
                .Where(r => !r.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _metadataReferences = (references ?? Array.Empty<string>())
                .Select(r => new MetadataReference(r))
                .ToImmutableArray();
            _codLibraries = LoadCodLibraries(references);
            SyntaxTrees = syntaxTrees.ToImmutableArray();
        }


        public static Compilation Create(params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: false, previous: null, entryPointName: "Main", references: null, linkCodDynamically: false, syntaxTrees);
        }

        public static Compilation Create(string[] references, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: false, previous: null, entryPointName: "Main", references, linkCodDynamically: false, syntaxTrees);
        }

        /// <summary>动态链接变体（阶段 A2）：dotnet 后端消费 `.coa` 时不内联，运行期依赖各库 dll。</summary>
        public static Compilation Create(string[] references, bool linkCodDynamically, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: false, previous: null, entryPointName: "Main", references, linkCodDynamically, syntaxTrees);
        }

        public static Compilation Create(string entryPointName, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: false, previous: null, entryPointName, references: null, linkCodDynamically: false, syntaxTrees);
        }

        public static Compilation Create(string entryPointName, string[] references, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: false, previous: null, entryPointName, references, linkCodDynamically: false, syntaxTrees);
        }

        /// <summary>动态链接变体（阶段 A2）：带入口名的 dotnet 消费方，`.coa` 库以外部 dll 依赖接入。</summary>
        public static Compilation Create(string entryPointName, string[] references, bool linkCodDynamically, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: false, previous: null, entryPointName, references, linkCodDynamically, syntaxTrees);
        }

        public static Compilation CreateScript(Compilation? previous, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: true, previous, entryPointName: "Main", references: null, linkCodDynamically: false, syntaxTrees);
        }

        /// <summary>带引用的脚本编译（REPL #import 场景）：references 为 `.coa` 库/程序集路径。</summary>
        public static Compilation CreateScript(Compilation? previous, string[]? references, params SyntaxTree[] syntaxTrees)
        {
            return CreateCompilation(isScript: true, previous, entryPointName: "Main", references: references, linkCodDynamically: false, syntaxTrees);
        }

        /// <summary>
        /// 经语言工厂分派（Y §6.7 A0 + S-4.2 Compilation 分家）：CO → <see cref="CocoaCompilation"/>，
        /// C# → <see cref="CSharpCompilation"/>；子类随语言库落位，Core 仅持 <see cref="Language"/> 抽象。
        /// 空语法树 / 脚本默认 Cocoa，行为等价，API 面不变。
        /// </summary>
        private static Compilation CreateCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
        {
            var language = syntaxTrees.Length == 0 ? Language.Cocoa : syntaxTrees[0].Language;
            return language.CreateCompilation(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees);
        }

        public bool IsScript { get; }
        public Compilation? Previous { get; }
        public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
        public FunctionSymbol? MainFunction => GlobalScope.MainFunction;
        public ImmutableArray<FunctionSymbol> Functions => GlobalScope.Functions;
        public ImmutableArray<VariableSymbol> Variables => GlobalScope.Variables;

        /// <summary>已加载的 `.coa` 库（含系统库；动态链接 CopyLocal 依据）。</summary>
        internal ImmutableArray<CoaProgram> CodLibraries => _codLibraries;

        internal BoundGlobalScope GlobalScope
        {
            get
            {
                var scope = _globalScope;
                if (scope != null)
                {
                    return scope;
                }

                var globalScope = BindGlobalScope(IsScript, Previous?.GlobalScope, SyntaxTrees, _entryPointName, _references, _codLibraries);
                Interlocked.CompareExchange(ref _globalScope, globalScope, null);
                // CAS 后重读（与 SourceAssembly 同模式）：并发绑定结果竞争失败方返回胜者
                return _globalScope!;
            }
        }

        public IEnumerable<Symbol> GetSymbols()
        {
            var submission = this;
            var seenSymbolNames = new HashSet<string>();

            var builtinFunctions = BuiltinFunctions.GetAll().ToList();

            while (submission != null)
            {
                foreach (var function in submission.Functions)
                    if (seenSymbolNames.Add(function.Name))
                        yield return function;

                foreach (var variable in submission.Variables)
                    if (seenSymbolNames.Add(variable.Name))
                        yield return variable;

                foreach (var builtin in builtinFunctions)
                    if (seenSymbolNames.Add(builtin.Name))
                        yield return builtin;

                submission = submission.Previous;
            }
        }

        /// <summary>本编译的全部诊断（对齐 Roslyn <c>Compilation.GetDiagnostics</c>）：语法解析 +
        /// 全局声明 + 函数体绑定；声明有错时短路（体绑定无意义）。</summary>
        public ImmutableArray<Diagnostic> GetDiagnostics()
        {
            var builder = ImmutableArray.CreateBuilder<Diagnostic>();
            builder.AddRange(GlobalScope.Diagnostics);
            if (!GlobalScope.Diagnostics.HasErrors())
            {
                builder.AddRange(GetProgram().Diagnostics);
            }

            return builder.ToImmutable();
        }


        /// <summary>为指定语法树获取语义模型（对齐 Roslyn <c>Compilation.GetSemanticModel</c>；
        /// P1-5 经 <see cref="Language.CreateSemanticModel"/> 分派语言专属语义模型）。</summary>
        public SemanticModel GetSemanticModel(SyntaxTree syntaxTree)
        {
            return Language.CreateSemanticModel(this, syntaxTree);
        }


        /// <summary>引用的元数据引用（对齐 Roslyn <c>Compilation.References</c>；含 .coa 库与程序集路径，保持传入顺序）。</summary>
        public ImmutableArray<MetadataReference> References => _metadataReferences;


        internal BoundProgram GetProgram()
        {
            var previous = Previous == null ? null : Previous.GetProgram();

            var program = BindProgram(IsScript, previous, GlobalScope, _codLibraries, SyntaxTrees.IsDefaultOrEmpty ? Language.Cocoa : SyntaxTrees[0].Language, _linkCodDynamically, GlobalNamespace);

            // Y A2-F1：规范 IR 契约（DEBUG）——消费边界不得有高 Bound 节点泄漏
            Lowering.CanonicalIr.Verify(program);

            return program;
        }

        /// <summary>
        /// 求值
        /// </summary>
        public EvaluationResult Evaluate(Dictionary<VariableSymbol, object> variables)
        {
            if (GlobalScope.Diagnostics.HasErrors())
            {
                return new EvaluationResult(GlobalScope.Diagnostics, null);
            }

            var program = GetProgram();

            if (program.Diagnostics.HasErrors())
            {
                return new EvaluationResult(program.Diagnostics, null);
            }

            var evaluator = _interpreterEvaluator
                ?? throw new InvalidOperationException("解释器后端未注册（Cocoa.CodeGen.Interpreter 未初始化）");

            var value = evaluator(program, null, variables);

            return new EvaluationResult(program.Diagnostics, value);
        }

        public EvaluationResult Evaluate(string[] args, Dictionary<VariableSymbol, object> variables)
        {
            if (GlobalScope.Diagnostics.HasErrors())
            {
                return new EvaluationResult(GlobalScope.Diagnostics, null);
            }

            var program = GetProgram();

            if (program.Diagnostics.HasErrors())
            {
                return new EvaluationResult(program.Diagnostics, null);
            }

            var evaluator = _interpreterEvaluator
                ?? throw new InvalidOperationException("解释器后端未注册（Cocoa.CodeGen.Interpreter 未初始化）");

            var value = evaluator(program, args, variables);

            return new EvaluationResult(program.Diagnostics, value);
        }





        /// <summary>
        /// 绑定树直接子节点（重构阶段 1a/A1）：实现委托给 <see cref="BoundNodeChildren"/>，
        /// 与 BoundTreeRewriter 的节点清单保持单一事实来源。旧手写 switch（120 行、漏
        /// Throw/Try/ConstructorChain/ByRefArgument 四类节点）已删除。
        /// </summary>
        internal static IEnumerable<BoundNode> BoundChildren(BoundNode node)
        {
            return BoundNodeChildren.Of(node);
        }
    }
}
