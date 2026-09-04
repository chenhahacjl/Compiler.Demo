using System;
using System.Collections.Generic;

namespace Cocoa.Compiler.Terminal;

/// <summary>输出区行缓冲：每行由带颜色的文本段组成（回显代码经语法分类着色，普通消息纯白）。</summary>
internal sealed class OutputHistory
{
    private readonly List<IReadOnlyList<(string Text, ConsoleColor Fg, ConsoleColor Bg)>> _lines = new();

    public int LineCount => _lines.Count;

    public void AppendLine(string text, ConsoleColor foreground = ConsoleColor.White)
    {
        foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            _lines.Add(new[] { (line, foreground, ConsoleColor.Black) });
    }

    /// <summary>追加一行已分类的着色段（回显代码用）。</summary>
    public void AppendSegments(IReadOnlyList<(string Text, ConsoleColor Fg, ConsoleColor Bg)> segments)
    {
        _lines.Add(segments);
    }

    public void Clear()
    {
        _lines.Clear();
    }

    public void Render(Frame frame, Rect area)
    {
        if (_lines.Count == 0) return;

        var maxLines = Math.Min(_lines.Count, area.Height);
        var startIdx = Math.Max(0, _lines.Count - maxLines);

        for (var i = 0; i < maxLines; i++)
        {
            var segments = _lines[startIdx + i];
            var col = 0;

            foreach (var (text, fg, bg) in segments)
            {
                if (col >= area.Width) break;

                var len = Math.Min(text.Length, area.Width - col);
                if (len > 0)
                    frame.WriteString(area.X + col, area.Y + i, text.Substring(0, len), fg, bg);
                col += len;
            }

            var padLen = area.Width - col;
            if (padLen > 0)
                frame.WriteString(area.X + col, area.Y + i, new string(' ', padLen), ConsoleColor.White, ConsoleColor.Black);
        }
    }
}
