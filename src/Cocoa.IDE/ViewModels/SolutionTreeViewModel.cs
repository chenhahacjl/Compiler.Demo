using System.Collections.ObjectModel;
using Avalonia.Media;
using Cocoa.Build;
using Cocoa.IDE.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class SolutionTreeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _solutionName = "（无解决方案）";

    [ObservableProperty]
    private bool _hasSolution;

    /// <summary>当前选中节点（TwoWay 绑定 TreeView.SelectedItem）。</summary>
    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    /// <summary>「显示所有文件」：展示未被项目源 glob 覆盖的磁盘文件。</summary>
    [ObservableProperty]
    private bool _showAllFiles;

    /// <summary>「与活动文档同步」：活动标签切换时选中对应树节点。</summary>
    [ObservableProperty]
    private bool _autoSync;

    public CocoaSolutionFile? CurrentSolution { get; private set; }
    public CocoaProjectFile? CurrentProject { get; private set; }
    public List<CocoaProjectFile> Projects { get; } = new();

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    public event Action<string>? FileActivated;

    private string? _loadedPath;
    private readonly Dictionary<string, TreeNodeViewModel> _pathIndex = new(StringComparer.OrdinalIgnoreCase);

    partial void OnShowAllFilesChanged(bool value) => Refresh();

    /// <summary>重新加载当前打开的解决方案/项目/文件夹（新建项目后刷新树）。</summary>
    public void Refresh()
    {
        if (_loadedPath != null)
            LoadPath(_loadedPath);
    }

    public void LoadPath(string path)
    {
        _loadedPath = path;
        RootNodes.Clear();
        _pathIndex.Clear();
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
        SolutionName = CurrentSolution != null
            ? (CurrentSolution.Name ?? Path.GetFileNameWithoutExtension(CurrentSolution.FilePath))
            : CurrentProject?.AssemblyName ?? "";
        RebuildIndex();
    }

    private void LoadSolution(string solutionPath)
    {
        if (!File.Exists(solutionPath)) return;

        CurrentSolution = CocoaSolutionFile.Load(solutionPath);

        // 先建项目节点，再据实际加载数生成解决方案标题
        var projectNodes = new List<TreeNodeViewModel>();
        foreach (var projectRelative in CurrentSolution.ProjectPaths)
        {
            var projectPath = Path.IsPathRooted(projectRelative)
                ? projectRelative
                : Path.GetFullPath(Path.Combine(CurrentSolution.Directory, projectRelative));
            var node = LoadProjectInto(projectPath);
            if (node != null) projectNodes.Add(node);
        }

        var name = CurrentSolution.Name ?? Path.GetFileNameWithoutExtension(solutionPath);
        var solutionNode = new TreeNodeViewModel(
            name, true, NodeKind.Solution, $"解决方案 '{name}' ({projectNodes.Count} 个项目)")
        {
            FullPath = solutionPath,
            IsExpanded = true,
        };
        foreach (var node in projectNodes)
        {
            node.Parent = solutionNode;
            solutionNode.Children.Add(node);
        }

        RootNodes.Add(solutionNode);
    }

    private void LoadProject(string projectPath)
    {
        if (!File.Exists(projectPath)) return;
        var node = LoadProjectInto(projectPath);
        if (node != null) RootNodes.Add(node);
    }

    private TreeNodeViewModel? LoadProjectInto(string projectPath)
    {
        if (!File.Exists(projectPath)) return null;

        CocoaProjectFile project;
        try
        {
            project = CocoaProjectFile.Load(projectPath);
        }
        catch
        {
            return null; // 损坏的 .coproj 跳过
        }

        CurrentProject ??= project;
        Projects.Add(project);

        var node = new TreeNodeViewModel(project.Name, true, NodeKind.Project)
        {
            FullPath = projectPath,
            IsExpanded = true,
        };
        BuildProjectChildren(node, project);
        return node;
    }

    private void BuildProjectChildren(TreeNodeViewModel projectNode, CocoaProjectFile project)
    {
        // 引用节点
        var referencesNode = new TreeNodeViewModel("引用", true, NodeKind.Dependencies) { IsExpanded = false };
        foreach (var reference in project.References)
        {
            var abs = Path.IsPathRooted(reference)
                ? reference
                : Path.GetFullPath(Path.Combine(project.Directory, reference));
            var refNode = new TreeNodeViewModel(Path.GetFileName(reference), false, NodeKind.Reference) { FullPath = abs };
            refNode.Parent = referencesNode;
            referencesNode.Children.Add(refNode);
        }
        referencesNode.Parent = projectNode;
        projectNode.Children.Add(referencesNode);

        // 源文件：按磁盘目录嵌套
        var expansion = Glob.Expand(project.SourcePatterns, project.Directory);
        var included = new HashSet<string>(expansion.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in expansion.Files)
            AddSourceByRelativePath(projectNode, project.Directory, file, isPhantom: false);

        if (ShowAllFiles && Directory.Exists(project.Directory))
        {
            foreach (var file in Directory.EnumerateFiles(project.Directory, "*", SearchOption.AllDirectories))
            {
                if (included.Contains(file)) continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".co" or ".cs" or ".cod" or ".coproj" or ".cosln")) continue;
                AddSourceByRelativePath(projectNode, project.Directory, file, isPhantom: true);
            }
        }
    }

    private void AddSourceByRelativePath(TreeNodeViewModel projectNode, string root, string file, bool isPhantom)
    {
        var relative = Path.GetRelativePath(root, file);
        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = projectNode;
        var currentDir = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];
            var folderPath = Path.Combine(currentDir, folderName);
            var folder = current.Children.FirstOrDefault(c => c.Kind == NodeKind.Folder && c.Name == folderName);
            if (folder == null)
            {
                folder = new TreeNodeViewModel(folderName, true, NodeKind.Folder) { FullPath = folderPath };
                folder.Parent = current;
                current.Children.Add(folder);
            }
            current = folder;
            currentDir = folderPath;
        }

        var leaf = new TreeNodeViewModel(segments[^1], false, NodeKind.Source, isPhantom: isPhantom) { FullPath = file };
        leaf.Parent = current;
        current.Children.Add(leaf);
    }

    private void RebuildIndex()
    {
        _pathIndex.Clear();
        foreach (var root in RootNodes)
            Index(root);
    }

    private void Index(TreeNodeViewModel node)
    {
        if (node.FullPath != null)
            _pathIndex[node.FullPath] = node;
        foreach (var child in node.Children)
            Index(child);
    }

    private void LoadFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath);
        var node = new TreeNodeViewModel(name, true, NodeKind.Folder) { FullPath = folderPath, IsExpanded = true };
        RootNodes.Add(node);
        PopulateFolder(node, folderPath);
    }

    private void PopulateFolder(TreeNodeViewModel parent, string folderPath)
    {
        foreach (var dir in Directory.EnumerateDirectories(folderPath))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var child = new TreeNodeViewModel(Path.GetFileName(dir), true, NodeKind.Folder) { FullPath = dir };
            child.Parent = parent;
            parent.Children.Add(child);
            PopulateFolder(child, dir);
        }

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".co" or ".cs" or ".cod" or ".coproj" or ".cosln")
            {
                var child = new TreeNodeViewModel(Path.GetFileName(file), false, NodeKind.Source) { FullPath = file };
                child.Parent = parent;
                parent.Children.Add(child);
            }
        }
    }

    /// <summary>折叠全部节点（保留根级展开由用户决定）。</summary>
    public void CollapseAll()
    {
        foreach (var root in RootNodes)
            Collapse(root);
    }

    private static void Collapse(TreeNodeViewModel node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children)
            Collapse(child);
    }

    /// <summary>与活动文档同步：选中并展开对应节点。</summary>
    public void SyncToFile(string filePath)
    {
        if (!_pathIndex.TryGetValue(filePath, out var node)) return;

        var parent = node.Parent;
        while (parent != null)
        {
            parent.IsExpanded = true;
            parent = parent.Parent;
        }
        SelectedNode = node;
    }

    [RelayCommand]
    private void Activate(TreeNodeViewModel node)
    {
        if (node.Kind == NodeKind.Source && node.FullPath != null)
            FileActivated?.Invoke(node.FullPath);
    }

    /// <summary>请求在指定节点所在目录新建源文件（由视图弹输入框）。</summary>
    public event Action<string>? NewFileRequested;

    /// <summary>请求从项目移除某源文件（由视图确认并处理）。</summary>
    public event Action<TreeNodeViewModel>? RemoveRequested;

    /// <summary>请求添加引用（由视图弹文件选择框）。</summary>
    public event Action<TreeNodeViewModel>? AddReferenceRequested;

    /// <summary>请求移除引用（由视图确认）。</summary>
    public event Action<TreeNodeViewModel>? RemoveReferenceRequested;

    public void RequestNewFile(TreeNodeViewModel node)
    {
        var dir = NodeDirectory(node);
        if (dir != null)
            NewFileRequested?.Invoke(dir);
    }

    public void RequestRemove(TreeNodeViewModel node)
    {
        if (node.Kind == NodeKind.Source && node.FullPath != null)
            RemoveRequested?.Invoke(node);
    }

    public void RequestAddReference(TreeNodeViewModel node) => AddReferenceRequested?.Invoke(node);

    public void RequestRemoveReference(TreeNodeViewModel node) => RemoveReferenceRequested?.Invoke(node);

    /// <summary>把引用写入节点所属项目的 .coproj，成功后刷新树。</summary>
    public bool AddReferenceToProject(TreeNodeViewModel node, string referencePath)
    {
        var projectPath = ResolveOwningProjectPath(node);
        if (projectPath == null) return false;
        if (!ProjectFileService.AddReference(projectPath, referencePath, out _)) return false;
        Refresh();
        return true;
    }

    /// <summary>从节点所属项目移除该引用，成功后刷新树。</summary>
    public bool RemoveReferenceFromProject(TreeNodeViewModel node)
    {
        if (node.FullPath == null) return false;
        var projectPath = ResolveOwningProjectPath(node);
        if (projectPath == null) return false;
        if (!ProjectFileService.RemoveReference(projectPath, node.FullPath, out _)) return false;
        Refresh();
        return true;
    }

    private string? ResolveOwningProjectPath(TreeNodeViewModel node)
    {
        var current = node;
        while (current != null)
        {
            if (current.Kind == NodeKind.Project) return current.FullPath;
            current = current.Parent;
        }
        return CurrentProject?.FilePath;
    }

    private static string? NodeDirectory(TreeNodeViewModel node)
    {
        if (node.FullPath == null) return null;
        if (node.Kind == NodeKind.Folder && Directory.Exists(node.FullPath)) return node.FullPath;
        if (node.Kind == NodeKind.Project) return Path.GetDirectoryName(node.FullPath);
        var dir = Directory.Exists(node.FullPath) ? node.FullPath : Path.GetDirectoryName(node.FullPath);
        return dir != null && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>为指定文件构建工程上下文（源文件集 + 引用集），供语义/诊断多文件编译使用。
    /// 优先匹配文件所在项目；无项目（打开文件夹/单文件）时回退到当前项目或同目录扫描。</summary>
    public ProjectContext? GetContext(string filePath)
    {
        var project = ResolveProjectFor(filePath);
        if (project != null)
        {
            var sources = Glob.Expand(project.SourcePatterns, project.Directory).Files
                .Concat(new[] { filePath })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var references = project.References
                .Select(r => Path.IsPathRooted(r) ? r : Path.GetFullPath(Path.Combine(project.Directory, r)))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ProjectContext(sources, references);
        }

        // 无工程：仅当前目录内的源文件，无引用
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var siblings = Directory.EnumerateFiles(dir, "*.co", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!siblings.Contains(filePath, StringComparer.OrdinalIgnoreCase))
            siblings.Add(filePath);

        return new ProjectContext(siblings, Array.Empty<string>());
    }

    private CocoaProjectFile? ResolveProjectFor(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null)
        {
            var match = Projects.FirstOrDefault(p =>
                string.Equals(Path.GetFullPath(p.Directory), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return CurrentProject;
    }
}

public enum NodeKind { Solution, Project, Folder, Source, Reference, Dependencies }

public partial class TreeNodeViewModel : ObservableObject
{
    public string Name { get; }

    /// <summary>树中显示文本（默认同 Name，解决方案节点为带计数的标题）。</summary>
    public string Caption { get; }

    public bool IsExpandable { get; }
    public NodeKind Kind { get; }
    public string? FullPath { get; set; }

    /// <summary>「显示所有文件」下的非项目包含项。</summary>
    public bool IsPhantom { get; }

    public TreeNodeViewModel? Parent { get; set; }
    public Geometry? Icon { get; }
    public IBrush? IconBrush { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public TreeNodeViewModel(string name, bool isExpandable, NodeKind kind,
        string? caption = null, bool isPhantom = false)
    {
        Name = name;
        IsExpandable = isExpandable;
        Kind = kind;
        Caption = caption ?? name;
        IsPhantom = isPhantom;
        (Icon, IconBrush) = Icons.ForNode(kind, name);
    }

    public bool IsSource => Kind == NodeKind.Source;
    public bool IsSolution => Kind == NodeKind.Solution;
    public bool IsProject => Kind == NodeKind.Project;
    public bool IsFolder => Kind == NodeKind.Folder;
    public bool IsReferenceNode => Kind == NodeKind.Reference;
    public bool HasPath => FullPath != null;

    /// <summary>可在其下新建文件（项目/文件夹）。</summary>
    public bool CanCreateFile => Kind is NodeKind.Project or NodeKind.Folder;

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
