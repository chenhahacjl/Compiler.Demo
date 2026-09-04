using System;
using System.Collections.Generic;

namespace Cocoa.Cli.Repl;

/// <summary>补全状态机：触发时向 provider 请求候选，弹窗的选择/展示由 CompletionPopup 承担。</summary>
internal sealed class CompletionEngine
{
    private readonly ICompletionProvider _provider;
    private IReadOnlyList<CompletionItem> _items = Array.Empty<CompletionItem>();
    private int _selectedIndex;
    private bool _isVisible;

    public IReadOnlyList<CompletionItem> Items => _items;
    public int SelectedIndex => _selectedIndex;
    public bool IsVisible => _isVisible;

    public CompletionEngine(ICompletionProvider provider)
    {
        _provider = provider;
    }

    public void Trigger(string text, int cursorPosition)
    {
        _items = _provider.GetCompletions(text, cursorPosition);
        _selectedIndex = 0;
        _isVisible = _items.Count > 0;
    }
}
