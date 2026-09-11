using System.Collections.ObjectModel;
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

    public void Clear() { Items.Clear(); ErrorCount = 0; WarningCount = 0; }

    public void Add(string file, int line, int col, string message, DiagnosticSeverity severity)
    {
        Items.Add(new ErrorItemViewModel { Message = message, FilePath = file, Line = line, Column = col, Severity = severity });

        if (severity == DiagnosticSeverity.Error) ErrorCount++;
        else if (severity == DiagnosticSeverity.Warning) WarningCount++;

        OnPropertyChanged(nameof(FilteredItems));
    }

    [RelayCommand]
    private void Activate(ErrorItemViewModel item) => ItemActivated?.Invoke(item);
}