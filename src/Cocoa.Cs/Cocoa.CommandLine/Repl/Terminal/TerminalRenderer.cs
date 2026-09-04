using System;

namespace Cocoa.Compiler.Terminal;

internal sealed class TerminalRenderer : IDisposable
{
    private readonly AnsiBackend _backend;
    private ScreenBuffer _buffer;
    private Frame _frame;
    private readonly object _renderLock = new();
    private bool _disposed;

    public Frame Frame => _frame;
    public int Width => _buffer.Width;
    public int Height => _buffer.Height;

    public TerminalRenderer()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var width = Console.WindowWidth;
        var height = Console.WindowHeight;

        _backend = new AnsiBackend(Console.Out);
        _buffer = new ScreenBuffer(width, height);
        _frame = new Frame(_buffer);

        _backend.ClearScreen();
    }

    public void Draw(Action<Frame> renderAction)
    {
        if (_disposed) return;

        lock (_renderLock)
        {
            EnsureSize();
            _backend.SetCursorVisible(false);
            _buffer.Clear();
            renderAction(_frame);
            _backend.FlushBuffer(_buffer);
        }
    }

    /// <summary>检测控制台窗口尺寸变化并重建缓冲（旧内容清屏后全量重绘）。</summary>
    private void EnsureSize()
    {
        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            if (width == _buffer.Width && height == _buffer.Height) return;

            _backend.ClearScreen();
            _buffer = new ScreenBuffer(width, height);
            _frame = new Frame(_buffer);
        }
        catch
        {
            // 重定向终端可能拿不到窗口尺寸，保持现有缓冲即可。
        }
    }

    public void SetCursorPosition(int x, int y)
    {
        _backend.SetCursorPosition(x, y);
    }

    public void SetCursorVisible(bool visible)
    {
        _backend.SetCursorVisible(visible);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_renderLock)
        {
            _backend.SetCursorVisible(true);
            _backend.ResetColor();
        }
    }
}
