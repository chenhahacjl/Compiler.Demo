using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Cocoa.Build;
using Cocoa.CodeAnalysis;
using Cocoa.IDE.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>全局共享实例（浮窗等需要访问共享服务时使用）。</summary>
    public static MainViewModel? Shared { get; private set; }

    public SolutionTreeViewModel SolutionTree { get; } = new();
    public EditorTabsViewModel EditorTabs { get; } = new();
    public ErrorListViewModel ErrorList { get; } = new();
    public OutputViewModel Output { get; } = new();
    public StatusBarViewModel StatusBar { get; } = new();
    public BuildService BuildService { get; } = new();
    public DiagnosticService DiagnosticService { get; } = new();

    private Window? MainWindow => App.Current?.ApplicationLifetime is
        IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

    public MainViewModel()
    {
        Shared = this;

        SolutionTree.FileActivated += path => OpenFile(path);
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

        EditorTabs.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
            {
                foreach (EditorTabViewModel tab in e.OldItems)
                    DiagnosticService.CloseFile(tab.FilePath);
            }
        };

        // 实时诊断：派发到所有已注册的标签集合（主窗口 + 浮窗）
        DiagnosticService.DiagnosticsReady += (filePath, diagnostics) =>
        {
            foreach (var set in EditorTabsRegistry.All)
            {
                foreach (var tab in set.Tabs.Where(t => t.FilePath == filePath))
                    tab.Diagnostics = diagnostics;
            }

            // 若该文件正显示在主窗口，同步进错误列表
            if (ErrorList != null)
            {
                var active = EditorTabs.ActiveTab;
                if (active != null && active.FilePath == filePath)
                    ErrorList.ReplaceFile(filePath, diagnostics);
            }

            EditorTabsRegistry.PublishDiagnostics(filePath, diagnostics);
        };
    }

    /// <summary>实时诊断通过 <see cref="EditorTabsRegistry.PublishDiagnostics"/> 广播给各窗口。</summary>

    public void InitializeServices()
    {
        // 打开当前活动文件触发一次初始诊断
        if (EditorTabs.ActiveTab != null)
            Reanalyze(EditorTabs.ActiveTab);
    }

    public void OpenFile(string path)
    {
        // 若该文件已在任意窗口打开，直接激活对应标签
        foreach (var set in EditorTabsRegistry.All)
        {
            var existing = set.ActiveTab != null && set.Tabs.Any(t => t.FilePath == path)
                ? set.Tabs.First(t => t.FilePath == path)
                : null;
            if (existing != null)
            {
                set.Activate(existing);
                FileActivated?.Invoke(path);
                return;
            }
        }

        EditorTabs.OpenFile(path);
        var tab = EditorTabs.ActiveTab;
        if (tab != null && tab.FilePath == path)
            Reanalyze(tab);
    }

    /// <summary>通知主窗口激活某标签（供浮窗拖回/错误导航使用）。</summary>
    public event Action<string>? FileActivated;

    private void Reanalyze(EditorTabViewModel tab)
    {
        if (tab.Dialect != null)
            DiagnosticService.TextChanged(tab.FilePath, tab.Content, tab.Dialect);
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

        // 若文件已在某窗口打开则激活；否则在主窗口打开
        var tab = FindOpenTab(item.FilePath);
        if (tab == null)
        {
            OpenFile(item.FilePath);
            tab = EditorTabs.ActiveTab;
        }
        else
        {
            EditorTabsRegistry.RequestNavigate(tab, Math.Max(1, item.Line), Math.Max(1, item.Column));
            return;
        }

        if (tab != null)
            EditorTabsRegistry.RequestNavigate(tab, Math.Max(1, item.Line), Math.Max(1, item.Column));
    }

    private EditorTabViewModel? FindOpenTab(string filePath)
    {
        foreach (var set in EditorTabsRegistry.All)
        {
            var tab = set.Tabs.FirstOrDefault(t => t.FilePath == filePath);
            if (tab != null) return tab;
        }
        return null;
    }

    /// <summary>公开版：查找任意窗口已打开的文件标签（F12/错误跳转用）。</summary>
    public EditorTabViewModel? FindOpenTabViewModel(string filePath) => FindOpenTab(filePath);

    /// <summary>编辑器内容变化时触发实时诊断（视图在 TextChanged 时调用）。</summary>
    public void EditorTextChanged(EditorTabViewModel tab)
    {
        Reanalyze(tab);
    }

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

    /// <summary>新建项目向导：弹对话框 → 生成工程 → 刷新树。需要窗口宿主，由视图触发。</summary>
    public async Task<string[]?> ShowNewProjectDialog(Window owner)
    {
        var dialog = new NewProjectDialog();
        return await dialog.ShowDialog<string[]?>(owner);
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (MainWindow == null) return;

        var created = await ShowNewProjectDialog(MainWindow);
        if (created == null || created.Length == 0) return;

        // 若已打开解决方案/文件夹，刷新树；否则加载新生成的工程
        var coproj = created.FirstOrDefault(p => p.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase));
        var cosln = created.FirstOrDefault(p => p.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase));

        if (cosln != null)
        {
            SolutionTree.LoadPath(cosln);
            StatusBar.SolutionName = SolutionTree.SolutionName;
        }
        else if (coproj != null)
        {
            SolutionTree.Refresh();
            if (!SolutionTree.HasSolution)
            {
                SolutionTree.LoadPath(coproj);
                StatusBar.SolutionName = SolutionTree.SolutionName;
            }
        }
        else
        {
            SolutionTree.Refresh();
        }

        Output.Clear();
        Output.AppendLine($"已创建：{string.Join(" / ", created.Select(Path.GetFileName))}");
        StatusBar.StatusText = "新建项目完成";
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