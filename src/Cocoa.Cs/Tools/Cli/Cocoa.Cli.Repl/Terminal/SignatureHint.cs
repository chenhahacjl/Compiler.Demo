using System;

namespace Cocoa.Cli.Repl;

internal sealed class SignatureHint
{
    private string? _text;

    public string? Text => _text;
    public bool HasContent => !string.IsNullOrEmpty(_text);

    public void Set(string? text)
    {
        _text = string.IsNullOrEmpty(text) ? null : text;
    }

    public void Clear()
    {
        _text = null;
    }

    public void Render(Frame frame, Rect area)
    {
        if (string.IsNullOrEmpty(_text)) return;

        var text = _text;
        var maxWidth = area.Width - 4;
        if (text.Length > maxWidth)
            text = text.Substring(0, maxWidth);

        frame.WriteString(area.X + 4, area.Y, text, ConsoleColor.Cyan, ConsoleColor.Black);
    }
}
