using Avalonia.Threading;
using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Interpreter;

namespace Cocoa.IDE.Services;

/// <summary>M7 调试器服务：管理 <see cref="DebuggerSession"/> 生命周期与断点表；
/// 求值线程的暂停/结束事件封送回 UI 线程。</summary>
public sealed class DebuggerService
{
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private DebuggerSession? _session;
    private string[]? _pendingArgs;

    /// <summary>某文件断点集变化（UI 线程）。</summary>
    public event Action<string>? BreakpointsChanged;

    /// <summary>在断点/单步处暂停（UI 线程）。</summary>
    public event Action? Paused;

    /// <summary>从暂停恢复运行（UI 线程）。</summary>
    public event Action? Resumed;

    /// <summary>会话结束（UI 线程）。</summary>
    public event Action? Exited;

    public DebugExecutionState State => _session?.State ?? DebugExecutionState.NotStarted;
    public bool IsActive => _session is { State: DebugExecutionState.Running or DebugExecutionState.Paused };
    public object? ReturnValue => _session?.ReturnValue;
    public Exception? Error => _session?.UnhandledException;

    public IReadOnlyList<StackFrame> CallStack => _session?.CallStack ?? Array.Empty<StackFrame>();
    public IReadOnlyList<LocalVariable>? Locals => _session?.CurrentLocals;

    public void SetArguments(string[]? args) => _pendingArgs = args;

    // ─── 断点表 ───

    public void ToggleBreakpoint(string filePath, int line)
    {
        if (string.IsNullOrEmpty(filePath) || line <= 0) return;

        if (!_breakpoints.TryGetValue(filePath, out var set))
        {
            set = new HashSet<int>();
            _breakpoints[filePath] = set;
        }

        if (!set.Add(line)) set.Remove(line);
        BreakpointsChanged?.Invoke(filePath);
    }

    public IReadOnlyCollection<int> GetBreakpoints(string filePath)
        => _breakpoints.TryGetValue(filePath, out var set) ? set.ToArray() : Array.Empty<int>();

    public IEnumerable<(string File, int Line)> AllBreakpoints
        => _breakpoints.SelectMany(kv => kv.Value.Select(line => (kv.Key, line))).ToList();

    public void ClearBreakpoints()
    {
        foreach (var file in _breakpoints.Keys.ToList())
        {
            _breakpoints[file].Clear();
            BreakpointsChanged?.Invoke(file);
        }
    }

    // ─── 会话 ───

    public void Start(Compilation compilation, string[]? args = null)
    {
        Stop();

        var session = DebuggerSession.Create(compilation, args ?? _pendingArgs);
        foreach (var (file, line) in AllBreakpoints)
            session.SetBreakpoint(file, line);

        session.Paused += _ => Dispatcher.UIThread.Post(() => Paused?.Invoke());
        session.Exited += () => Dispatcher.UIThread.Post(() => Exited?.Invoke());

        _session = session;
        session.Start();
    }

    public void Continue()
    {
        _session?.Continue();
        Resumed?.Invoke();
    }

    public void StepOver()
    {
        _session?.StepOver();
        Resumed?.Invoke();
    }

    public void StepInto()
    {
        _session?.StepInto();
        Resumed?.Invoke();
    }

    public void StepOut()
    {
        _session?.StepOut();
        Resumed?.Invoke();
    }

    public void Stop()
    {
        _session?.Stop();
        _session = null;
    }
}
