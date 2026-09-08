using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class EditorTabViewModel : ObservableObject
{
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string DirectoryPath => System.IO.Path.GetDirectoryName(FilePath) ?? "";
    public string LanguageDialect => System.IO.Path.GetExtension(FilePath).ToLowerInvariant() == ".cs" ? "CSharp" : "Cocoa";

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private int _cursorLine = 1;

    [ObservableProperty]
    private int _cursorColumn = 1;

    public EditorTabViewModel(string filePath)
    {
        FilePath = filePath;
        if (System.IO.File.Exists(filePath))
            Content = System.IO.File.ReadAllText(filePath);
    }

    partial void OnContentChanged(string value) => IsModified = true;

    public void MarkSaved() => IsModified = false;
}
