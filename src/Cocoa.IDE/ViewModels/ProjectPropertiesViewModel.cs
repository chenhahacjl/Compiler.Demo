using System.Collections.ObjectModel;
using Cocoa.Build;
using Cocoa.IDE.Services;
using Cocoa.Targeting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

/// <summary>项目属性页（VS 风格文档标签）：编辑并写回 <c>.coproj</c> 的常用属性。</summary>
public partial class ProjectPropertiesViewModel : ObservableObject
{
    private string _projectPath = "";

    public string ProjectFilePath => _projectPath;
    public string ProjectDirectory { get; private set; } = "";
    public string DisplayName { get; private set; } = "项目属性";

    public IReadOnlyList<string> OutputTypes { get; } = new[] { "Executable", "Library", "Cod" };
    public IReadOnlyList<string> Platforms { get; } = new[] { "AnyCPU", "x86", "x64" };
    public IReadOnlyList<string> TargetOses { get; } = new[] { "Windows", "Linux" };
    public IReadOnlyList<string> Configurations { get; } = new[] { "Debug", "Release" };
    public IReadOnlyList<string> Backends { get; } = new[] { "(默认 Managed)", "Managed", "Native" };

    [ObservableProperty] private string _assemblyName = "";
    [ObservableProperty] private string _outputType = "Executable";
    [ObservableProperty] private string _backend = "(默认 Managed)";
    [ObservableProperty] private string _platform = "AnyCPU";
    [ObservableProperty] private string _targetFramework = "";
    [ObservableProperty] private string _targetOs = "Windows";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private string _startupObject = "";
    [ObservableProperty] private string _subsystem = "";
    [ObservableProperty] private string _configuration = "Debug";
    [ObservableProperty] private bool _treatWarningsAsErrors;
    [ObservableProperty] private bool _optimize;
    [ObservableProperty] private string _statusText = "";

    public ObservableCollection<string> References { get; } = new();
    public ObservableCollection<string> SourcePatterns { get; } = new();

    /// <summary>保存成功后触发（供刷新解决方案树/状态栏）。</summary>
    public event Action<ProjectPropertiesViewModel>? Saved;

    public ProjectPropertiesViewModel(CocoaProjectFile project) => Load(project);

    /// <summary>用最新的工程模型刷新字段（重开属性页时调用）。</summary>
    public void ReloadFrom(CocoaProjectFile project) => Load(project);

    private void Load(CocoaProjectFile project)
    {
        _projectPath = project.FilePath;
        ProjectDirectory = project.Directory;
        DisplayName = project.Name + " 属性";

        AssemblyName = project.AssemblyName;
        OutputType = project.Output switch
        {
            ProjectOutputFormat.Dll => "Library",
            ProjectOutputFormat.Cod => "Cod",
            _ => "Executable",
        };
        Backend = project.Backend switch
        {
            CodeBackend.Native => "Native",
            CodeBackend.DotNet => "Managed",
            _ => "(默认 Managed)",
        };
        Platform = project.Platform;
        TargetFramework = project.DotnetRuntime ?? "";
        TargetOs = project.TargetOs.ToString();
        OutputPath = project.OutputPath ?? "";
        StartupObject = project.Entry ?? "";
        Subsystem = project.Subsystem ?? "";
        Configuration = project.Configuration.ToString();
        TreatWarningsAsErrors = project.TreatWarningsAsErrors;
        Optimize = project.Optimize;

        References.Clear();
        foreach (var r in project.References) References.Add(r);
        SourcePatterns.Clear();
        foreach (var s in project.SourcePatterns) SourcePatterns.Add(s);

        StatusText = "";
    }

    [RelayCommand]
    private void Reload()
    {
        try
        {
            Load(CocoaProjectFile.Load(_projectPath));
        }
        catch (Exception ex)
        {
            StatusText = "重新加载失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AssemblyName"] = AssemblyName,
            ["OutputType"] = OutputType,
            ["Backend"] = Backend == "Native" ? "Native" : Backend == "Managed" ? "Managed" : null,
            ["Platform"] = Platform,
            ["TargetFramework"] = Blank(TargetFramework),
            ["TargetOS"] = TargetOs,
            ["OutputPath"] = Blank(OutputPath),
            ["StartupObject"] = Blank(StartupObject),
            ["Subsystem"] = Blank(Subsystem),
            ["Configuration"] = Configuration,
            ["TreatWarningsAsErrors"] = TreatWarningsAsErrors ? "true" : null,
            ["Optimize"] = Optimize ? "true" : null,
        };

        if (ProjectFileService.UpdateProperties(_projectPath, values, out var error))
        {
            StatusText = "已保存";
            Saved?.Invoke(this);
        }
        else
        {
            StatusText = "保存失败：" + error;
        }
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
