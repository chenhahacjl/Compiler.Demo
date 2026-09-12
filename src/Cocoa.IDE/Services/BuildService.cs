using System.Diagnostics;
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
    private volatile Process? _runningProcess;

    /// <summary>(success, errors, warnings) — UI 线程触发</summary>
    public event Action<bool, int, int>? BuildFinished;

    /// <summary>(exitCode) — 运行结束（UI 线程触发）</summary>
    public event Action<int>? RunFinished;

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
            // 构建已通过 BuildFinished 汇报结果；此处仅报告缺少产物，不再重复触发
            Dispatcher.UIThread.Post(() => OutputLine?.Invoke($"error: executable '{exePath}' was not produced by the build"));
            return false;
        }

        var workingDir = Path.GetDirectoryName(exePath) ?? project.Directory;

        return await Task.Run(() =>
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };

            Process? proc = null;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) Dispatcher.UIThread.Post(() => OutputLine?.Invoke(e.Data));
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) Dispatcher.UIThread.Post(() => OutputLine?.Invoke(e.Data));
                };

                proc.Start();
                _runningProcess = proc;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                proc.WaitForExit(); // 确保异步输出读取全部完成

                var exitCode = proc.ExitCode;
                Dispatcher.UIThread.Post(() =>
                {
                    OutputLine?.Invoke($"程序已退出，退出代码 {exitCode}。");
                    RunFinished?.Invoke(exitCode);
                });
                return exitCode == 0;
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => OutputLine?.Invoke($"error: failed to launch '{exePath}': {ex.Message}"));
                return false;
            }
            finally
            {
                _runningProcess = null;
                proc?.Dispose();
            }
        });
    }

    /// <summary>终止当前正在运行的程序（若有）。</summary>
    public void Stop()
    {
        var proc = _runningProcess;
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出，忽略
        }
    }

    private async Task<bool> RunCoreAsync(Func<TextWriter, bool> build)
    {
        await _gate.WaitAsync();
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