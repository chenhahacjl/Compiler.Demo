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

    public void SetBuildResult(bool success, int errors, int warnings)
    {
        StatusText = success
            ? $"生成成功（{errors} 错误, {warnings} 警告）"
            : $"生成失败（{errors} 错误, {warnings} 警告）";
    }

    public void ResetActiveDocument()
    {
        Language = "";
        CursorPosition = "Ln 1, Col 1";
    }
}