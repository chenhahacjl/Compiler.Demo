using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class EditorTabViewModel : ObservableObject
{
    private bool _suppressDirty;

    public string FilePath { get; }
    public string FileName => _displayName ?? System.IO.Path.GetFileName(FilePath);
    public string DirectoryPath => System.IO.Path.GetDirectoryName(FilePath) ?? "";

    private readonly string? _dialect;
    private readonly string? _displayName;

    /// <summary>非 null 时为“项目属性”虚拟标签（中央区显示属性页而非编辑器）。</summary>
    public ProjectPropertiesViewModel? ProjectProperties { get; }

    public bool IsVirtual => ProjectProperties != null;

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

    /// <summary>“项目属性”虚拟标签：不读文件、不参与诊断/保存。</summary>
    public EditorTabViewModel(ProjectPropertiesViewModel properties)
    {
        ProjectProperties = properties;
        FilePath = properties.ProjectFilePath + ".props";
        _displayName = properties.DisplayName;
        _suppressDirty = true;
        Content = "";
        IsModified = false;
    }

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private int _cursorLine = 1;

    [ObservableProperty]
    private int _cursorColumn = 1;

    /// <summary>当前文件最新诊断（M3 实时诊断结果），供编辑器画波浪线。</summary>
    [ObservableProperty]
    private ImmutableArray<Diagnostic> _diagnostics = ImmutableArray<Diagnostic>.Empty;

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