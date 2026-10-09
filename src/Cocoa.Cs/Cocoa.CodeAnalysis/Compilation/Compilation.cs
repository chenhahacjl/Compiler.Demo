using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Documentation;
using System.IO;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 发射管线（4.2 自 Compilation.cs 拆出，partial 分文件）：EmitTree/Emit/EmitNative/EmitCocoa 与函数值/OOP 门禁扫描。
    /// </summary>
    /// <summary>
    /// 命名空间与类型解析（4.2 自 Compilation.cs 拆出，partial 分文件）：全局命名空间树构建、GetTypeByMetadataName/GetNamespace 等查询。
    /// </summary>
    /// <summary>
    /// 程序集与 .coa 库引用管理的实例部分：SourceAssembly/ReferencedAssemblies（惰性缓存）+ ValidateCodBackendRequirements。
    /// 静态部分（库加载/拓扑序/序列化门禁判据）已拆至 <see cref="AssemblyReferenceManager"/>。
    /// </summary>
    public class Compilation
    {
        private BoundGlobalScope? _globalScope;
        private readonly string _entryPointName;
        private readonly string[] _references;
        private readonly ImmutableArray<MetadataReference> _metadataReferences;
        private readonly ImmutableArray<CoaProgram> _codLibraries;
        private readonly ImmutableArray<string> _ambiguousCodTypeNames;

        /// <summary>6f-3：跨用户库同名类型（类/枚举/泛型定义）全名集——非限定使用即绑定期歧义；
        /// 消歧经 `using X = 库名.全名` 库限定解析。</summary>
        public ImmutableArray<string> AmbiguousCodTypeNames => _ambiguousCodTypeNames;

        /// <summary>动态链接（阶段 A2）：dotnet 后端消费 `.coa` 时不内联库体，发射外部 Ref 指向各库 dll。</summary>
        private readonly bool _linkCodDynamically;

        /// <summary>
        /// managed（dotnet/IL）后端发射委托（拆分后由 <c>Cocoa.CodeGen.Managed.Writer</c> 经 <see cref="RegisterManagedEmitter"/> 注入；
        /// Core 不引用后端，发射能力经此委托接入）。volatile：注册发生在宿主启动、读取在编译线程（重构阶段 1a/A7）。
        /// </summary>
        private static volatile Func<BoundProgram, string, string[], string, IlTarget, bool, ImmutableDictionary<object, string>?, bool, ImmutableArray<Diagnostic>>? _managedEmitter;

        /// <summary>native 后端发射委托（由 <c>Cocoa.CodeGen.Native</c> 经 <see cref="RegisterNativeEmitter"/> 注入，含后端专属校验）。</summary>
        private static volatile Func<Compilation, string, string, TargetPlatform, ushort, ImmutableArray<Diagnostic>>? _nativeEmitter;

        /// <summary>注册 managed（dotnet/IL）后端发射实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        public static void RegisterManagedEmitter(Func<BoundProgram, string, string[], string, IlTarget, bool, ImmutableDictionary<object, string>?, bool, ImmutableArray<Diagnostic>> emitter)
            => _managedEmitter = emitter;

        /// <summary>注册 native 后端发射实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        public static void RegisterNativeEmitter(Func<Compilation, string, string, TargetPlatform, ushort, ImmutableArray<Diagnostic>> emitter)
            => _nativeEmitter = emitter;

        /// <summary>
        /// 解释器求值委托（4.1）：由 <c>Cocoa.CodeGen.Interpreter</c> 经 <see cref="RegisterInterpreterEvaluator"/> 注册；
        /// Core 自身不引用后端。args 为 null 表示无参 REPL 求值，否则为 Main(string[]) 形态。
        /// </summary>
        private static volatile Func<BoundProgram, string[]?, Dictionary<VariableSymbol, object>, object?>? _interpreterEvaluator;

        /// <summary>注册解释器求值实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        public static void RegisterInterpreterEvaluator(Func<BoundProgram, string[]?, Dictionary<VariableSymbol, object>, object?> evaluator)
            => _interpreterEvaluator = evaluator;

        /// <summary>
        /// 解释器「按名调用顶层函数」委托（嵌入式引擎 Call 路径）：由 <c>Cocoa.CodeGen.Interpreter</c>
        /// 经 <see cref="RegisterInterpreterFunctionEvaluator"/> 注册。Core 自身不引用后端。
        /// </summary>
        private static volatile Func<BoundProgram, FunctionSymbol, object?[], Dictionary<VariableSymbol, object>, object?>? _interpreterFunctionEvaluator;

        /// <summary>注册解释器「按名调用顶层函数」实现（后端/宿主启动时调用；Core 自身不引用后端）。</summary>
        public static void RegisterInterpreterFunctionEvaluator(Func<BoundProgram, FunctionSymbol, object?[], Dictionary<VariableSymbol, object>, object?> evaluator)
            => _interpreterFunctionEvaluator = evaluator;

        /// <summary>绑定全局作用域（经 <see cref="CocoaBinder"/>.BindGlobalScope 静态编排）。</summary>
        public BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName, string[]? references, ImmutableArray<CoaProgram> codLibraries)
            => CocoaBinder.BindGlobalScope(isScript, previous, syntaxTrees, entryPointName, references, codLibraries);

        /// <summary>绑定程序（含单态化/降级；见 <see cref="BindGlobalScope"/>）。</summary>
        public BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CoaProgram> codLibraries, bool linkCodDynamically, NamespaceSymbol? globalNamespace)
            => CocoaBinder.BindProgram(isScript, previous, globalScope, codLibraries, linkCodDynamically, globalNamespace);

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
            var loadedLibraries = AssemblyReferenceManager.LoadCodLibraries(references);
            _codLibraries = loadedLibraries.Libraries;
            _ambiguousCodTypeNames = loadedLibraries.AmbiguousTypeNames;
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

        /// <summary>构造编译对象（单语言，直接实例化）。</summary>
        private static Compilation CreateCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            => new Compilation(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees);

        public bool IsScript { get; }
        public Compilation? Previous { get; }
        public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
        public FunctionSymbol? MainFunction => GlobalScope.MainFunction;
        public ImmutableArray<FunctionSymbol> Functions => GlobalScope.Functions;
        public ImmutableArray<VariableSymbol> Variables => GlobalScope.Variables;

        /// <summary>已加载的 `.coa` 库（含系统库；动态链接 CopyLocal 依据）。</summary>
        public ImmutableArray<CoaProgram> CodLibraries => _codLibraries;

        public BoundGlobalScope GlobalScope
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


        /// <summary>为指定语法树获取语义模型（对齐 Roslyn <c>Compilation.GetSemanticModel</c>）。</summary>
        public SemanticModel GetSemanticModel(SyntaxTree syntaxTree)
        {
            return new SemanticModel(this, syntaxTree);
        }


        /// <summary>引用的元数据引用（对齐 Roslyn <c>Compilation.References</c>；含 .coa 库与程序集路径，保持传入顺序）。</summary>
        public ImmutableArray<MetadataReference> References => _metadataReferences;


        public BoundProgram GetProgram()
        {
            var previous = Previous == null ? null : Previous.GetProgram();

            var program = BindProgram(IsScript, previous, GlobalScope, _codLibraries, _linkCodDynamically, GlobalNamespace);

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
        /// 嵌入式引擎 Call 路径：按名在 submission 链（含 previous，Latest 优先）查找顶层函数并带实参求值。
        /// 未找到抛 <see cref="ArgumentException"/>；运行期异常原样上抛（引擎宿主可订阅 Error 事件）。
        /// </summary>
        public EvaluationResult EvaluateFunction(string name, object?[] args, Dictionary<VariableSymbol, object> variables)
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

            var function = FindFunction(name);
            if (function == null)
            {
                throw new ArgumentException($"未找到可调用的顶层函数 '{name}'", nameof(name));
            }

            var evaluator = _interpreterFunctionEvaluator
                ?? throw new InvalidOperationException("解释器按名调用后端未注册（Cocoa.CodeGen.Interpreter 未初始化）");

            var value = evaluator(program, function, args, variables);

            return new EvaluationResult(program.Diagnostics, value);
        }

        /// <summary>在 submission 链（含 previous，Latest 优先）查找同名顶层函数。</summary>
        private FunctionSymbol? FindFunction(string name)
        {
            var current = this;
            while (current != null)
            {
                var function = current.Functions.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
                if (function != null)
                {
                    return function;
                }

                current = current.Previous;
            }

            return null;
        }





        /// <summary>
        /// 绑定树直接子节点（重构阶段 1a/A1）：实现委托给 <see cref="BoundNodeChildren"/>，
        /// 与 BoundTreeRewriter 的节点清单保持单一事实来源。旧手写 switch（120 行、漏
        /// Throw/Try/ConstructorChain/ByRefArgument 四类节点）已删除。
        /// </summary>
        public static IEnumerable<BoundNode> BoundChildren(BoundNode node)
        {
            return BoundNodeChildren.Of(node);
        }


        public void EmitTree(TextWriter writer)
        {
            var program = GetProgram();

            if (GlobalScope.MainFunction != null)
            {
                EmitTree(GlobalScope.MainFunction, writer);
            }
            else if (GlobalScope.ScriptFunction != null)
            {
                EmitTree(GlobalScope.ScriptFunction, writer);
            }
        }

        public void EmitTree(FunctionSymbol symbol, TextWriter writer)
        {
            var program = GetProgram();

            symbol.WriteTo(writer);
            writer.WriteLine();

            if (!program.Functions.TryGetValue(symbol, out var body))
            {
                return;
            }

            body.WriteTo(writer);
        }

        // TODO: References should be part of the compilation, not arguments for Emit
        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath)
            => Emit(moduleName, references, outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath, IlTarget target)
            => Emit(moduleName, references, outputPath, target, emitLibrary: false);

        // 引用作为编译组成部分（对齐 Roslyn：Emit 不接收引用参数，经 Compilation.References 提供）
        public ImmutableArray<Diagnostic> Emit(string moduleName, string outputPath)
            => Emit(moduleName, this.References.Select(r => r.Display).ToArray(), outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string outputPath, IlTarget target)
            => Emit(moduleName, this.References.Select(r => r.Display).ToArray(), outputPath, target, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string outputPath, IlTarget target, bool emitLibrary)
            => Emit(moduleName, this.References.Select(r => r.Display).ToArray(), outputPath, target, emitLibrary);

        // MetadataReference 形态重载（Roslyn 形态引用参数）
        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<MetadataReference> references, string outputPath)
            => Emit(moduleName, references.Select(r => r.Display).ToArray(), outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<MetadataReference> references, string outputPath, IlTarget target)
            => Emit(moduleName, references.Select(r => r.Display).ToArray(), outputPath, target, emitLibrary: false);

        // AssemblySymbol 形态重载（Emit 内部消费 AssemblySymbol：经 Display 派生路径）
        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<AssemblySymbol> references, string outputPath)
            => Emit(moduleName, references.Select(r => r.Display ?? r.Name).ToArray(), outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<AssemblySymbol> references, string outputPath, IlTarget target)
            => Emit(moduleName, references.Select(r => r.Display ?? r.Name).ToArray(), outputPath, target, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<AssemblySymbol> references, string outputPath, IlTarget target, bool emitLibrary)
            => Emit(moduleName, references.Select(r => r.Display ?? r.Name).ToArray(), outputPath, target, emitLibrary);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary)
        {
            var parseDiagnostics = SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = GetProgram();

            // 与 Evaluate/EmitCocoa 一致的门禁（重构阶段 1a/A3）：绑定/单态化产出的错误
            // 不得进入发射——否则带错程序会被静默生成为 dll/exe
            if (program.Diagnostics.HasErrors())
            {
                return diagnostics.Concat(program.Diagnostics).ToImmutableArray();
            }

            // 6e-M22 C4-b：IL 后端已支持函数值（Func`N 委托映射），门禁移除；native 见 EmitNative

            var ilReferences = references
                .Where(r => !r.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var backendDiagnostics = _managedEmitter == null
                ? ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "managed 后端未注册（Cocoa.CodeGen.Managed.Writer 未初始化）"))
                : _managedEmitter(program, moduleName, ilReferences, outputPath, target, emitLibrary, program.CodAssemblies, false);

            // 成功路径也带上 GlobalScope 警告（using 未解析等），供 CLI 打印
            return diagnostics.Concat(backendDiagnostics).ToImmutableArray();
        }

        /// <summary>
        /// 把程序直接生成为原生可执行文件，不依赖 .NET 运行时。
        /// 实现经 <see cref="RegisterNativeEmitter"/> 注入的 native 后端（Core 自身不引用后端）。
        /// </summary>
        public ImmutableArray<Diagnostic> EmitNative(string moduleName, string outputPath, TargetPlatform platform = default, ushort subsystem = 3 /* PeSubsystem.WindowsCui */)
        {
            if (_nativeEmitter == null)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "native 后端未注册（Cocoa.CodeGen.Native 未初始化）"));
            }

            return _nativeEmitter(this, moduleName, outputPath, platform, subsystem);
        }

        /// <summary>
        /// 把库编译为 `.coa` 语义层程序集（编译到 BoundProgram 即停，不走 IR/机器码/IL）。
        /// </summary>
        public ImmutableArray<Diagnostic> EmitCocoa(string moduleName, string outputPath, string? docPath = null)
        {
            var parseDiagnostics = SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = GetProgram();

            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            // 6e-Step D-a：lambda/函数值/闭包环境类库体接入 .coa 序列化（fnval/invoc 节点 + cls 字段）。
            // 门禁由序列化器兜底（未覆盖节点显式抛错），此处不再拦截。

            // 校验 1：库无入口
            if (program.MainFunction != null || program.ScriptFunction != null)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 的库不允许入口函数（Main/script）"));
            }

            // 校验 2：库无内部 OOP（.coa 6e-M17 起放行纯容器类；6b 起放行 facade 实例类与真体实例类；
            // 6e-M33 起放行同库可序列化基类的继承链（Handle 族）——仅剩真不可序列化类（接口/多继承基类等）报错）
            if (program.Classes.Length > 0)
            {
                var offendingClass = program.Classes.FirstOrDefault(c => !AssemblyReferenceManager.IsCodSerializableClass(c));
                if (offendingClass != null)
                {
                    var location = offendingClass.Declaration?.GetDeclarationNameLocation() ?? ZeroLocation;
                    return ImmutableArray.Create(Diagnostic.Error(location, $"库含不可序列化类 '{offendingClass.Name}'（基类不可序列化/接口等），.coa 暂不支持（纯容器/facade/真体实例类/同库可序列化基类链已支持）"));
                }
            }

            // 校验 4：必须声明 namespace
            var namespaces = CollectNamespaceNames();
            if (namespaces.Length == 0)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 库必须声明 namespace（如 `namespace MyLib { ... }`）"));
            }

// 6e-Step D-a：库函数符号集 = 顶层声明序 ∪ 绑定体原始符号 ∪ 嵌套函数值（λ 合成 __Lambda$N）——
// 否则 fnval 携带的 FnKey 消费方符号表缺失（"Unknown function"）。
var rawBodies = program.RawFunctions;
var collected = new Dictionary<FunctionSymbol, BoundBlockStatement>();
var collectedOrder = new List<FunctionSymbol>();
foreach (var (_, rawBody) in rawBodies)
{
    CollectFunctionValueBodies(rawBody, collected, collectedOrder);
}
// 嵌套 λ（内层函数值体再含函数值）至不动点
for (var pass = 0; pass < collectedOrder.Count; pass++)
{
    CollectFunctionValueBodies(collected[collectedOrder[pass]], collected, collectedOrder);
}

var functions = GlobalScope.Functions
    .Concat(rawBodies.Keys.Where(f => !GlobalScope.Functions.Contains(f)))
    .Concat(collectedOrder)
    .ToImmutableArray();
var bodies = rawBodies.AddRange(collected);
var globals = GlobalScope.Variables.OfType<GlobalVariableSymbol>().ToImmutableArray();
            var enums = GlobalScope.Enums;

            if (globals.Length > 0)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "库含全局变量，发射暂不支持（阶段 6b 后置）"));
            }

            var imports = functions
                .Where(f => f.IsExtern && f.DllName != null)
                .Select(f => f.DllName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

            var containerClasses = program.Classes.Where(AssemblyReferenceManager.IsCodSerializableClass).ToImmutableArray();

            // 6e-M24：文档化符号全集 = 顶层函数/类型/全局 + 类（含泛型定义）成员 + 枚举。
            // 类成员文档（方法含构造/属性/字段/事件）是 stdlib 文档主体，必须纳入。
            var documentableSymbols = new List<Symbol>();
            documentableSymbols.AddRange(functions);
            documentableSymbols.AddRange(containerClasses);
            foreach (var type in containerClasses.Concat(program.GenericDefinitions))
            {
                documentableSymbols.AddRange(type.Methods);
                documentableSymbols.AddRange(type.Fields);
                documentableSymbols.AddRange(type.Properties);
                documentableSymbols.AddRange(type.Events);
            }

            documentableSymbols.AddRange(globals);
            documentableSymbols.AddRange(enums);

            // 6e-M24：构建文档注释映射（DocID → 规范化原文）
            var docsBuilder = ImmutableDictionary.CreateBuilder<string, string>();
            foreach (var sym in documentableSymbols)
            {
                if (!string.IsNullOrEmpty(sym.DocumentationText))
                {
                    var docId = DocIdBuilder.GetDocId(sym);
                    if (docId != null)
                    {
                        docsBuilder[docId] = sym.DocumentationText!;
                    }
                }
            }

            var codProgram = new CoaProgram(
                functions,
                globals,
                enums,
                containerClasses,
                // S-7：.coa bodies 序列化 raw（未 Lower 结构化 HIR：for/while/if 保留），
                // 非 program.Functions（lowered/MIR）。消费方链接/动态发射处统一补 Lower。
                bodies,
                CoaRequirement.Any,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                imports,
                ImmutableArray<string>.Empty,
                namespaces,
                program.GenericDefinitions,
                program.GenericOpenBodies,
                docs: docsBuilder.ToImmutable())
            {
                // 程序集名 = 模块名：动态链接时消费方据此合成 AssemblyRef 指向同名 dll（阶段 A2）
                Name = moduleName,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            using (var writer = new StreamWriter(outputPath))
            {
                CoaSerializer.Write(writer, codProgram);
            }

            // 6e-M24：XML documentation 文件生成
            if (docPath != null)
            {
                DocumentationFileWriter.Write(docPath, moduleName, documentableSymbols);
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        /// <summary>
        /// 6e-M24：为 exe/library 产物生成 XML documentation 文件（`.coa` 由 <see cref="EmitCocoa"/> 内写出）。
        /// 遍历全局作用域符号 + 类成员，输出带 <c>DocumentationText</c> 的成员。
        /// </summary>
        public int EmitDocumentation(string moduleName, string docPath)
        {
            var symbols = new List<Symbol>();
            symbols.AddRange(GlobalScope.Functions);
            symbols.AddRange(GlobalScope.Classes);
            symbols.AddRange(GlobalScope.Enums);
            foreach (var type in GlobalScope.Classes)
            {
                symbols.AddRange(type.Methods);
                symbols.AddRange(type.Fields);
                symbols.AddRange(type.Properties);
                symbols.AddRange(type.Events);
            }

            return DocumentationFileWriter.Write(docPath, moduleName, symbols);
        }

        private TextLocation ZeroLocation
        {
            get
            {
                if (SyntaxTrees.Length > 0)
                {
                    return new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                }

                // 无语法树场景（如纯库引用编译）本就无 Text 可言；TextLocation.Text 允许 null 由消费方判空
                return new TextLocation(null!, new TextSpan(0, 0));
            }
        }

        /// <summary>新增节点序列化以兜底抛错为准（Write/Read 未覆盖的 kind 会在 EmitCocoa/载入时报显式错误，杜绝静默损坏流）。</summary>

        /// <summary>Step D-a：从已经绑定 raw 体中抽取得 λ/方法值携带的已绑定体，入库符号+body 集合（至不动点，嵌套 λ 递归发现）。</summary>
        private static void CollectFunctionValueBodies(
            BoundNode node,
            Dictionary<FunctionSymbol, BoundBlockStatement> collected,
            List<FunctionSymbol> order)
        {
            if (node is BoundFunctionValueExpression { Body: not null } functionValue &&
                !collected.ContainsKey(functionValue.Function))
            {
                collected.Add(functionValue.Function, functionValue.Body);
                order.Add(functionValue.Function);
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                if (child != null)
                {
                    CollectFunctionValueBodies(child, collected, order);
                }
            }
        }

        /// <summary>按名称枚举符号（对齐 Roslyn <c>Compilation.GetSymbolsWithName</c>）：
        /// 命名类型 + 顶层函数（经全局命名空间树）+ 全局变量；去重。</summary>
        public IEnumerable<Symbol> GetSymbolsWithName(string name)
        {
            var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (var ns in EnumerateNamespaces(GlobalNamespace))
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    if (type.Name == name && seen.Add(type))
                    {
                        yield return type;
                    }
                }

                foreach (var function in ns.GetFunctionMembers())
                {
                    if (function.Name == name && seen.Add(function))
                    {
                        yield return function;
                    }
                }
            }

            foreach (var variable in Variables)
            {
                if (variable.Name == name && seen.Add(variable))
                {
                    yield return variable;
                }
            }
        }

        private static IEnumerable<NamespaceSymbol> EnumerateNamespaces(NamespaceSymbol root)
        {
            yield return root;
            foreach (var child in root.GetNamespaceMembers())
            {
                foreach (var nested in EnumerateNamespaces(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>按元数据全名解析类型（对齐 Roslyn <c>CSharpCompilation.GetTypeByMetadataName</c>）。
        /// 内建特殊类型（基元/Object/Type/String/Void）优先，其次全局命名空间树（源 + 注入的 .coa 库）类/枚举/
        /// 泛型定义。支持后置 [] 数组全名、泛型定义（<c>"...List`1"</c>）与实例化 mangle（<c>"...List`1#System.Int32"</c>）。</summary>
        public TypeSymbol? GetTypeByMetadataName(string fullyQualifiedName)
        {
            var elementName = fullyQualifiedName;
            var isArray = false;
            if (fullyQualifiedName.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                elementName = fullyQualifiedName.Substring(0, fullyQualifiedName.Length - 2);
            }

            TypeSymbol? type = elementName switch
            {
                "System.Object" => NamedTypeSymbol.SystemObject,
                "System.Type" => NamedTypeSymbol.SystemType,
                "System.String" => TypeSymbol.String,
                "System.Void" => TypeSymbol.Void,
                "System.Boolean" => TypeSymbol.Boolean,
                "System.SByte" => TypeSymbol.Int8,
                "System.Byte" => TypeSymbol.UInt8,
                "System.Int16" => TypeSymbol.Int16,
                "System.UInt16" => TypeSymbol.UInt16,
                "System.Int32" => TypeSymbol.Int32,
                "System.UInt32" => TypeSymbol.UInt32,
                "System.Int64" => TypeSymbol.Int64,
                "System.UInt64" => TypeSymbol.UInt64,
                "System.Single" => TypeSymbol.Float,
                "System.Double" => TypeSymbol.Double,
                "System.Char" => TypeSymbol.Char,
                _ => null,
            };

            if (type == null)
            {
                type = ResolveNamedTypeByMetadataName(elementName);
            }

            return isArray && type != null ? TypeSymbol.ArrayOf(type) : type;
        }

        /// <summary>命名类型解析：泛型定义（<c>名称`元数</c>）/ 实例化（<c>名称`元数#实参$实参</c>）或普通声明类型。</summary>
        private TypeSymbol? ResolveNamedTypeByMetadataName(string elementName)
        {
            var backtick = elementName.IndexOf('`');
            if (backtick >= 0)
            {
                var baseName = elementName.Substring(0, backtick);
                var rest = elementName.Substring(backtick + 1);
                var hash = rest.IndexOf('#');
                var arityText = hash < 0 ? rest : rest.Substring(0, hash);
                if (int.TryParse(arityText, out var arity) && arity > 0)
                {
                    if (ResolveDeclaredType(baseName) is NamedTypeSymbol definition &&
                        definition.IsGenericDefinition && definition.TypeParameters.Length == arity)
                    {
                        if (hash < 0)
                        {
                            return definition;
                        }

                        var argsText = rest.Substring(hash + 1);
                        if (argsText.Length == 0)
                        {
                            return null;
                        }

                        var argumentNames = argsText.Split('$');
                        var arguments = ImmutableArray.CreateBuilder<TypeSymbol>(argumentNames.Length);
                        foreach (var argumentName in argumentNames)
                        {
                            if (GetTypeByMetadataName(argumentName) is not { } argumentType)
                            {
                                return null;
                            }

                            arguments.Add(argumentType);
                        }

                        return GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
                    }
                }

                return null;
            }

            return ResolveDeclaredType(elementName);
        }

        /// <summary>经全局命名空间树按「命名空间.简单名」定位声明类型。</summary>
        private TypeSymbol? ResolveDeclaredType(string elementName)
        {
            return GlobalNamespace.TryGetType(elementName);
        }

        private NamespaceSymbol? _globalNamespace;

        /// <summary>全局命名空间根（对齐 Roslyn <c>Compilation.GlobalNamespace</c>）：包含子命名空间与
        /// 全部已声明的命名类型（源 + 注入的 .coa 库；按符号的 <see cref="NamedTypeSymbol.Namespace"/> 归组）。</summary>
        public NamespaceSymbol GlobalNamespace
        {
            get
            {
                var global = _globalNamespace;
                if (global != null)
                {
                    return global;
                }

                var tree = NamespaceSymbol.CreateGlobal();
                AddTypesToNamespace(tree, GlobalScope.Enums);
                AddTypesToNamespace(tree, GlobalScope.Classes);
                AddTypesToNamespace(tree, _codLibraries.SelectMany(l => l.Enums));
                AddTypesToNamespace(tree, _codLibraries.SelectMany(l => l.Classes));
                AddTypesToNamespace(tree, _codLibraries.SelectMany(l => l.GenericDefinitions));
                AddFunctionsToNamespace(tree, GlobalScope.Functions.Where(f => f.ContainingClass == null));
                AddFunctionsToNamespace(tree, _codLibraries.SelectMany(l => l.Functions).Where(f => f.ContainingClass == null));
                Interlocked.CompareExchange(ref _globalNamespace, tree, null);
                // CAS 后重读（与 SourceAssembly 同模式）
                return _globalNamespace!;
            }
        }

        /// <summary>按点分全名解析命名空间符号（全局根取 ""；未命中返回 null）。</summary>
        public NamespaceSymbol? GetNamespace(string fullName)
        {
            return GlobalNamespace.GetNamespace(fullName ?? "");
        }

        private static void AddTypesToNamespace(NamespaceSymbol root, IEnumerable<NamedTypeSymbol> types)
        {
            foreach (var type in types)
            {
                var ns = NamespaceSymbol.GetOrCreateNamespace(root, type.Namespace);
                ns.AddTypeMember(type);
            }
        }

        private static void AddFunctionsToNamespace(NamespaceSymbol root, IEnumerable<FunctionSymbol> functions)
        {
            foreach (var function in functions)
            {
                var ns = NamespaceSymbol.GetOrCreateNamespace(root, function.Namespace);
                ns.AddFunctionMember(function);
            }
        }

        private ImmutableArray<string> CollectNamespaceNames()
        {
            var names = new List<string>();
            foreach (var tree in SyntaxTrees)
            {
                names.AddRange(tree.GetDeclaredNamespaceNames());
            }

            return names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        }

        private AssemblySymbol? _sourceAssembly;

        /// <summary>本编译的源程序集（对齐 Roslyn <c>Compilation.SourceAssembly</c>）。</summary>
        public AssemblySymbol SourceAssembly
        {
            get
            {
                var source = _sourceAssembly;
                if (source == null)
                {
                    source = new AssemblySymbol("Cocoa", isSource: true);
                    Interlocked.CompareExchange(ref _sourceAssembly, source, null);
                    source = _sourceAssembly;
                }

                return source;
            }
        }

        private ImmutableArray<AssemblySymbol> _referencedAssemblies;

        /// <summary>引用的元数据程序集（对齐 Roslyn <c>Compilation.References</c>）：程序集路径引用 + 已加载的 `.coa` 库；
        /// <see cref="AssemblySymbol.Display"/> 携带路径，供 Emit 解析 BCL/引用。</summary>
        public ImmutableArray<AssemblySymbol> ReferencedAssemblies
        {
            get
            {
                if (_referencedAssemblies.IsDefault && (_references.Length > 0 || _codLibraries.Length > 0))
                {
                    var builder = ImmutableArray.CreateBuilder<AssemblySymbol>(_references.Length + _codLibraries.Length);
                    foreach (var path in _references)
                    {
                        builder.Add(new AssemblySymbol(Path.GetFileNameWithoutExtension(path), isSource: false, display: path));
                    }

                    foreach (var library in _codLibraries)
                    {
                        var name = string.IsNullOrEmpty(library.Name)
                            ? Path.GetFileNameWithoutExtension(library.SourcePath ?? "reference")
                            : library.Name;
                        builder.Add(new AssemblySymbol(name, isSource: false, display: library.SourcePath));
                    }

                    ImmutableInterlocked.InterlockedInitialize(ref _referencedAssemblies, builder.MoveToImmutable());
                }

                return _referencedAssemblies.IsDefault ? ImmutableArray<AssemblySymbol>.Empty : _referencedAssemblies;
            }
        }

        /// <summary>校验 `.coa` 库的 `requires` 与消费方后端匹配。</summary>
        public ImmutableArray<Diagnostic> ValidateCodBackendRequirements(bool isNative)
        {
            if (!isNative || _codLibraries.IsDefaultOrEmpty)
            {
                return ImmutableArray<Diagnostic>.Empty;
            }

            foreach (var library in _codLibraries)
            {
                if (library.Requires == CoaRequirement.DotNet)
                {
                    var ns = library.Namespaces.Length > 0 ? library.Namespaces[0] : "library";
                    return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, $"库 '{ns}' requires dotnet（含 .NET API/OOP），native 后端不支持（阶段 9 CLR Hosting 前）"));
                }
            }

            return ImmutableArray<Diagnostic>.Empty;
        }
    }

}
