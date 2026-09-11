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

    public void LoadPath(string path)
    {
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
}