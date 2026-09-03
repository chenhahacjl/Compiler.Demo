using Cocoa.CodeAnalysis.Binding;
using Binding = Cocoa.CodeAnalysis.Binding;
using Symbols = Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Cocoa.CodeGen.Interpreter
{
    // TODO: Get rid of evaluator in favor of IlEmitter
    /// <summary>
    /// 求值器
    /// </summary>
    internal sealed partial class Evaluator
    {
        private readonly BoundProgram _program;
        private readonly Dictionary<VariableSymbol, object> _globals;
        private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _functions = new Dictionary<FunctionSymbol, BoundBlockStatement>();
        private readonly Stack<Dictionary<VariableSymbol, object>> _locals = new Stack<Dictionary<VariableSymbol, object>>();

        // 6e-M19 M3-c锛歄OP 杩愯鏃剁姸鎬佲€斺€斿疄渚嬪瓧娈靛竷灞€缂撳瓨 / 闈欐€佸瓧娈垫Ы / .cctor 宸插垵濮嬪寲闆?/ this 鎺ユ敹鑰呮爤
        private readonly Dictionary<NamedTypeSymbol, ImmutableArray<FieldSymbol>> _instanceFields = new Dictionary<NamedTypeSymbol, ImmutableArray<FieldSymbol>>();
        private readonly Dictionary<FieldSymbol, object> _staticFields = new Dictionary<FieldSymbol, object>();
        private readonly HashSet<NamedTypeSymbol> _staticsInitialized = new HashSet<NamedTypeSymbol>();
        private readonly Stack<object> _thisStack = new Stack<object>();

        private object? _lastValue;

        // 6e-M23 R5：byref 实参回写队列（LIFO——调用退出时回写到基线标记）
        private readonly List<Action> _byRefWriteBacks = new List<Action>();

        // 6e-M23 R5：当前调用实参物化的别名去重作用域（同一存储共享 Box，三后端别名语义一致）
        private Dictionary<object, ByRefBox> _byRefSlotScope = new Dictionary<object, ByRefBox>();

        public Evaluator(BoundProgram program, Dictionary<VariableSymbol, object> variables)
        {
            _program = program;

            _globals = variables;
            _locals.Push(new Dictionary<VariableSymbol, object>());

            var current = program;

            while (current != null)
            {
                foreach (var kv in current.Functions)
                {
                    var function = kv.Key;
                    var body = kv.Value;

                    // indexer overwrite instead of Add (1a/A5): same symbol reused across
                    // two levels of the submission chain takes the latest submission
                    _functions[function] = body;
                }

                current = current.Previous;
            }
        }

        public object? Evaluate()
        {
            return Evaluate(null);
        }

        public object? Evaluate(string[]? args)
        {
            var function = _program.MainFunction ?? _program.ScriptFunction;

            if (function == null)
            {
                return null;
            }

            if (function.Parameters.Length > 0)
            {
                _locals.Peek()[function.Parameters[0]] = (args ?? Array.Empty<string>()).Cast<object>().ToArray();
            }

            // 6e-M22 C5：入口函数自身带捕获变量时（顶层 lambda 捕获入口局部——当前限定非顶层，占位防御）
            var pushedEnvironment = false;
            if (function.CapturedVariables is { Count: > 0 })
            {
                _closureEnvironments.Push(CreateEnvironment(function, null));
                pushedEnvironment = true;
            }

            try
            {
                var body = _functions[function];
                return EvaluateStatement(body);
            }
            finally
            {
                if (pushedEnvironment)
                {
                    _closureEnvironments.Pop();
                }
            }
        }

    }
}
