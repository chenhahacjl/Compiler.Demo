using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binder;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.Text;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>M7 解释器调试会话：实现 <see cref="IExecutionHook"/> 经语句边界钩子实现断点/单步。
    /// 3.5 解耦：仅经 <see cref="IExecutionHost"/>（求值器只读视图）与 <see cref="ExecutionSnapshot"/> 访问状态，
    /// 不引用 <see cref="Evaluator"/> 任何 internal 成员。</summary>
    public sealed class DebuggerSession : IExecutionHook
    {
        private readonly IExecutionHost _evaluator;
        private readonly string[]? _args;
        private readonly HashSet<BreakpointKey> _breakpoints = new();

        private readonly ManualResetEventSlim _resume = new(false);
        private readonly ManualResetEventSlim _pausedSignal = new(false);
        private readonly ManualResetEventSlim _exitSignal = new(false);

        private volatile DebugExecutionState _state = DebugExecutionState.NotStarted;
        private volatile DebugPauseReason _pauseReason = DebugPauseReason.Entry;
        private volatile bool _stopRequested;
        private volatile bool _breakAtEntry;

        private StepMode _stepMode = StepMode.None;
        private int _stepStartDepth;
        private (string File, int Line)? _lastPause;

        private enum StepMode { None, Over, Into, Out }

        private readonly record struct BreakpointKey(string File, int Line);

        private DebuggerSession(BoundProgram program, string[]? args)
        {
            _evaluator = new Evaluator(program, new Dictionary<VariableSymbol, object>());
            _args = args;
            _evaluator.ExecutionHook = this;
        }

        /// <summary>用已绑定的编译创建调试会话（编译错误时抛出）。</summary>
        public static DebuggerSession Create(Compilation compilation, string[]? args = null)
        {
            var program = compilation.GetProgram();
            if (program.Diagnostics.HasErrors())
                throw new InvalidOperationException("编译存在错误，无法启动调试");
            return new DebuggerSession(program, args);
        }

        public DebugExecutionState State => _state;
        public DebugPauseReason PauseReason => _pauseReason;
        public object? ReturnValue { get; private set; }
        public Exception? UnhandledException { get; private set; }

        /// <summary>断点命中 / 单步完成 / 异常暂停时触发（求值线程）。</summary>
        public event Action<DebugPauseReason>? Paused;

        /// <summary>会话结束（完成/停止/异常）时触发。</summary>
        public event Action? Exited;

        public void SetBreakpoint(string filePath, int line)
        {
            lock (_breakpoints) _breakpoints.Add(new BreakpointKey(Normalize(filePath), line));
        }

        public void RemoveBreakpoint(string filePath, int line)
        {
            lock (_breakpoints) _breakpoints.Remove(new BreakpointKey(Normalize(filePath), line));
        }

        public void ClearBreakpoints()
        {
            lock (_breakpoints) _breakpoints.Clear();
        }

        /// <summary>是否在首个语句处暂停（从起点开始单步时使用）。</summary>
        public bool BreakAtEntry { get; set; }

        /// <summary>在后台线程开始执行。</summary>
        public void Start()
        {
            if (_state is DebugExecutionState.Running or DebugExecutionState.Paused)
                throw new InvalidOperationException("调试会话已在运行");

            _state = DebugExecutionState.Running;
            _stopRequested = false;
            _breakAtEntry = BreakAtEntry;
            System.Threading.Tasks.Task.Run(RunEvaluator);
        }

        public void Continue()
        {
            _stepMode = StepMode.None;
            Resume();
        }

        public void StepOver()
        {
            _stepMode = StepMode.Over;
            _stepStartDepth = _evaluator.BuildSnapshot().FrameCount;
            Resume();
        }

        public void StepInto()
        {
            _stepMode = StepMode.Into;
            Resume();
        }

        public void StepOut()
        {
            _stepMode = StepMode.Out;
            _stepStartDepth = _evaluator.BuildSnapshot().FrameCount;
            Resume();
        }

        /// <summary>请求停止（退出求值）。</summary>
        public void Stop()
        {
            _stopRequested = true;
            _resume.Set();
        }

        /// <summary>当前调用栈（顶帧在前）。</summary>
        public IReadOnlyList<StackFrame> CallStack
        {
            get
            {
                var frames = _evaluator.BuildSnapshot().CallStack;
                var list = new List<StackFrame>(frames.Count);
                for (var i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    if (i == 0 && _lastPause is { } last)
                    {
                        list.Add(new StackFrame(frame.Function, last.File, last.Line, frame.Locals));
                    }
                    else
                    {
                        list.Add(frame);
                    }
                }

                return list;
            }
        }

        /// <summary>当前（顶）帧的局部变量。</summary>
        public IReadOnlyList<LocalVariable>? CurrentLocals
        {
            get
            {
                var snapshot = _evaluator.BuildSnapshot();
                return snapshot.CallStack.Count > 0 ? snapshot.TopLocals : null;
            }
        }

        /// <summary>等待暂停（测试/宿主同步用）。</summary>
        public bool WaitForPause(TimeSpan timeout) => _pausedSignal.Wait(timeout);

        /// <summary>等待会话结束。</summary>
        public bool WaitForExit(TimeSpan timeout) => _exitSignal.Wait(timeout);

        private void Resume()
        {
            if (_state != DebugExecutionState.Paused) return;
            _pausedSignal.Reset();
            _resume.Set();
        }

        private void RunEvaluator()
        {
            try
            {
                ReturnValue = _args == null ? _evaluator.Evaluate(null) : _evaluator.Evaluate(_args);
            }
            catch (DebuggerStopException)
            {
                // 用户停止
            }
            catch (Exception ex)
            {
                UnhandledException = ex;
            }
            finally
            {
                _state = DebugExecutionState.Completed;
                _exitSignal.Set();
                Exited?.Invoke();
            }
        }

        /// <summary>IExecutionHook（3.5）：函数进入（当前实现无额外动作，供未来探查/采样）。</summary>
        public void OnFunctionEnter(FunctionSymbol function)
        {
        }

        /// <summary>IExecutionHook（3.5）：语句边界——断点/单步裁决。</summary>
        public void OnStatementBoundary(BoundStatement statement, TextLocation? location, bool isSequencePoint, int frameCount, Func<ExecutionSnapshot> snapshotFactory)
        {
            if (_stopRequested)
                throw new DebuggerStopException();

            // 仅在序列点（真实语句边界）处暂停，避免逐条合成 IR
            if (!isSequencePoint || location is not { Text: not null } loc) return;

            var file = loc.FileName;
            var line = loc.StartLine + 1;

            var shouldPause = false;
            var reason = DebugPauseReason.Paused;

            if (_breakAtEntry)
            {
                _breakAtEntry = false;
                shouldPause = true;
                reason = DebugPauseReason.Entry;
            }

            if (!shouldPause)
            {
                lock (_breakpoints)
                {
                    if (_breakpoints.Contains(new BreakpointKey(Normalize(file), line)))
                    {
                        shouldPause = true;
                        reason = DebugPauseReason.Breakpoint;
                    }
                }
            }

            if (!shouldPause)
            {
                var depth = frameCount;
                switch (_stepMode)
                {
                    case StepMode.Into when !IsSameAsLastPause(file, line):
                        shouldPause = true;
                        reason = DebugPauseReason.Step;
                        break;
                    case StepMode.Over when depth <= _stepStartDepth && !IsSameAsLastPause(file, line):
                        shouldPause = true;
                        reason = DebugPauseReason.Step;
                        break;
                    case StepMode.Out when depth < _stepStartDepth:
                        shouldPause = true;
                        reason = DebugPauseReason.Step;
                        break;
                }
            }

            if (!shouldPause) return;

            Pause(reason, file, line);
        }

        /// <summary>IExecutionHook（3.5）：函数退出（当前实现无额外动作）。</summary>
        public void OnFunctionExit(FunctionSymbol function)
        {
        }

        private void Pause(DebugPauseReason reason, string file, int line)
        {
            _stepMode = StepMode.None;
            _pauseReason = reason;
            _lastPause = (file, line);
            _state = DebugExecutionState.Paused;

            _resume.Reset();
            _pausedSignal.Set();
            Paused?.Invoke(reason);

            _resume.Wait();

            if (_stopRequested)
                throw new DebuggerStopException();

            _state = DebugExecutionState.Running;
        }

        private bool IsSameAsLastPause(string file, int line)
            => _lastPause is { } last
               && last.Line == line
               && string.Equals(last.File, file, StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string path) => path.Replace('\\', '/');

        private sealed class DebuggerStopException : Exception
        {
        }
    }
}