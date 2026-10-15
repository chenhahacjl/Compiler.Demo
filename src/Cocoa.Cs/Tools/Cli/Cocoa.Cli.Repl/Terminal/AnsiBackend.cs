using System.IO;

namespace Cocoa.Cli.Repl;

internal sealed class AnsiBackend
{
    private readonly TextWriter _output;
    private bool _cursorVisible = true;

    public AnsiBackend(TextWriter output)
    {
        _output = output;
    }

    public void SetCursorPosition(int x, int y)
    {
        _output.Write($"\x1b[{y + 1};{x + 1}H");
    }

    public void SetCursorVisible(bool visible)
    {
        if (visible == _cursorVisible) return;
        _cursorVisible = visible;
        _output.Write(visible ? "\x1b[?25h" : "\x1b[?25l");
    }

    public void FlushBuffer(ScreenBuffer buffer)
    {
        buffer.DiffAndFlush(_output);
        _output.Flush();
    }

    public void ClearScreen()
    {
        _output.Write("\x1b[2J\x1b[H");
    }

    public void ResetColor()
    {
        _output.Write("\x1b[0m");
    }
}
