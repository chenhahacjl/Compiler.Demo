using System;
using System.Collections.Generic;

namespace Cocoa.Cli.Repl;

internal sealed class InputEditor
{
    private readonly List<string> _lines = new() { "" };
    private int _cursorLine;
    private int _cursorColumn;

    public IReadOnlyList<string> Lines => _lines;
    public int CursorLine => _cursorLine;
    public int CursorColumn => _cursorColumn;
    public int LineCount => _lines.Count;
    public bool IsEmpty => _lines.Count == 1 && _lines[0].Length == 0;

    public string Text => string.Join(Environment.NewLine, _lines);

    public void InsertChar(char c)
    {
        var line = _lines[_cursorLine];
        _lines[_cursorLine] = line.Insert(_cursorColumn, c.ToString());
        _cursorColumn++;
    }

    public void InsertLine()
    {
        var line = _lines[_cursorLine];
        var remainder = line.Substring(_cursorColumn);
        _lines[_cursorLine] = line.Substring(0, _cursorColumn);

        var baseIndent = GetIndentation(line);
        var opensBlock = line.TrimEnd().EndsWith("{");
        var closesBlockAhead = remainder.TrimStart().StartsWith("}");

        var insertIndex = _cursorLine + 1;

        if (opensBlock && closesBlockAhead)
        {
            var innerIndent = baseIndent + "    ";
            _lines.Insert(insertIndex, innerIndent);
            _lines.Insert(insertIndex + 1, remainder);
            _cursorLine = insertIndex;
            _cursorColumn = innerIndent.Length;
        }
        else if (opensBlock)
        {
            var innerIndent = baseIndent + "    ";
            _lines.Insert(insertIndex, remainder.Length > 0 ? remainder : innerIndent);
            _cursorLine = insertIndex;
            _cursorColumn = opensBlock ? innerIndent.Length : 0;
        }
        else
        {
            _lines.Insert(insertIndex, remainder);
            _cursorLine = insertIndex;
            _cursorColumn = 0;
        }
    }

    public void DeleteCharBackward()
    {
        if (_cursorColumn > 0)
        {
            var line = _lines[_cursorLine];
            _lines[_cursorLine] = line.Remove(_cursorColumn - 1, 1);
            _cursorColumn--;
        }
        else if (_cursorLine > 0)
        {
            var prevLine = _lines[_cursorLine - 1];
            var curLine = _lines[_cursorLine];
            _lines[_cursorLine - 1] = prevLine + curLine;
            _lines.RemoveAt(_cursorLine);
            _cursorLine--;
            _cursorColumn = prevLine.Length;
        }
    }

    public void DeleteCharForward()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn < line.Length)
        {
            _lines[_cursorLine] = line.Remove(_cursorColumn, 1);
        }
        else if (_cursorLine < _lines.Count - 1)
        {
            var nextLine = _lines[_cursorLine + 1];
            _lines[_cursorLine] = line + nextLine;
            _lines.RemoveAt(_cursorLine + 1);
        }
    }

    public void MoveLeft()
    {
        if (_cursorColumn > 0)
            _cursorColumn--;
        else if (_cursorLine > 0)
        {
            _cursorLine--;
            _cursorColumn = _lines[_cursorLine].Length;
        }
    }

    public void MoveRight()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn < line.Length)
            _cursorColumn++;
        else if (_cursorLine < _lines.Count - 1)
        {
            _cursorLine++;
            _cursorColumn = 0;
        }
    }

    public void MoveUp()
    {
        if (_cursorLine > 0)
        {
            _cursorLine--;
            _cursorColumn = Math.Min(_cursorColumn, _lines[_cursorLine].Length);
        }
    }

    public void MoveDown()
    {
        if (_cursorLine < _lines.Count - 1)
        {
            _cursorLine++;
            _cursorColumn = Math.Min(_cursorColumn, _lines[_cursorLine].Length);
        }
    }

    public void MoveHome()
    {
        _cursorColumn = 0;
    }

    public void MoveEnd()
    {
        _cursorColumn = _lines[_cursorLine].Length;
    }

    public void MoveWordLeft()
    {
        if (_cursorColumn == 0 && _cursorLine > 0)
        {
            _cursorLine--;
            _cursorColumn = _lines[_cursorLine].Length;
            return;
        }

        var line = _lines[_cursorLine];
        if (_cursorColumn == 0) return;

        var pos = _cursorColumn - 1;
        while (pos > 0 && char.IsWhiteSpace(line[pos]))
            pos--;
        while (pos > 0 && !char.IsWhiteSpace(line[pos - 1]))
            pos--;
        _cursorColumn = pos;
    }

    public void MoveWordRight()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn >= line.Length && _cursorLine < _lines.Count - 1)
        {
            _cursorLine++;
            _cursorColumn = 0;
            return;
        }

        var pos = _cursorColumn;
        while (pos < line.Length && char.IsWhiteSpace(line[pos]))
            pos++;
        while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            pos++;
        _cursorColumn = pos;
    }

    public void DeleteWordBackward()
    {
        var oldCol = _cursorColumn;
        var oldLine = _cursorLine;
        MoveWordLeft();
        if (_cursorLine == oldLine)
        {
            var line = _lines[_cursorLine];
            _lines[_cursorLine] = line.Remove(_cursorColumn, oldCol - _cursorColumn);
        }
        else
        {
            var prevLine = _lines[_cursorLine];
            var curLine = _lines[oldLine];
            var removed = prevLine.Substring(_cursorColumn) + curLine.Substring(0, oldCol);
            _lines[_cursorLine] = prevLine.Substring(0, _cursorColumn) + curLine.Substring(oldCol);
            _lines.RemoveAt(oldLine);
            _cursorLine--;
        }
    }

    public void Clear()
    {
        _lines.Clear();
        _lines.Add("");
        _cursorLine = 0;
        _cursorColumn = 0;
    }

    public void SetText(string text)
    {
        _lines.Clear();
        _lines.AddRange(text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));
        if (_lines.Count == 0) _lines.Add("");
        _cursorLine = 0;
        _cursorColumn = 0;
    }

    public void SetCursor(int line, int column)
    {
        _cursorLine = Math.Clamp(line, 0, _lines.Count - 1);
        _cursorColumn = Math.Clamp(column, 0, _lines[_cursorLine].Length);
    }

    private static string GetIndentation(string line)
    {
        var indent = "";
        foreach (var c in line)
        {
            if (c == ' ') indent += " ";
            else if (c == '\t') indent += "    ";
            else break;
        }
        return indent;
    }
}
