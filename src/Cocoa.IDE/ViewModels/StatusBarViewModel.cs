using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _cursorPosition = "Ln 1, Col 1";

    [ObservableProperty]
    private string _encoding = "UTF-8";

    [ObservableProperty]
    private string _language = "";

    [ObservableProperty]
    private string _solutionName = "";
}
