using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class OutputViewModel : ObservableObject
{
    public ObservableCollection<string> Lines { get; } = new();

    private readonly StringBuilder _buffer = new();

    [ObservableProperty]
    private bool _isAutoScroll = true;

    public void AppendLine(string text)
    {
        Lines.Add(text);
        _buffer.AppendLine(text);

        // 保持合理数量，避免内存爆
        if (Lines.Count > 5000)
            Lines.RemoveAt(0);
    }

    public void AppendLines(IEnumerable<string> texts)
    {
        foreach (var t in texts)
            Lines.Add(t);
    }

    public void Clear()
    {
        Lines.Clear();
        _buffer.Clear();
    }

    public string GetAllText() => _buffer.ToString();
}
