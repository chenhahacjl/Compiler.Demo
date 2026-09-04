using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocoa.CodeAnalysis;

namespace Cocoa.Cli.Repl;

/// <summary>后台实时诊断：主线程投递最新 live compilation，worker 排空队列只编译最新版本，
/// 结果经 <see cref="TryTakePendingResult"/> 由主循环取回渲染（避免跨线程改 UI 状态）。</summary>
internal sealed class DiagnosticsService : IDisposable
{
    private readonly Channel<DiagnosticRequest> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _worker;
    private readonly object _gate = new();

    private (int Version, IReadOnlyList<Diagnostic> Diagnostics)? _pendingResult;

    public DiagnosticsService()
    {
        _channel = Channel.CreateUnbounded<DiagnosticRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => ProcessRequests(_cts.Token));
    }

    public void RequestDiagnostics(Compilation compilation, int version)
    {
        _channel.Writer.TryWrite(new DiagnosticRequest(compilation, version));
    }

    /// <summary>主线程取回最新一批诊断结果及其版本；无新结果时返回 false。
    /// 调用方用版本号丢弃过期结果（如已转入元命令模式）。</summary>
    public bool TryTakePendingResult(out int version, out IReadOnlyList<Diagnostic>? diagnostics)
    {
        lock (_gate)
        {
            if (_pendingResult.HasValue)
            {
                (version, diagnostics) = _pendingResult.Value;
                _pendingResult = null;
                return true;
            }

            version = 0;
            diagnostics = null;
            return false;
        }
    }

    private async Task ProcessRequests(CancellationToken ct)
    {
        try
        {
            await foreach (var received in _channel.Reader.ReadAllAsync(ct))
            {
                // 排空通道只保留最新版本：快速打字时中间版本无需编译
                var request = received;
                while (_channel.Reader.TryRead(out var newer) && newer.Version > request.Version)
                    request = newer;

                try
                {
                    var diagnostics = request.Compilation.GetDiagnostics();
                    lock (_gate)
                    {
                        _pendingResult = (request.Version, diagnostics);
                    }
                }
                catch
                {
                    // 半成品输入的编译可能失败，保留上一次结果即可。
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose 触发的正常取消。
        }
        catch
        {
            // 后台诊断绝不能拖垮 REPL。
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _worker.Wait(200);
        }
        catch
        {
            // worker 可能已结束或出错，无需在此观察。
        }
        _cts.Dispose();
    }

    private sealed record DiagnosticRequest(Compilation Compilation, int Version);
}
