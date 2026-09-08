using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public enum DiagnosticSeverity { Error, Warning, Info, Hidden }

public partial class ErrorItemViewModel : ObservableObject
{
    public string Message { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public DiagnosticSeverity Severity { get; set; }

    public string SeverityIcon => Severity switch
    {
        DiagnosticSeverity.Error => "❌",
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

    public IEnumerable<ErrorItemViewModel> FilteredItems =>
        Items.Where(e => (e.Severity == DiagnosticSeverity.Error && ShowErrors) ||
                         (e.Severity == DiagnosticSeverity.Warning && ShowWarnings));

    public void Clear() { Items.Clear(); ErrorCount = 0; WarningCount = 0; }

    public void AddError(string file, int line, int col, string message)
    {
        Items.Add(new ErrorItemViewModel { Message = message, FilePath = file, Line = line, Column = col, Severity = DiagnosticSeverity.Error });
        ErrorCount++;
    }

    public void AddWarning(string file, int line, int col, string message)
    {
        Items.Add(new ErrorItemViewModel { Message = message, FilePath = file, Line = line, Column = col, Severity = DiagnosticSeverity.Warning });
        WarningCount++;
    }
}
