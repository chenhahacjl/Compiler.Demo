using System;
using System.Collections.Generic;

namespace Cocoa.Cli.Repl;

internal sealed class CompletionPopup
{
    private const int MaxVisible = 8;

    private IReadOnlyList<CompletionItem> _items = Array.Empty<CompletionItem>();
    private int _selectedIndex;
    private bool _isVisible;

    public IReadOnlyList<CompletionItem> Items => _items;
    public int SelectedIndex => _selectedIndex;
    public bool IsVisible => _isVisible;
    public int VisibleRowCount => _isVisible ? Math.Min(MaxVisible, _items.Count) : 0;

    public void Show(IReadOnlyList<CompletionItem> items, int selectedIndex = 0)
    {
        _items = items;
        _selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Count - 1));
        _isVisible = items.Count > 0;
    }

    public void Hide()
    {
        _isVisible = false;
        _items = Array.Empty<CompletionItem>();
        _selectedIndex = 0;
    }

    public void MoveUp()
    {
        if (!_isVisible || _items.Count == 0) return;
        _selectedIndex = (_selectedIndex - 1 + _items.Count) % _items.Count;
    }

    public void MoveDown()
    {
        if (!_isVisible || _items.Count == 0) return;
        _selectedIndex = (_selectedIndex + 1) % _items.Count;
    }

    public CompletionItem? SelectedItem =>
        _isVisible && _selectedIndex >= 0 && _selectedIndex < _items.Count
            ? _items[_selectedIndex]
            : null;

    public void Render(Frame frame, Rect area)
    {
        if (!_isVisible || _items.Count == 0) return;

        var firstVisible = Math.Max(0, _selectedIndex - (MaxVisible - 1) / 2);
        firstVisible = Math.Min(firstVisible, Math.Max(0, _items.Count - MaxVisible));

        // 可视行数受弹窗区域高度约束（屏幕空间不足时收缩）
        var maxRows = Math.Max(0, area.Height - 2);
        var visibleCount = Math.Min(Math.Min(MaxVisible, maxRows), _items.Count - firstVisible);
        if (_selectedIndex >= firstVisible + visibleCount)
            firstVisible = Math.Max(0, _selectedIndex - visibleCount + 1);
        if (visibleCount <= 0) return;

        var boxWidth = Math.Min(60, area.Width);

        frame.DrawBox(area.X, area.Y, boxWidth, visibleCount + 2, ConsoleColor.DarkGray, ConsoleColor.Black);

        for (var i = 0; i < visibleCount; i++)
        {
            var item = _items[firstVisible + i];
            var isSelected = firstVisible + i == _selectedIndex;
            var row = area.Y + 1 + i;
            var contentWidth = boxWidth - 4;

            var bg = isSelected ? ConsoleColor.Gray : ConsoleColor.Black;
            var nameFg = isSelected ? ConsoleColor.Black : ConsoleColor.White;
            var detailFg = isSelected ? ConsoleColor.Black : ConsoleColor.DarkGray;

            var name = item.Text;
            if (name.Length > contentWidth)
                name = name.Substring(0, contentWidth);

            // 签名右对齐，与名字之间至少留 2 空格
            var maxDetailWidth = Math.Max(0, contentWidth - name.Length - 2);
            var detail = item.Detail ?? "";
            if (detail.Length > maxDetailWidth)
                detail = maxDetailWidth > 0 ? detail.Substring(0, maxDetailWidth) : "";

            frame.WriteString(area.X + 2, row, name.PadRight(contentWidth), nameFg, bg);

            if (detail.Length > 0)
                frame.WriteString(area.X + 2 + contentWidth - detail.Length, row, detail, detailFg, bg);
        }

        if (_items.Count > MaxVisible)
        {
            var footer = $" ({_items.Count} items) ";
            frame.WriteString(area.X + 2, area.Y + visibleCount + 1, footer, ConsoleColor.DarkGray, ConsoleColor.Black);
        }
    }
}
