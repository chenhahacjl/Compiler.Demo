using System;
using System.IO;

namespace Cocoa.Cli.Repl;

internal sealed class ScreenBuffer
{
    private Cell[,] _current;
    private Cell[,] _previous;
    private readonly int _width;
    private readonly int _height;

    public int Width => _width;
    public int Height => _height;

    public ScreenBuffer(int width, int height)
    {
        _width = width;
        _height = height;
        _current = new Cell[height, width];
        _previous = new Cell[height, width];
        Clear();
    }

    public void Clear()
    {
        for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
                _current[y, x] = Cell.Empty;
    }

    public void SetCell(int x, int y, Cell cell)
    {
        if (x >= 0 && x < _width && y >= 0 && y < _height)
            _current[y, x] = cell;
    }

    public Cell GetCell(int x, int y)
    {
        if (x >= 0 && x < _width && y >= 0 && y < _height)
            return _current[y, x];
        return Cell.Empty;
    }

    public void WriteString(int x, int y, string text, ConsoleColor fg, ConsoleColor bg, bool bold = false)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var cx = x + i;
            if (cx >= _width) break;
            if (y >= 0 && y < _height)
                _current[y, cx] = new Cell(text[i], fg, bg, bold);
        }
    }

    public void WriteEmptyRow(int y, int startX = 0)
    {
        if (y < 0 || y >= _height) return;
        for (var x = startX; x < _width; x++)
            _current[y, x] = Cell.Empty;
    }

    public void DiffAndFlush(TextWriter output)
    {
        var lastFg = (ConsoleColor)(-1);
        var lastBg = (ConsoleColor)(-1);
        var lastBold = false;
        var lastX = -1;
        var lastY = -1;

        for (var y = 0; y < _height; y++)
        {
            var rowChanged = false;
            for (var x = 0; x < _width; x++)
            {
                if (_current[y, x] != _previous[y, x])
                {
                    rowChanged = true;
                    break;
                }
            }

            if (!rowChanged) continue;

            for (var x = 0; x < _width; x++)
            {
                var cell = _current[y, x];
                if (cell == _previous[y, x]) continue;

                if (lastY != y || lastX != x)
                {
                    output.Write($"\x1b[{y + 1};{x + 1}H");
                }

                if (cell.Foreground != lastFg || cell.Background != lastBg || cell.Bold != lastBold)
                {
                    output.Write(BuildSgr(cell.Foreground, cell.Background, cell.Bold));
                    lastFg = cell.Foreground;
                    lastBg = cell.Background;
                    lastBold = cell.Bold;
                }

                output.Write(cell.Character == '\0' ? ' ' : cell.Character);
                lastX = x + 1;
                lastY = y;
            }
        }

        SwapBuffers();
    }

    private void SwapBuffers()
    {
        var temp = _previous;
        _previous = _current;
        _current = temp;
    }

    private static string BuildSgr(ConsoleColor fg, ConsoleColor bg, bool bold)
    {
        var fgCode = MapColor(fg);
        var bgAnsi = MapColor(bg) + 10;
        var sgr = "\x1b[0";
        if (bold) sgr += ";1";
        sgr += $";{bgAnsi};{fgCode}m";
        sgr = sgr.Replace("[0;", "[");
        return sgr;
    }

    private static int MapColor(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => 30,
        ConsoleColor.DarkBlue => 34,
        ConsoleColor.DarkGreen => 32,
        ConsoleColor.DarkCyan => 36,
        ConsoleColor.DarkRed => 31,
        ConsoleColor.DarkMagenta => 35,
        ConsoleColor.DarkYellow => 33,
        ConsoleColor.Gray => 37,
        ConsoleColor.DarkGray => 90,
        ConsoleColor.Blue => 94,
        ConsoleColor.Green => 92,
        ConsoleColor.Cyan => 96,
        ConsoleColor.Red => 91,
        ConsoleColor.Magenta => 95,
        ConsoleColor.Yellow => 93,
        ConsoleColor.White => 97,
        _ => 37
    };
}
