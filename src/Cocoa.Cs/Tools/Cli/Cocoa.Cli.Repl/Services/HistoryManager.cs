using System.Collections.Generic;

namespace Cocoa.Cli.Repl;

/// <summary>提交历史与 PageUp/PageDown 导航（_index == Count 表示未在导航中）。</summary>
internal sealed class HistoryManager
{
    private readonly List<string> _history = new();
    private int _index;

    public bool IsNavigating => _index < _history.Count;

    public void Add(string submission)
    {
        if (string.IsNullOrWhiteSpace(submission)) return;
        if (_history.Count > 0 && _history[^1] == submission) return;
        _history.Add(submission);
        _index = _history.Count;
    }

    public void Reset() => _index = _history.Count;

    /// <summary>翻到更早的一条；已在最早一条时返回 null。</summary>
    public string? MoveOlder()
    {
        if (_history.Count == 0) return null;

        if (_index >= _history.Count)
            _index = _history.Count - 1;
        else if (_index > 0)
            _index--;
        else
            return null;

        return _history[_index];
    }

    /// <summary>翻到更新的一条；越过最新一条时返回 null（调用方清空输入）。</summary>
    public string? MoveNewer()
    {
        if (_index >= _history.Count) return null;

        _index++;
        return _index < _history.Count ? _history[_index] : null;
    }
}
