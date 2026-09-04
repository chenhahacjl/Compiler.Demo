using System;

namespace Cocoa.Cli.Repl;

internal sealed class StatusBar
{
    private string _leftText = "";
    private string _centerText = "";
    private ConsoleColor _centerForeground = ConsoleColor.White;
    private string _rightText = "";

    public void SetLeft(string text) => _leftText = text ?? "";
    public void SetCenter(string text, ConsoleColor foreground = ConsoleColor.White)
    {
        _centerText = text ?? "";
        _centerForeground = foreground;
    }
    public void SetRight(string text) => _rightText = text ?? "";

    public void Render(Frame frame, Rect area)
    {
        var bg = ConsoleColor.DarkBlue;
        var fg = ConsoleColor.White;

        frame.FillRect(area.X, area.Y, area.Width, 1, ' ', fg, bg);

        var usedSides = 0;
        if (_leftText.Length > 0)
        {
            var maxLeft = Math.Min(_leftText.Length, area.Width / 3);
            frame.WriteString(area.X, area.Y, _leftText.Substring(0, maxLeft), fg, bg);
            usedSides += maxLeft;
        }

        if (_centerText.Length > 0)
        {
            var maxCenter = Math.Min(_centerText.Length, Math.Max(0, area.Width - usedSides - 2));
            var startX = area.X + (area.Width - maxCenter) / 2;
            frame.WriteString(startX, area.Y, _centerText.Substring(0, maxCenter), _centerForeground, bg);
        }

        if (_rightText.Length > 0)
        {
            var maxRight = Math.Min(_rightText.Length, area.Width / 3);
            var startX = area.X + area.Width - maxRight;
            frame.WriteString(startX, area.Y, _rightText.Substring(0, maxRight), fg, bg);
        }
    }
}
