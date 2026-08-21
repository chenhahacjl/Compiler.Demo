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

        private Compilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, params SyntaxTree[] syntaxTrees)
        {
            IsScript = isScript;
            Previous = previous;
            _entryPointName = entryPointName;
            _references = (references ?? Array.Empty<string>())
                .Where(r => !r.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _codLibraries = LoadCodLibraries(references);
            SyntaxTrees = syntaxTrees.ToImmutableArray();
        }

        private static ImmutableArray<CodProgram> LoadCodLibraries(string[]? references)
        {
            if (references == null)
            {
                return ImmutableArray<CodProgram>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<CodProgram>();
            foreach (var reference in references)
            {
                if (reference.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add(CodSerializer.Load(reference));
                }
            }

            return builder.ToImmutable();
        }

        public static Compilation Create(params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName: "Main", references: null, syntaxTrees);
        }

        public static Compilation Create(string[] references, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName: "Main", references, syntaxTrees);
        }

        public static Compilation Create(string entryPointName, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName, references: null, syntaxTrees);
        }

        public static Compilation Create(string entryPointName, string[] references, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName, references, syntaxTrees);
        }

        public static Compilation CreateScript(Compilation? previous, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: true, previous, entryPointName: "Main", references: null, syntaxTrees);
        }

        public bool IsScript { get; }
        public Compilation? Previous { get; }
        public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
        public FunctionSymbol? MainFunction => GlobalScope.MainFunction;
        public ImmutableArray<FunctionSymbol> Functions => GlobalScope.Functions;
        public ImmutableArray<VariableSymbol> Variables => GlobalScope.Variables;

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

        private BoundProgram GetProgram()
        {
            var previous = Previous == null ? null : Previous.GetProgram();

            return Binding.Binder.BindProgram(IsScript, previous, GlobalScope, _codLibraries);
        }

        /// <summary>
        /// 求值
        /// </summary>
        public EvaluationResult Evaluate(Dictionary<VariableSymbol, object> variables)
        {
            if (GlobalScope.Diagnostics.Any())
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
            if (GlobalScope.Diagnostics.Any())
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

            var ilReferences = references
                .Where(r => !r.EndsWith(".cod", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return IlEmitter.Emit(program, moduleName, ilReferences, outputPath, target, emitLibrary);
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

            if (program.MainFunction == null)
            {
                var location = new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                return ImmutableArray.Create(Diagnostic.Error(location, "native code generation requires a main function"));
            }

            if (program.Classes.Length > 0)
            {
                var location = program.Classes[0].Declaration?.Identifier.Location
                               ?? new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                return ImmutableArray.Create(Diagnostic.Error(location, "class 暂不支持 native 后端（后置，见 docs/类库设计.md）"));
            }

            var backendDiagnostics = ValidateCodBackendRequirements(isNative: true);
            if (backendDiagnostics.Length > 0)
            {
                return backendDiagnostics;
            }

            var importWarnings = NativeImportValidator.Validate(program, platform.Arch);

            NativeCodeEmitter.Emit(program, moduleName, outputPath, platform);

            return importWarnings;
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

            // 校验 1：库无入口
            if (program.MainFunction != null || program.ScriptFunction != null)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 的库不允许入口函数（Main/script）"));
            }

            // 校验 2：无内部 OOP（class 序列化阶段 6b 后置，requires:dotnet）
            if (program.Classes.Length > 0)
            {
                var location = program.Classes[0].Declaration?.Identifier.Location ?? ZeroLocation;
                return ImmutableArray.Create(Diagnostic.Error(location, "库含 class（OOP），.cod 序列化阶段 6b 后置（requires:dotnet）"));
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

            var codProgram = new CodProgram(
                functions,
                globals,
                enums,
                program.Functions,
                CodRequirement.Any,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                imports,
                ImmutableArray<string>.Empty,
                namespaces);

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

        /// <summary>库体是否含 OOP/.NET API 节点（v1 拒绝：序列化阶段 6b 后置）。</summary>
        private static bool HasOopNode(BoundNode node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.ObjectCreationExpression:
                case BoundNodeKind.ThisExpression:
                case BoundNodeKind.BaseExpression:
                case BoundNodeKind.StaticTypeExpression:
                case BoundNodeKind.ConstructorChainExpression:
                case BoundNodeKind.MemberAssignmentExpression:
                case BoundNodeKind.ErrorExpression:
                    return true;
                case BoundNodeKind.MemberAccessExpression:
                    return ((BoundMemberAccessExpression)node).Field != null;
                case BoundNodeKind.MemberCallExpression:
                    {
                        var call = (BoundMemberCallExpression)node;
                        return call.Method != null || call.IsBase;
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

        private static IEnumerable<BoundNode> BoundChildren(BoundNode node)
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
                        return new BoundNode[] { n.LowerBound, n.UpperBound, n.Body };
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
                default:
                    return Array.Empty<BoundNode>();
            }
        }
    }
}
