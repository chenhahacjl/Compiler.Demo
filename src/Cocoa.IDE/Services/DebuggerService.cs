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

    /// <summary>E2：调试期间解释器 Console 输出（UI 线程）。</summary>
    public event Action<string>? OutputLine;

    private TextWriter? _savedOut;
    private TextWriter? _savedError;
    private DebugOutputWriter? _consoleWriter;

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
        session.Exited += () =>
        {
            RestoreConsole();
            Dispatcher.UIThread.Post(() => Exited?.Invoke());
        };

        _session = session;
        RedirectConsole();
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
        RestoreConsole();
    }

    // ─── E2：解释器 Console → IDE 输出面板 ───

    private void RedirectConsole()
    {
        try
        {
            _savedOut = Console.Out;
            _savedError = Console.Error;
            _consoleWriter = new DebugOutputWriter(line => Dispatcher.UIThread.Post(() => OutputLine?.Invoke(line)));
            Console.SetOut(_consoleWriter);
            Console.SetError(_consoleWriter);
        }
        catch
        {
            _consoleWriter = null;
        }
    }

    private void RestoreConsole()
    {
        if (_consoleWriter == null) return;
        try
        {
            if (_savedOut != null) Console.SetOut(_savedOut);
            if (_savedError != null) Console.SetError(_savedError);
        }
        catch
        {
            // 忽略恢复失败
        }
        finally
        {
            _consoleWriter = null;
        }
    }
}

/// <summary>调试期间把解释器的 Console 输出按行转发到 IDE（GUI 无控制台）。</summary>
internal sealed class DebugOutputWriter : TextWriter
{
    private readonly Action<string> _onLine;
    private readonly System.Text.StringBuilder _line = new();
    private readonly object _gate = new();

    public DebugOutputWriter(Action<string> onLine) => _onLine = onLine;

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                var text = _line.ToString();
                _line.Clear();
                if (text.Length > 0) _onLine(text);
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var ch in value) Write(ch);
    }
}
