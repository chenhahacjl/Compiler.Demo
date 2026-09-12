using Cocoa.CodeAnalysis.Binding;
using Binding = Cocoa.CodeAnalysis.Binding;
using Symbols = Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
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

        // 6e-M19 M3-c：OOP 运行时状态——实例字段布局缓存 / 静态字段槽 / .cctor 已初始化集 / this 接收者栈
        private readonly Dictionary<NamedTypeSymbol, ImmutableArray<FieldSymbol>> _instanceFields = new Dictionary<NamedTypeSymbol, ImmutableArray<FieldSymbol>>();
        private readonly Dictionary<FieldSymbol, object> _staticFields = new Dictionary<FieldSymbol, object>();
        private readonly HashSet<NamedTypeSymbol> _staticsInitialized = new HashSet<NamedTypeSymbol>();
        private readonly Stack<object> _thisStack = new Stack<object>();

        private object? _lastValue;
        private bool _returned;

        // 6e-M23 R5：byref 实参回写队列（LIFO——调用退出时回写到基线标记）
        private readonly List<Action> _byRefWriteBacks = new List<Action>();

        // 6e-M23 R5：当前调用实参物化的别名去重作用域（同一存储共享 Box，三后端别名语义一致）
        private Dictionary<object, ByRefBox> _byRefSlotScope = new Dictionary<object, ByRefBox>();

        // yield 迭代器收集器
        private List<object?>? _yieldedValues;

        // M7 调试器：调用帧栈（顶在最前）+ 语句边界钩子（调试会话注入；生产路径为 null）
        internal readonly Stack<DebugFrame> _frames = new Stack<DebugFrame>();
        internal Action<BoundStatement, TextLocation?, bool>? StatementBoundaryHook;

        /// <summary>调用帧（顶帧在前）。</summary>
        internal IReadOnlyList<DebugFrame> Frames => _frames.ToArray();

        /// <summary>最近一次表达式求值结果（调试器读取返回值用）。</summary>
        internal object? LastValue => _lastValue;

        /// <summary>全局变量槽（脚本顶层变量；调试器读取局部变量时合并）。</summary>
        internal IReadOnlyDictionary<VariableSymbol, object> Globals => _globals;

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
                _frames.Push(new DebugFrame(function, _locals.Peek()));
                try
                {
                    return EvaluateStatement(body);
                }
                finally
                {
                    _frames.Pop();
                }
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

    internal sealed class YieldBreakException : Exception
    {
    }
}
