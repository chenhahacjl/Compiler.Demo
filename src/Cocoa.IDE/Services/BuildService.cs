using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Cocoa.Build;
using Cocoa.IDE.ViewModels;

namespace Cocoa.IDE.Services;

/// <summary>F6 构建 + F5 运行。<see cref="ProjectBuilder"/>/<see cref="SolutionBuilder"/> 用
/// <see cref="TextWriter"/> 输出消息。诊断行格式：<c>file(line,col,line2,col2): message</c>
/// （位置均为 1-based；消息文本不含 error/warning 前缀，严重性仅靠颜色区分——非控制台捕获时
/// 一律按错误处理，保证跳转可用）。构建级 <c>error:</c>/<c>warning:</c> 行另行计数。</summary>
public sealed class BuildService
{
    private static readonly Regex DiagRegex =
        new(@"^(?<file>.+?)\((?<sl>\d+),(?<sc>\d+),(?<el>\d+),(?<ec>\d+)\): (?<msg>.+)$",
            RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _isBuilding;

    public bool IsBuilding => _isBuilding;

    /// <summary>(success, errors, warnings) — UI 线程触发</summary>
    public event Action<bool, int, int>? BuildFinished;

    /// <summary>原始输出行 — UI 线程触发</summary>
    public event Action<string>? OutputLine;

    /// <summary>(file, line, col, message) 定位诊断 — UI 线程触发</summary>
    public event Action<string, int, int, string>? ErrorReported;

    public async Task<bool> BuildProjectAsync(CocoaProjectFile project, bool noIncremental = false)
    {
        return await RunCoreAsync(w => ProjectBuilder.Build(project, new ProjectBuildOptions { NoIncremental = noIncremental }, w).Success);
    }

    public async Task<bool> BuildSolutionAsync(CocoaSolutionFile solution, bool noIncremental = false)
    {
        return await RunCoreAsync(w => SolutionBuilder.Build(solution, new ProjectBuildOptions { NoIncremental = noIncremental }, w));
    }

    public async Task<bool> RunAsync(CocoaProjectFile project)
    {
        var built = await BuildProjectAsync(project);
        if (!built) return false;

        var exePath = Path.Combine(project.GetOutputDirectory(), project.GetDefaultOutputFileName());
        if (!File.Exists(exePath))
        {
            OutputLine?.Invoke($"error: executable '{exePath}' was not produced by the build");
            BuildFinished?.Invoke(false, 1, 0);
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = project.Directory
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => OutputLine?.Invoke($"error: failed to launch '{exePath}': {ex.Message}"));
                return false;
            }
        });
    }

    private async Task<bool> RunCoreAsync(Func<TextWriter, bool> build)
    {
        await _gate.WaitAsync();
        if (_isBuilding)
        {
            _gate.Release();
            return false;
        }

        _isBuilding = true;
        var errors = 0;
        var warnings = 0;

        try
        {
            return await Task.Run(() =>
            {
                using var writer = new SinkTextWriter(line =>
                {
                    var (file, sl, sc, msg) = ParseDiagLine(line);
                    bool? isError = null;
                    if (file != null)
                    {
                        isError = true;
                    }
                    else if (line.StartsWith("error:", StringComparison.Ordinal))
                    {
                        isError = true;
                    }
                    else if (line.StartsWith("warning:", StringComparison.Ordinal))
                    {
                        isError = false;
                    }

                    if (isError == true) errors++;
                    else if (isError == false) warnings++;

                    var capturedFile = file;
                    var capturedSl = sl;
                    var capturedSc = sc;
                    var capturedMsg = msg ?? "";

                    Dispatcher.UIThread.Post(() =>
                    {
                        OutputLine?.Invoke(line);
                        if (capturedFile != null)
                            ErrorReported?.Invoke(capturedFile, capturedSl, capturedSc, capturedMsg);
                    });
                });

                var ok = build(writer);

                Dispatcher.UIThread.Post(() => BuildFinished?.Invoke(ok, errors, warnings));
                return ok;
            });
        }
        finally
        {
            _isBuilding = false;
            _gate.Release();
        }
    }

    private static (string? file, int line, int col, string? msg) ParseDiagLine(string line)
    {
        var m = DiagRegex.Match(line);
        if (!m.Success) return (null, 0, 0, null);
        return (
            m.Groups["file"].Value,
            int.Parse(m.Groups["sl"].Value),
            int.Parse(m.Groups["sc"].Value),
            m.Groups["msg"].Value
        );
    }
}

/// <summary>把后台构建的 <see cref="TextWriter"/> 输出按行转发到回调。</summary>
public sealed class SinkTextWriter : TextWriter
{
    private readonly Action<string> _onLine;
    private readonly StringBuilder _line = new();

    public SinkTextWriter(Action<string> onLine) => _onLine = onLine;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
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

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        foreach (var ch in value)
            Write(ch);
    }
}