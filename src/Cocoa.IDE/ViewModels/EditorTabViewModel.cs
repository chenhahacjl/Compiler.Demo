using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class EditorTabViewModel : ObservableObject
{
    private bool _suppressDirty;

    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string DirectoryPath => System.IO.Path.GetDirectoryName(FilePath) ?? "";

    private readonly string? _dialect;

    public EditorTabViewModel(string filePath)
    {
        FilePath = filePath;
        _dialect = System.IO.Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "CSharp",
            ".co" => "Cocoa",
            _     => null
        };

        if (System.IO.File.Exists(filePath))
        {
            _suppressDirty = true;
            Content = System.IO.File.ReadAllText(filePath);
            _suppressDirty = false;
        }
        else
        {
            Content = "";
        }
    }

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private int _cursorLine = 1;

    [ObservableProperty]
    private int _cursorColumn = 1;

    public string? Dialect => _dialect;

    partial void OnContentChanged(string value)
    {
        if (!_suppressDirty)
            IsModified = true;
    }

    public void MarkSaved()
    {
        IsModified = false;
        _suppressDirty = false;
    }
}