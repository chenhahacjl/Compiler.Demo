using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Emit;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    public class Compilation
    {
        private BoundGlobalScope? _globalScope;
        private readonly string _entryPointName;
        private readonly string[] _references;
        private readonly ImmutableArray<CodProgram> _codLibraries;

        /// <summary>动态链接（阶段 A2）：dotnet 后端消费 `.cod` 时不内联库体，发射外部 Ref 指向各库 dll。</summary>
        private readonly bool _linkCodDynamically;

        private Compilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically = false, params SyntaxTree[] syntaxTrees)
        {
            IsScript = isScript;
            Previous = previous;
            _entryPointName = entryPointName;
            _linkCodDynamically = linkCodDynamically;
            _references = (references ?? Array.Empty<string>())
                .Where(r => !r.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _codLibraries = LoadCodLibraries(references);
            SyntaxTrees = syntaxTrees.ToImmutableArray();
        }

        private static ImmutableArray<CodProgram> LoadCodLibraries(string[]? references)
        {
            var builder = ImmutableArray.CreateBuilder<CodProgram>();

            // 内建系统库（System.Core.cod 等，目录发现 `System*.cod`）先行：用户引用可覆盖/补充同名符号
            builder.AddRange(SystemLibrary.Load());

            if (references != null)
            {
                foreach (var reference in references)
                {
                    if (reference.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                    {
                        var library = CodSerializer.Load(reference);
                        library.Name = Cod.CodAssemblyNaming.ManagedAssemblyName(Path.GetFileNameWithoutExtension(reference));
                        library.SourcePath = Path.GetFullPath(reference);
                        builder.Add(library);
                    }
                }
            }

            return builder.ToImmutable();
        }

        public static Compilation Create(params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName: "Main", references: null, syntaxTrees: syntaxTrees);
        }

        public static Compilation Create(string[] references, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName: "Main", references, syntaxTrees: syntaxTrees);
        }

        /// <summary>动态链接变体（阶段 A2）：dotnet 后端消费 `.cod` 时不内联，运行期依赖各库 dll。</summary>
        public static Compilation Create(string[] references, bool linkCodDynamically, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName: "Main", references, linkCodDynamically, syntaxTrees);
        }

        public static Compilation Create(string entryPointName, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName, references: null, syntaxTrees: syntaxTrees);
        }

        public static Compilation Create(string entryPointName, string[] references, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName, references, syntaxTrees: syntaxTrees);
        }

        /// <summary>动态链接变体（阶段 A2）：带入口名的 dotnet 消费方，`.cod` 库以外部 dll 依赖接入。</summary>
        public static Compilation Create(string entryPointName, string[] references, bool linkCodDynamically, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName, references, linkCodDynamically, syntaxTrees);
        }

        public static Compilation CreateScript(Compilation? previous, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: true, previous, entryPointName: "Main", references: null, syntaxTrees: syntaxTrees);
        }

        public bool IsScript { get; }
        public Compilation? Previous { get; }
        public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
        public FunctionSymbol? MainFunction => GlobalScope.MainFunction;
        public ImmutableArray<FunctionSymbol> Functions => GlobalScope.Functions;
        public ImmutableArray<VariableSymbol> Variables => GlobalScope.Variables;

        /// <summary>已加载的 `.cod` 库（含系统库；动态链接 CopyLocal 依据）。</summary>
        internal ImmutableArray<CodProgram> CodLibraries => _codLibraries;

        internal BoundGlobalScope GlobalScope
        {
            get
            {
                if (_globalScope == null)
                {
                    var globalScope = Binding.Binder.BindGlobalScope(IsScript, Previous?.GlobalScope, SyntaxTrees, _entryPointName, _references, _codLibraries);
                    Interlocked.CompareExchange(ref _globalScope, globalScope, null);
                }

                return _globalScope;
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

        /// <summary>按元数据全名解析类型（对齐 Roslyn <c>CSharpCompilation.GetTypeByMetadataName</c>）。
        /// 内建特殊类型（基元/Object/Type/String/Void）优先，其次全局作用域声明（源 + 注入的 .cod 库）类与枚举；
        /// 缺失返回 null。支持后置 [] 数组全名（如 <c>"System.Int32[]"</c>）。</summary>
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
                // 声明的命名类型（源 + 注入的 .cod 库）：经全局命名空间树归组解析（Phase 1-5）
                var dotIndex = elementName.LastIndexOf('.');
                var namespaceName = dotIndex < 0 ? "" : elementName.Substring(0, dotIndex);
                var simpleName = dotIndex < 0 ? elementName : elementName.Substring(dotIndex + 1);
                var ns = GlobalNamespace.GetNamespace(namespaceName);
                if (ns != null)
                {
                    foreach (var member in ns.GetTypeMembers())
                    {
                        if (member.Name == simpleName)
                        {
                            type = member;
                            break;
                        }
                    }
                }
            }

            return isArray && type != null ? TypeSymbol.ArrayOf(type) : type;
        }

        private NamespaceSymbol? _globalNamespace;

        /// <summary>全局命名空间根（对齐 Roslyn <c>Compilation.GlobalNamespace</c>）：包含子命名空间与
        /// 全部已声明的命名类型（源 + 注入的 .cod 库；按符号的 <see cref="NamedTypeSymbol.Namespace"/> 归组）。</summary>
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
                Interlocked.CompareExchange(ref _globalNamespace, tree, null);
                return _globalNamespace;
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

        private ImmutableArray<AssemblySymbol> _referencedAssemblies = ImmutableArray<AssemblySymbol>.Empty;

        /// <summary>引用的元数据程序集（对齐 Roslyn <c>Compilation.References</c>；本阶段即已加载的 `.cod` 库）。</summary>
        public ImmutableArray<AssemblySymbol> ReferencedAssemblies
        {
            get
            {
                if (_referencedAssemblies.Length == 0 && _codLibraries.Length > 0)
                {
                    var builder = ImmutableArray.CreateBuilder<AssemblySymbol>(_codLibraries.Length);
                    foreach (var library in _codLibraries)
                    {
                        var name = string.IsNullOrEmpty(library.Name)
                            ? Path.GetFileNameWithoutExtension(library.SourcePath ?? "reference")
                            : library.Name;
                        builder.Add(new AssemblySymbol(name, isSource: false));
                    }

                    ImmutableInterlocked.InterlockedInitialize(ref _referencedAssemblies, builder.MoveToImmutable());
                }

                return _referencedAssemblies;
            }
        }

        private BoundProgram GetProgram()
        {
            var previous = Previous == null ? null : Previous.GetProgram();

            return Binding.Binder.BindProgram(IsScript, previous, GlobalScope, _codLibraries, SyntaxTrees.IsDefaultOrEmpty ? LanguageDialect.Cocoa : SyntaxTrees[0].Dialect, _linkCodDynamically);
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

            var evaluator = new Evaluator(program, variables);

            var value = evaluator.Evaluate();

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

            var evaluator = new Evaluator(program, variables);

            var value = evaluator.Evaluate(args);

            return new EvaluationResult(program.Diagnostics, value);
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

        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary)
        {
            var parseDiagnostics = SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = GetProgram();

            // 6e-M22 C4-b：IL 后端已支持函数值（Func`N 委托映射），门禁移除；native 见 EmitNative

            var ilReferences = references
                .Where(r => !r.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var backendDiagnostics = IlEmitter.Emit(program, moduleName, ilReferences, outputPath, target, emitLibrary);

            // 成功路径也带上 GlobalScope 警告（using 未解析等），供 CLI 打印
            return diagnostics.Concat(backendDiagnostics).ToImmutableArray();
        }

        /// <summary>
        /// 把程序直接生成为原生可执行文件，不依赖 .NET 运行时。
        /// </summary>
        internal ImmutableArray<Diagnostic> EmitNative(string moduleName, string outputPath, TargetPlatform platform = default)
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

            // 6e-M22 C4-c 已落地：native 函数值发射（[typeId][fnptr][env] 三字对象 + CallReg）——门禁移除

            if (program.MainFunction == null)
            {
                var location = new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                return ImmutableArray.Create(Diagnostic.Error(location, "native code generation requires a main function"));
            }

            // 6e-M19 M4：native 对象模型——用户类（字段/方法/继承/多态/vtable 虚分派）全面放行。
            // 仍拒绝：接口声明（接口分派未实现）、含初始化器的静态构造（无 .cctor 触发时机）。
            if (program.Classes.Length > 0)
            {
                var interfaceClass = program.Classes.FirstOrDefault(c => c.IsInterface);
                if (interfaceClass != null)
                {
                    var location = interfaceClass.Declaration?.Identifier.Location
                                   ?? new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                    return ImmutableArray.Create(Diagnostic.Error(location, $"interface '{interfaceClass.Name}' 暂不支持 native 后端（接口分派随后续里程碑落地，见 docs-dev/对象模型设计.md）"));
                }

                var staticInitClass = program.Classes.FirstOrDefault(HasStaticInitializer);
                if (staticInitClass != null)
                {
                    var location = staticInitClass.Declaration?.Identifier.Location
                                   ?? new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                    return ImmutableArray.Create(Diagnostic.Error(location, $"class '{staticInitClass.Name}' 含静态构造函数或静态字段初始化器，native 后端暂不支持静态初始化触发（字段可声明但保持零值；请改在显式代码中赋值）"));
                }
            }

            var backendDiagnostics = ValidateCodBackendRequirements(isNative: true);
            if (backendDiagnostics.Length > 0)
            {
                return backendDiagnostics;
            }

            // M4：Object 成员面 receiver 形状校验（any/数组/枚举接收者需装箱表示，明确报错不静默错编）
            var objectFaceBag = new DiagnosticBag();
            NativeObjectModelValidator.Validate(program, objectFaceBag, new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0)));
            if (objectFaceBag.Any())
            {
                return diagnostics.Concat(objectFaceBag).ToImmutableArray();
            }

            var importWarnings = NativeImportValidator.Validate(program, platform.Arch);

            NativeCodeEmitter.Emit(program, moduleName, outputPath, platform);

            return diagnostics.Concat(importWarnings).ToImmutableArray();
        }

        /// <summary>校验 `.cod` 库的 `requires` 与消费方后端匹配。</summary>
        private ImmutableArray<Diagnostic> ValidateCodBackendRequirements(bool isNative)
        {
            if (!isNative || _codLibraries.IsDefaultOrEmpty)
            {
                return ImmutableArray<Diagnostic>.Empty;
            }

            foreach (var library in _codLibraries)
            {
                if (library.Requires == CodRequirement.DotNet)
                {
                    var ns = library.Namespaces.Length > 0 ? library.Namespaces[0] : "library";
                    return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, $"库 '{ns}' requires dotnet（含 .NET API/OOP），native 后端不支持（阶段 9 CLR Hosting 前）"));
                }
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        /// <summary>
        /// 纯容器类判定（6e-M17，.cod 库放行判据）：类只含 syscall/静态 extern 方法，
        /// 无实例字段/实例构造/属性/显式基类/实例方法。等价"编译期透明的互操作分组"，
        /// 不涉对象模型。
        /// </summary>
        private static bool IsPureContainerClass(NamedTypeSymbol classType)
        {
            if (classType.IsInterface || (classType.BaseType != null && !classType.BaseType.IsSystemObjectRoot) || classType.Fields.Any(f => !f.IsStatic))
            {
                return false;
            }

            if (classType.Properties.Length > 0)
            {
                return false;
            }

            foreach (var method in classType.Methods)
            {
                // 隐式默认实例构造（无声明、0 参）→ 允许（容器类不必实例化，发射端忽略）；显式实例构造 → 非容器
                if (method.IsConstructor && !method.IsStatic)
                {
                    if (method.Declaration != null || method.Parameters.Length != 0)
                    {
                        return false;
                    }

                    continue;
                }

                if (!method.IsStatic)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 类是否携带静态初始化语义（静态字段初始化器合成的 .cctor 或显式静态构造）。
        /// Binder 仅在存在静态初始化器或显式声明时创建 .cctor 符号，故符号存在即需运行期触发——
        /// native 后端无该时机，门禁拒绝并提示改写为显式赋值。
        /// </summary>
        private static bool HasStaticInitializer(NamedTypeSymbol classType)
        {
            foreach (var method in classType.Methods)
            {
                if (method.IsConstructor && method.IsStatic && method.Parameters.Length == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把库编译为 `.cod` 语义层程序集（编译到 BoundProgram 即停，不走 IR/机器码/IL）。
        /// </summary>
        internal ImmutableArray<Diagnostic> EmitCocoa(string moduleName, string outputPath)
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

            // 6e-M22：lambda/函数值节点入 `.cod` 序列化于 C6 接入——先行明确诊断
            var cocoaFunctionValueDiagnostic = FindFunctionValueDiagnostic(program);
            if (cocoaFunctionValueDiagnostic != null)
            {
                return ImmutableArray.Create(cocoaFunctionValueDiagnostic);
            }

            // 校验 1：库无入口
            if (program.MainFunction != null || program.ScriptFunction != null)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 的库不允许入口函数（Main/script）"));
            }

            // 校验 2：无内部 OOP（.cod 6e-M17 起放行纯容器类：仅 syscall/extern 静态方法；实例类仍 6b 后置）
            if (program.Classes.Length > 0)
            {
                var offendingClass = program.Classes.FirstOrDefault(c => !IsPureContainerClass(c));
                if (offendingClass != null)
                {
                    var location = offendingClass.Declaration?.Identifier.Location ?? ZeroLocation;
                    return ImmutableArray.Create(Diagnostic.Error(location, $"库含实例类 '{offendingClass.Name}'（OOP），.cod 序列化阶段 6b 后置（requires:dotnet）；纯 syscall/extern 容器类已支持"));
                }
            }

            // 校验 3：库体不含 OOP/.NET API 节点（类字段/方法/对象创建/this/base/静态类型等）
            foreach (var (fn, body) in program.Functions)
            {
                if (HasOopNode(body))
                {
                    return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, $"库函数 '{fn.Name}' 含 class/OOP 或 .NET API 调用，.cod 阶段 6b 后置（requires:dotnet）"));
                }
            }

            // 校验 4：必须声明 namespace
            var namespaces = CollectNamespaceNames();
            if (namespaces.Length == 0)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 库必须声明 namespace（如 `namespace MyLib { ... }`）"));
            }

            var functions = GlobalScope.Functions;
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

            var containerClasses = program.Classes.Where(IsPureContainerClass).ToImmutableArray();

            var codProgram = new CodProgram(
                functions,
                globals,
                enums,
                containerClasses,
                program.Functions,
                CodRequirement.Any,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                imports,
                ImmutableArray<string>.Empty,
                namespaces,
                program.GenericDefinitions,
                program.GenericOpenBodies)
            {
                // 程序集名 = 模块名：动态链接时消费方据此合成 AssemblyRef 指向同名 dll（阶段 A2）
                Name = moduleName,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            using (var writer = new StreamWriter(outputPath))
            {
                CodSerializer.Write(writer, codProgram);
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        private TextLocation ZeroLocation
        {
            get
            {
                if (SyntaxTrees.Length > 0)
                {
                    return new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                }

                return new TextLocation(null, new TextSpan(0, 0));
            }
        }

        private ImmutableArray<string> CollectNamespaceNames()
        {
            var names = new List<string>();
            foreach (var tree in SyntaxTrees)
            {
                CollectNamespaceNames(tree.Root.Members, names);
            }

            return names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        }

        private static void CollectNamespaceNames(ImmutableArray<MemberSyntax> members, List<string> names)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDeclarationSyntax ns)
                {
                    names.Add(ns.Name);
                    CollectNamespaceNames(ns.Members, names);
                }
            }
        }

        /// <summary>函数值/函数类型签名扫描（6e-M22 C4）：发射器接入前的统一门禁。</summary>
        private Diagnostic? FindFunctionValueDiagnostic(BoundProgram program)
        {
            foreach (var (function, body) in program.Functions)
            {
                if (HasFunctionValueNode(body))
                {
                    var location = function.Syntax?.Location ?? ZeroLocation;
                    return Diagnostic.Error(location, "lambda/函数值的三后端发射将于 6e-M22 C4-b（IL）/C4-c（native）逐步接入。");
                }

                foreach (var parameter in function.Parameters)
                {
                    if (parameter.Type is FunctionTypeSymbol)
                    {
                        var location = function.Syntax?.Location ?? ZeroLocation;
                        return Diagnostic.Error(location, $"函数 '{function.Name}' 的参数 '{parameter.Name}' 为函数类型，发射将于 6e-M22 C4-b/C4-c 接入。");
                    }
                }

                if (function.ReturnType is FunctionTypeSymbol)
                {
                    var location = function.Syntax?.Location ?? ZeroLocation;
                    return Diagnostic.Error(location, $"函数 '{function.Name}' 返回函数类型，发射将于 6e-M22 C4-b/C4-c 接入。");
                }
            }

            foreach (var classType in program.Classes)
            {
                foreach (var field in classType.Fields)
                {
                    if (field.Type is FunctionTypeSymbol)
                    {
                        var location = classType.Declaration?.Identifier.Location ?? ZeroLocation;
                        return Diagnostic.Error(location, $"类 '{classType.Name}' 的字段 '{field.Name}' 为函数类型，发射将于 6e-M22 C4-b/C4-c 接入。");
                    }
                }
            }

            return null;
        }

        private static bool HasFunctionValueNode(BoundNode node)
        {
            if (node.Kind == BoundNodeKind.FunctionValueExpression || node.Kind == BoundNodeKind.InvocationExpression)
            {
                return true;
            }

            foreach (var child in BoundChildren(node))
            {
                if (HasFunctionValueNode(child))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>库体是否含 OOP/.NET API 节点（v1 拒绝：序列化阶段 6b 后置）。</summary>
        private static bool HasOopNode(BoundNode node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.ObjectCreationExpression:
                case BoundNodeKind.ThisExpression:
                case BoundNodeKind.BaseExpression:
                case BoundNodeKind.ConstructorChainExpression:
                case BoundNodeKind.MemberAssignmentExpression:
                case BoundNodeKind.ErrorExpression:
                    return true;
                case BoundNodeKind.StaticTypeExpression:
                    // 容器类静态类型表达式（System.Runtime.Print 的目标）不是 OOP
                    return false;
                case BoundNodeKind.MemberAccessExpression:
                    return ((BoundMemberAccessExpression)node).Field != null;
                case BoundNodeKind.MemberCallExpression:
                    {
                        var call = (BoundMemberCallExpression)node;
                        // 静态容器类方法调用（syscall/extern/带体静态方法，6e-M18）不是 OOP；实例方法/继承仍是
                        return call.IsBase || (call.Method != null && !call.Method.IsStatic);
                    }
                default:
                    foreach (var child in BoundChildren(node))
                    {
                        if (HasOopNode(child))
                        {
                            return true;
                        }
                    }

                    return false;
            }
        }

        internal static IEnumerable<BoundNode> BoundChildren(BoundNode node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    return ((BoundBlockStatement)node).Statements;
                case BoundNodeKind.VariableDeclaration:
                    return new[] { ((BoundVariableDeclaration)node).Initializer };
                case BoundNodeKind.IfStatement:
                    {
                        var n = (BoundIfStatement)node;
                        return n.ElseStatement == null
                            ? new BoundNode[] { n.Condition, n.ThenStatement }
                            : new BoundNode[] { n.Condition, n.ThenStatement, n.ElseStatement };
                    }
                case BoundNodeKind.WhileStatement:
                    {
                        var n = (BoundWhileStatement)node;
                        return new BoundNode[] { n.Condition, n.Body };
                    }
                case BoundNodeKind.DoWhileStatement:
                    {
                        var n = (BoundDoWhileStatement)node;
                        return new BoundNode[] { n.Body, n.Condition };
                    }
                case BoundNodeKind.ForStatement:
                    {
                        var n = (BoundForStatement)node;
                        return n.Step == null
                            ? new BoundNode[] { n.LowerBound, n.UpperBound, n.Body }
                            : new BoundNode[] { n.LowerBound, n.UpperBound, n.Step, n.Body };
                    }
                case BoundNodeKind.ConditionalGotoStatement:
                    return new[] { ((BoundConditionalGotoStatement)node).Condition };
                case BoundNodeKind.ReturnStatement:
                    {
                        var n = (BoundReturnStatement)node;
                        return n.Expression == null ? Array.Empty<BoundNode>() : new[] { n.Expression };
                    }
                case BoundNodeKind.ExpressionStatement:
                    return new[] { ((BoundExpressionStatement)node).Expression };
                case BoundNodeKind.SequencePointStatement:
                    return new[] { ((BoundSequencePointStatement)node).Statement };
                case BoundNodeKind.LiteralExpression:
                    return Array.Empty<BoundNode>();
                case BoundNodeKind.VariableExpression:
                    return Array.Empty<BoundNode>();
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)node;
                        return new[] { n.Expression };
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)node;
                        return new[] { n.Expression };
                    }
                case BoundNodeKind.UnaryExpression:
                    return new[] { ((BoundUnaryExpression)node).Operand };
                case BoundNodeKind.BinaryExpression:
                    {
                        var n = (BoundBinaryExpression)node;
                        return new BoundNode[] { n.Left, n.Right };
                    }
                case BoundNodeKind.ConditionalExpression:
                    {
                        var n = (BoundConditionalExpression)node;
                        return new BoundNode[] { n.Condition, n.WhenTrue, n.WhenFalse };
                    }
                case BoundNodeKind.CallExpression:
                    return ((BoundCallExpression)node).Arguments;
                case BoundNodeKind.ConversionExpression:
                    return new[] { ((BoundConversionExpression)node).Expression };
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)node;
                        return new BoundNode[] { n.Length }.Concat(n.Initializers);
                    }
                case BoundNodeKind.ElementAccessExpression:
                    {
                        var n = (BoundElementAccessExpression)node;
                        return new BoundNode[] { n.Target, n.Index };
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)node;
                        return new BoundNode[] { n.Target, n.Expression };
                    }
                case BoundNodeKind.MemberAccessExpression:
                    return new[] { ((BoundMemberAccessExpression)node).Target };
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)node;
                        return new BoundNode[] { n.Expression }.Concat(n.Arguments);
                    }
                case BoundNodeKind.MemberAssignmentExpression:
                    return new[] { ((BoundMemberAssignmentExpression)node).Expression };
                case BoundNodeKind.FormatExpression:
                    return new[] { ((BoundFormatExpression)node).Value };
                case BoundNodeKind.IsExpression:
                    return new[] { ((BoundIsExpression)node).Expression };
                case BoundNodeKind.AsExpression:
                    return new[] { ((BoundAsExpression)node).Expression };
                case BoundNodeKind.StaticTypeExpression:
                    return Array.Empty<BoundNode>();
                case BoundNodeKind.FunctionValueExpression:
                    {
                        var n = (BoundFunctionValueExpression)node;
                        return n.Receiver == null
                            ? Array.Empty<BoundNode>()
                            : new[] { n.Receiver };
                    }
                case BoundNodeKind.InvocationExpression:
                    {
                        var n = (BoundInvocationExpression)node;
                        return new BoundNode[] { n.Callee }.Concat(n.Arguments);
                    }
                default:
                    return Array.Empty<BoundNode>();
            }
        }
    }
}
