using System;

namespace Cocoa.Compiler.Terminal;

internal sealed class Frame
{
    private readonly ScreenBuffer _buffer;

    public int Width => _buffer.Width;
    public int Height => _buffer.Height;

    public Frame(ScreenBuffer buffer)
    {
        _buffer = buffer;
    }

    public void SetCell(int x, int y, char character, ConsoleColor fg, ConsoleColor bg, bool bold = false)
    {
        _buffer.SetCell(x, y, new Cell(character, fg, bg, bold));
    }

    public void WriteString(int x, int y, string text, ConsoleColor fg, ConsoleColor bg, bool bold = false)
    {
        _buffer.WriteString(x, y, text, fg, bg, bold);
    }

    public void WriteEmptyRow(int y, int startX = 0)
    {
        _buffer.WriteEmptyRow(y, startX);
    }

    public void FillRect(int x, int y, int width, int height, char fill = ' ', ConsoleColor fg = ConsoleColor.Gray, ConsoleColor bg = ConsoleColor.Black)
    {
        for (var row = y; row < y + height && row < _buffer.Height; row++)
            for (var col = x; col < x + width && col < _buffer.Width; col++)
                _buffer.SetCell(col, row, new Cell(fill, fg, bg, false));
    }

    public void DrawBox(int x, int y, int width, int height, ConsoleColor fg = ConsoleColor.Gray, ConsoleColor bg = ConsoleColor.Black)
    {
        if (width < 2 || height < 2) return;

        SetCell(x, y, '\u250c', fg, bg);
        SetCell(x + width - 1, y, '\u2510', fg, bg);
        SetCell(x, y + height - 1, '\u2514', fg, bg);
        SetCell(x + width - 1, y + height - 1, '\u2518', fg, bg);

        for (var col = x + 1; col < x + width - 1; col++)
        {
            SetCell(col, y, '\u2500', fg, bg);
            SetCell(col, y + height - 1, '\u2500', fg, bg);
        }

        for (var row = y + 1; row < y + height - 1; row++)
        {
            SetCell(x, row, '\u2502', fg, bg);
            SetCell(x + width - 1, row, '\u2502', fg, bg);
        }
    }
}
