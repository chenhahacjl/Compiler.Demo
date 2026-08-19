using Cocoa.CodeAnalysis.Binding;
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

        private Compilation(bool isScript, Compilation? previous, string entryPointName, params SyntaxTree[] syntaxTrees)
        {
            IsScript = isScript;
            Previous = previous;
            _entryPointName = entryPointName;
            SyntaxTrees = syntaxTrees.ToImmutableArray();
        }

        public static Compilation Create(params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName: "Main", syntaxTrees);
        }

        public static Compilation Create(string entryPointName, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: false, previous: null, entryPointName, syntaxTrees);
        }

        public static Compilation CreateScript(Compilation? previous, params SyntaxTree[] syntaxTrees)
        {
            return new Compilation(isScript: true, previous, entryPointName: "Main", syntaxTrees);
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
                    var globalScope = Binding.Binder.BindGlobalScope(IsScript, Previous?.GlobalScope, SyntaxTrees, _entryPointName);
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

            return Binding.Binder.BindProgram(IsScript, previous, GlobalScope);
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
            => Emit(moduleName, references, outputPath, IlTarget.Default);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath, IlTarget target)
        {
            var parseDiagnostics = SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = GetProgram();

            return Emitter.Emit(program, moduleName, references, outputPath, target);
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

            var importWarnings = NativeImportValidator.Validate(program, platform.Arch);

            NativeCodeEmitter.Emit(program, moduleName, outputPath, platform);

            return importWarnings;
        }
    }
}
