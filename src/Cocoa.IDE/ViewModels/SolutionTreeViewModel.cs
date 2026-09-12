using System.Collections.ObjectModel;
using Cocoa.Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class SolutionTreeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _solutionName = "（无解决方案）";

    [ObservableProperty]
    private bool _hasSolution;

    public CocoaSolutionFile? CurrentSolution { get; private set; }
    public CocoaProjectFile? CurrentProject { get; private set; }
    public List<CocoaProjectFile> Projects { get; } = new();

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    public event Action<string>? FileActivated;

    /// <summary>重新加载当前打开的解决方案/项目/文件夹（新建项目后刷新树）。</summary>
    public void Refresh()
    {
        if (_loadedPath != null)
            LoadPath(_loadedPath);
    }

    private string? _loadedPath;

    public void LoadPath(string path)
    {
        _loadedPath = path;
        RootNodes.Clear();
        CurrentSolution = null;
        CurrentProject = null;
        Projects.Clear();

        if (path.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            LoadSolution(path);
        else if (path.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase))
            LoadProject(path);
        else if (Directory.Exists(path))
            LoadFolder(path);
        else
        {
            HasSolution = false;
            SolutionName = "（无解决方案）";
            return;
        }

        HasSolution = RootNodes.Count > 0;
        SolutionName = CurrentSolution?.Name ?? CurrentProject?.AssemblyName ?? "";
    }

    private void LoadSolution(string solutionPath)
    {
        if (!File.Exists(solutionPath)) return;

        CurrentSolution = CocoaSolutionFile.Load(solutionPath);
        var solutionNode = new TreeNodeViewModel(CurrentSolution.Name ?? Path.GetFileNameWithoutExtension(solutionPath), true, NodeKind.Solution)
        {
            FullPath = solutionPath
        };

        foreach (var projectRelative in CurrentSolution.ProjectPaths)
        {
            var projectPath = Path.IsPathRooted(projectRelative)
                ? projectRelative
                : Path.GetFullPath(Path.Combine(CurrentSolution.Directory, projectRelative));

            LoadProjectInto(solutionNode, projectPath);
        }

        RootNodes.Add(solutionNode);
    }

    private void LoadProject(string projectPath)
    {
        if (!File.Exists(projectPath)) return;
        LoadProjectInto(null, projectPath);
    }

    private void LoadProjectInto(TreeNodeViewModel? parent, string projectPath)
    {
        if (!File.Exists(projectPath)) return;

        CocoaProjectFile project;
        try
        {
            project = CocoaProjectFile.Load(projectPath);
        }
        catch
        {
            return; // 损坏的 .coproj 跳过
        }

        CurrentProject ??= project;
        Projects.Add(project);

        var node = new TreeNodeViewModel(Path.GetFileName(projectPath), true, NodeKind.Project)
        {
            FullPath = projectPath
        };
        (parent?.Children ?? RootNodes).Add(node);

        var expansion = Glob.Expand(project.SourcePatterns, project.Directory);
        foreach (var filePath in expansion.Files)
        {
            var rel = Path.GetRelativePath(project.Directory, filePath);
            var child = new TreeNodeViewModel(rel, false, NodeKind.Source) { FullPath = filePath };
            node.Children.Add(child);
        }
    }

    private void LoadFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath);
        var node = new TreeNodeViewModel(name, true, NodeKind.Folder) { FullPath = folderPath };
        RootNodes.Add(node);
        PopulateFolder(node, folderPath);
    }

    private void PopulateFolder(TreeNodeViewModel parent, string folderPath)
    {
        foreach (var dir in Directory.EnumerateDirectories(folderPath))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var child = new TreeNodeViewModel(Path.GetFileName(dir), true, NodeKind.Folder) { FullPath = dir };
            parent.Children.Add(child);
            PopulateFolder(child, dir);
        }

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".co" or ".cs" or ".cod" or ".coproj" or ".cosln")
            {
                parent.Children.Add(new TreeNodeViewModel(Path.GetFileName(file), false, NodeKind.Source) { FullPath = file });
            }
        }
    }

    [RelayCommand]
    private void Activate(TreeNodeViewModel node)
    {
        if (node.Kind == NodeKind.Source && node.FullPath != null)
            FileActivated?.Invoke(node.FullPath);
    }

    /// <summary>请求在指定目录新建源文件（由视图弹输入框）。</summary>
    public event Action<string>? NewFileRequested;

    /// <summary>请求从项目/文件夹移除某源文件（由视图确认并处理）。</summary>
    public event Action<TreeNodeViewModel>? RemoveRequested;

    public void RequestNewFile(TreeNodeViewModel parent)
    {
        var dir = parent.FullPath;
        if (dir == null) return;
        if (!Directory.Exists(dir) && File.Exists(dir))
            dir = Path.GetDirectoryName(dir);
        if (dir != null)
            NewFileRequested?.Invoke(dir);
    }

    public void RequestRemove(TreeNodeViewModel node)
    {
        if (node.Kind == NodeKind.Source && node.FullPath != null)
            RemoveRequested?.Invoke(node);
    }
}

public enum NodeKind { Solution, Project, Folder, Source }

public partial class TreeNodeViewModel : ObservableObject
{
    public string Name { get; }
    public bool IsExpandable { get; }
    public NodeKind Kind { get; }
    public string? FullPath { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public TreeNodeViewModel(string name, bool isExpandable, NodeKind kind)
    {
        Name = name;
        IsExpandable = isExpandable;
        Kind = kind;
    }

    /// <summary>是否源文件（可打开）。</summary>
    public bool IsSource => Kind == NodeKind.Source;

    /// <summary>复制完整路径到剪贴板。</summary>
    public void CopyFullPath()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        var main = Avalonia.Application.Current?.ApplicationLifetime as
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var win = main?.MainWindow is { } w
            ? Avalonia.Controls.TopLevel.GetTopLevel(w)
            : null;
        win?.Clipboard?.SetTextAsync(FullPath);
    }

    /// <summary>在系统资源管理器中显示。</summary>
    public void ShowInExplorer()
    {
        if (string.IsNullOrEmpty(FullPath)) return;
        var dir = Directory.Exists(FullPath) ? FullPath : Path.GetDirectoryName(FullPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
    }
}