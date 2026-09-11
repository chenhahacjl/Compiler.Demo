using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Cocoa.Build;
using Cocoa.IDE.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public SolutionTreeViewModel SolutionTree { get; } = new();
    public EditorTabsViewModel EditorTabs { get; } = new();
    public ErrorListViewModel ErrorList { get; } = new();
    public OutputViewModel Output { get; } = new();
    public StatusBarViewModel StatusBar { get; } = new();
    public BuildService BuildService { get; } = new();

    private Window? MainWindow => App.Current?.ApplicationLifetime is
        IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

    public MainViewModel()
    {
        SolutionTree.FileActivated += path => EditorTabs.OpenFile(path);
        EditorTabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorTabs.ActiveTab))
                OnActiveTabChanged();
        };
        ErrorList.ItemActivated += item => NavigateToError(item);
        BuildService.OutputLine += line => Output.AppendLine(line);
        BuildService.ErrorReported += (file, line, col, msg) =>
            ErrorList.Add(file, line, col, msg, DiagnosticSeverity.Error);
        BuildService.BuildFinished += (success, errors, warnings) =>
            StatusBar.SetBuildResult(success, errors, warnings);
    }

    public void InitializeServices()
    {
        // 占位：后续可在此注册文件监听等
    }

    private void OnActiveTabChanged()
    {
        var tab = EditorTabs.ActiveTab;
        if (tab == null)
        {
            StatusBar.ResetActiveDocument();
            return;
        }

        StatusBar.Language = tab.Dialect ?? "";
        StatusBar.CursorPosition = $"Ln {tab.CursorLine}, Col {tab.CursorColumn}";
    }

    private void NavigateToError(ErrorItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.FilePath)) return;
        EditorTabs.OpenFile(item.FilePath);
        var tab = EditorTabs.ActiveTab;
        if (tab != null)
        {
            tab.CursorLine = Math.Max(1, item.Line);
            tab.CursorColumn = Math.Max(1, item.Column);
            EditorContent?.Invoke(tab.FilePath, item.Line, item.Column);
        }
    }

    public event Action<string, int, int>? EditorContent; // filePath, line, col — 供视图将光标移到文件错误位置

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (MainWindow == null) return;

        var files = await MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cocoa 文件") { Patterns = new[] { "*.co", "*.cs" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
                EditorTabs.OpenFile(path);
        }
    }

    [RelayCommand]
    private async Task OpenSolutionPickerAsync()
    {
        if (MainWindow == null) return;

        var files = await MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开解决方案",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cocoa 解决方案") { Patterns = new[] { "*.cosln", "*.coproj" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null)
            {
                SolutionTree.LoadPath(path);
                StatusBar.SolutionName = SolutionTree.SolutionName;
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (EditorTabs.ActiveTab == null) return;
        await SaveTabAsync(EditorTabs.ActiveTab);
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        foreach (var tab in EditorTabs.Tabs.Where(t => t.IsModified))
            await SaveTabAsync(tab);
    }

    private async Task SaveTabAsync(EditorTabViewModel tab)
    {
        try
        {
            await File.WriteAllTextAsync(tab.FilePath, tab.Content);
            tab.MarkSaved();
        }
        catch (Exception ex)
        {
            Output.AppendLine($"error: 保存失败 '{tab.FilePath}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseTab()
    {
        if (EditorTabs.ActiveTab != null)
            EditorTabs.CloseTab(EditorTabs.ActiveTab);
    }

    [RelayCommand]
    private void CloseTabItem(EditorTabViewModel? tab)
    {
        if (tab != null)
            EditorTabs.CloseTab(tab);
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        Output.Clear();
        ErrorList.Clear();

        if (SolutionTree.CurrentSolution != null)
            await BuildService.BuildSolutionAsync(SolutionTree.CurrentSolution);
        else if (SolutionTree.CurrentProject != null)
            await BuildService.BuildProjectAsync(SolutionTree.CurrentProject);
        else
            Output.AppendLine("error: 请先打开解决方案或项目");
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        var exeProject = ResolveExecutableProject();
        if (exeProject == null) return;

        Output.Clear();
        ErrorList.Clear();
        await BuildService.RunAsync(exeProject);
    }

    private CocoaProjectFile? ResolveExecutableProject()
    {
        if (SolutionTree.CurrentSolution != null)
        {
            var executables = SolutionTree.Projects
                .Where(p => p.Output == ProjectOutputFormat.Exe)
                .ToList();
            if (executables.Count == 1)
                return executables[0];
            if (executables.Count == 0)
                Output.AppendLine("error: 解决方案中没有可执行项目");
            else
                Output.AppendLine("error: 解决方案有多个可执行项目；请单独打开一个项目运行");
            return null;
        }

        if (SolutionTree.CurrentProject != null)
        {
            if (SolutionTree.CurrentProject.Output != ProjectOutputFormat.Exe)
                Output.AppendLine("error: 当前项目不是可执行项目");
            return SolutionTree.CurrentProject;
        }

        Output.AppendLine("error: 请先打开解决方案或项目");
        return null;
    }
}