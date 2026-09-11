using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cocoa.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public enum DiagnosticSeverity { Error, Warning, Info, Hidden }

public partial class ErrorItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = "";

    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public DiagnosticSeverity Severity { get; set; }

    public string SeverityIcon => Severity switch
    {
        DiagnosticSeverity.Error => "✗",
        DiagnosticSeverity.Warning => "⚠",
        DiagnosticSeverity.Info => "ℹ",
        _ => ""
    };

    public string Location => string.IsNullOrEmpty(FilePath) ? ""
        : $"{System.IO.Path.GetFileName(FilePath)}({Line},{Column})";
}

public partial class ErrorListViewModel : ObservableObject
{
    public ObservableCollection<ErrorItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private bool _showErrors = true;

    [ObservableProperty]
    private bool _showWarnings = true;

    [ObservableProperty]
    private ErrorItemViewModel? _selectedItem;

    public event Action<ErrorItemViewModel>? ItemActivated;

    partial void OnShowErrorsChanged(bool value) => OnFilterChanged();
    partial void OnShowWarningsChanged(bool value) => OnFilterChanged();

    private void OnFilterChanged()
    {
        OnPropertyChanged(nameof(FilteredItems));
    }

    public IEnumerable<ErrorItemViewModel> FilteredItems =>
        Items.Where(e => (e.Severity == DiagnosticSeverity.Error && ShowErrors) ||
                         (e.Severity == DiagnosticSeverity.Warning && ShowWarnings));

    public void Clear()
    {
        Items.Clear();
        RecomputeCounts();
        OnPropertyChanged(nameof(FilteredItems));
    }

    public void Add(string file, int line, int col, string message, DiagnosticSeverity severity)
    {
        Items.Add(new ErrorItemViewModel { Message = message, FilePath = file, Line = line, Column = col, Severity = severity });
        RecomputeCounts();
        OnPropertyChanged(nameof(FilteredItems));
    }

    /// <summary>用实时诊断替换某文件的全部条目（M3）。</summary>
    public void ReplaceFile(string filePath, ImmutableArray<Diagnostic> diagnostics)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Items[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                Items.RemoveAt(i);
        }

        foreach (var d in diagnostics)
        {
            if (d.Location.Text == null) continue;

            Items.Add(new ErrorItemViewModel
            {
                Message = d.Message,
                FilePath = d.Location.FileName,
                Line = d.Location.StartLine + 1,
                Column = d.Location.StartCharacter + 1,
                Severity = d.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning
            });
        }

        RecomputeCounts();
        OnPropertyChanged(nameof(FilteredItems));
    }

    /// <summary>切换标签时恢复该文件的诊断条目（等价于 ReplaceFile，语义更明确）。</summary>
    public void ShowFile(string filePath, ImmutableArray<Diagnostic> diagnostics)
        => ReplaceFile(filePath, diagnostics);

    private void RecomputeCounts()
    {
        ErrorCount = Items.Count(i => i.Severity == DiagnosticSeverity.Error);
        WarningCount = Items.Count(i => i.Severity == DiagnosticSeverity.Warning);
    }

    [RelayCommand]
    private void Activate(ErrorItemViewModel item) => ItemActivated?.Invoke(item);
}