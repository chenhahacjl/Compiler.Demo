using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class OutputViewModel : ObservableObject
{
    private const int MaxLines = 5000;

    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty]
    private bool _isAutoScroll = true;

    /// <summary>请求复制时触发，由视图层把文本放到剪贴板。</summary>
    public event Action<string>? CopyRequested;

    public void AppendLine(string text)
    {
        Lines.Add(text);

        if (Lines.Count > MaxLines)
        {
            for (var i = Lines.Count - MaxLines; i > 0; i--)
                Lines.RemoveAt(0);
        }
    }

    public void AppendLines(IEnumerable<string> texts)
    {
        foreach (var t in texts)
            AppendLine(t);
    }

    public void Clear()
    {
        Lines.Clear();
    }

    public string GetAllText() => string.Join(Environment.NewLine, Lines);

    [RelayCommand]
    private void Copy()
    {
        var text = GetAllText();
        if (text.Length > 0)
            CopyRequested?.Invoke(text);
    }

    [RelayCommand]
    private void ClearOutput()
    {
        Clear();
    }
}