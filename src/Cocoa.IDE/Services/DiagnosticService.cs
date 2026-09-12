using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Avalonia.Threading;

namespace Cocoa.IDE.Services;

/// <summary>M3 实时诊断：键入防抖 → 后台重解析 + 单文件编译 → 推送诊断。</summary>
public sealed class DiagnosticService
{
    private const int DebounceMilliseconds = 300;

    private readonly object _sync = new();
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(filePath, diagnostics) — 每次重解析后触发（UI 线程）。</summary>
    public event Action<string, ImmutableArray<Diagnostic>>? DiagnosticsReady;

    /// <summary>文件关闭时清理挂起的防抖任务。</summary>
    public void CloseFile(string filePath)
    {
        lock (_sync)
        {
            if (_debounce.TryGetValue(filePath, out var cts))
            {
                cts.Cancel();
                _debounce.Remove(filePath);
            }
        }
    }

    /// <summary>文档内容变化 → 防抖后触发重解析。已在 UI 线程调用。
    /// 传入 <paramref name="context"/> 时做多文件 + 引用编译（跨文件符号可解析，避免误报）。</summary>
    public void TextChanged(string filePath, string text, string? dialect, ProjectContext? context = null)
    {
        CancellationTokenSource? existing;
        lock (_sync)
        {
            if (!_debounce.TryGetValue(filePath, out existing))
            {
                existing = new CancellationTokenSource();
                _debounce[filePath] = existing;
            }
        }

        existing.Cancel();
        var newCts = new CancellationTokenSource();
        lock (_sync) _debounce[filePath] = newCts;
        var token = newCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMilliseconds, token);
                if (token.IsCancellationRequested) return;

                var tree = SyntaxTree.Parse(SourceText.From(text, filePath), LanguageFor(dialect));
                var diagnostics = tree.Diagnostics;

                // 多文件（含工程引用）语义编译：让跨文件/引用符号可解析
                var trees = new List<SyntaxTree> { tree };
                var references = new List<string>();
                if (context != null)
                {
                    references.AddRange(context.References);
                    foreach (var source in context.SourceFiles)
                    {
                        if (string.Equals(source, filePath, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            if (File.Exists(source))
                                trees.Add(SyntaxTree.Load(source));
                        }
                        catch { /* 忽略损坏文件 */ }
                    }
                }

                var compilation = Compilation.Create(references.ToArray(), trees.ToArray());
                var semantic = compilation.GetDiagnostics()
                    .Where(d => string.Equals(d.Location.FileName, filePath, StringComparison.OrdinalIgnoreCase));
                diagnostics = diagnostics.AddRange(semantic);

                var final = diagnostics
                    .Where(d => d.Location.Text != null)
                    .ToImmutableArray();

                if (token.IsCancellationRequested) return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    DiagnosticsReady?.Invoke(filePath, final);
                });
            }
            catch (OperationCanceledException)
            {
                // 防抖被新输入取代，忽略
            }
            catch (Exception)
            {
                // 后台解析异常不致命
            }
        }, token);
    }

    private static Language LanguageFor(string? dialect)
    {
        if (string.Equals(dialect, "CSharp", StringComparison.OrdinalIgnoreCase))
            return Language.CSharp;
        return Language.Cocoa;
    }
}