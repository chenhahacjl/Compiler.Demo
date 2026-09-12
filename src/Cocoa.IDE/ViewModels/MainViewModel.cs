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
    public PropertiesViewModel Properties { get; } = new();
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
        BuildService.RunFinished += code =>
            StatusBar.StatusText = code == 0 ? "运行结束（退出代码 0）" : $"运行结束（退出代码 {code}）";

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
            var existing = set.Tabs.FirstOrDefault(t => t.FilePath == path);
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
        if (tab.Dialect == null) return;
        var context = SolutionTree.GetContext(tab.FilePath);
        DiagnosticService.TextChanged(tab.FilePath, tab.Content, tab.Dialect, context);
    }

    private void OnActiveTabChanged()
    {
        var tab = EditorTabs.ActiveTab;
        if (tab == null)
        {
            StatusBar.ResetActiveDocument();
            Properties.Clear();
            return;
        }

        StatusBar.Language = tab.Dialect ?? "";
        StatusBar.CursorPosition = $"Ln {tab.CursorLine}, Col {tab.CursorColumn}";
        Properties.ShowDocument(tab);

        if (SolutionTree.AutoSync)
            SolutionTree.SyncToFile(tab.FilePath);
    }

    /// <summary>树节点选中 → 属性窗口自动填充。</summary>
    public void ShowNodeProperties(TreeNodeViewModel node)
    {
        if (node != null)
            Properties.ShowNode(node);
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
                OpenFile(path);
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
    private async Task CloseTabAsync()
    {
        if (EditorTabs.ActiveTab != null)
            await EditorTabs.RequestCloseAsync(EditorTabs.ActiveTab);
    }

    /// <summary>编辑动作请求（撤销/重做/剪切/复制/粘贴/全选），由视图转发到编辑器。</summary>
    public event Action<string>? EditActionRequested;

    [RelayCommand]
    private void Undo() => EditActionRequested?.Invoke("Undo");
    [RelayCommand]
    private void Redo() => EditActionRequested?.Invoke("Redo");
    [RelayCommand]
    private void Cut() => EditActionRequested?.Invoke("Cut");
    [RelayCommand]
    private void Copy() => EditActionRequested?.Invoke("Copy");
    [RelayCommand]
    private void Paste() => EditActionRequested?.Invoke("Paste");
    [RelayCommand]
    private void SelectAll() => EditActionRequested?.Invoke("SelectAll");
    [RelayCommand]
    private void Find() => EditActionRequested?.Invoke("Find");

    /// <summary>新建项目向导：弹对话框 → 生成工程 → 加载返回的解决方案。</summary>
    public async Task<NewProjectService.NewProjectResult?> ShowNewProjectDialog(Window owner)
    {
        var dialog = new NewProjectDialog();
        return await dialog.ShowDialog<NewProjectService.NewProjectResult?>(owner);
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (MainWindow == null) return;

        var result = await ShowNewProjectDialog(MainWindow);
        if (result == null) return;

        SolutionTree.LoadPath(result.SolutionPath);
        StatusBar.SolutionName = SolutionTree.SolutionName;

        Output.Clear();
        Output.AppendLine($"已创建：{string.Join(" / ", result.CreatedFiles.Select(Path.GetFileName))}");
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

    [RelayCommand]
    private async Task RebuildAsync()
    {
        Output.Clear();
        ErrorList.Clear();

        if (SolutionTree.CurrentSolution != null)
            await BuildService.BuildSolutionAsync(SolutionTree.CurrentSolution, noIncremental: true);
        else if (SolutionTree.CurrentProject != null)
            await BuildService.BuildProjectAsync(SolutionTree.CurrentProject, noIncremental: true);
        else
            Output.AppendLine("error: 请先打开解决方案或项目");
    }

    [RelayCommand]
    private void Clean()
    {
        Output.Clear();

        var projects = SolutionTree.CurrentSolution != null
            ? SolutionTree.Projects.ToList()
            : SolutionTree.CurrentProject != null
                ? new List<CocoaProjectFile> { SolutionTree.CurrentProject }
                : new List<CocoaProjectFile>();

        if (projects.Count == 0)
        {
            Output.AppendLine("error: 请先打开解决方案或项目");
            return;
        }

        foreach (var project in projects)
        {
            try
            {
                var outputDir = project.GetOutputDirectory();
                if (Directory.Exists(outputDir))
                {
                    Directory.Delete(outputDir, true);
                    Output.AppendLine($"已清理：{outputDir}");
                }
            }
            catch (Exception ex)
            {
                Output.AppendLine($"error: 清理失败 '{project.Name}': {ex.Message}");
            }
        }

        StatusBar.StatusText = "清理完成";
    }

    [RelayCommand]
    private void Stop() => BuildService.Stop();

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
            {
                Output.AppendLine("error: 当前项目不是可执行项目");
                return null;
            }
            return SolutionTree.CurrentProject;
        }

        Output.AppendLine("error: 请先打开解决方案或项目");
        return null;
    }
}