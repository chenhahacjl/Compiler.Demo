using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class SolutionTreeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _solutionName = "（无解决方案）";

    [ObservableProperty]
    private bool _hasSolution;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    public void LoadPath(string path)
    {
        RootNodes.Clear();

        if (path.EndsWith(".cosln", StringComparison.OrdinalIgnoreCase))
            LoadSolution(path);
        else if (path.EndsWith(".coproj", StringComparison.OrdinalIgnoreCase))
            LoadProject(path);
        else
            LoadFolder(path);

        HasSolution = RootNodes.Count > 0;
    }

    private void LoadSolution(string solutionPath)
    {
        var dir = System.IO.Path.GetDirectoryName(solutionPath)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(solutionPath);
        SolutionName = name;
        HasSolution = true;

        // 简易解析 .cosln 的 [projects] 节
        var node = new TreeNodeViewModel(name, true) { FullPath = dir };
        RootNodes.Add(node);

        foreach (var line in File.ReadLines(solutionPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith('['))
                continue;

            var projectRelative = trimmed.Split('#')[0].Trim();
            if (string.IsNullOrEmpty(projectRelative)) continue;

            var projectPath = System.IO.Path.Combine(dir, projectRelative.Replace('/', '\\'));
            if (File.Exists(projectPath))
                LoadProjectInto(node, projectPath);
            else if (Directory.Exists(projectPath))
                LoadFolderInto(node, projectPath);
        }
    }

    private void LoadProject(string projectPath)
    {
        var dir = System.IO.Path.GetDirectoryName(projectPath)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(projectPath);
        SolutionName = name;

        LoadProjectInto(null, projectPath);
    }

    private void LoadProjectInto(TreeNodeViewModel? parent, string projectPath)
    {
        var dir = System.IO.Path.GetDirectoryName(projectPath)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(projectPath);
        var node = new TreeNodeViewModel($"{name}.coproj", true) { FullPath = projectPath };
            (parent?.Children ?? RootNodes).Add(node);

        // 读 [sources] 节展开 glob
        var inSources = false;
        foreach (var line in File.ReadLines(projectPath))
        {
            var trimmed = line.Trim();
            if (trimmed == "[sources]") { inSources = true; continue; }
            if (trimmed.StartsWith('[')) { inSources = false; continue; }
            if (!inSources || string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var pattern = trimmed.Split('#')[0].Trim();
            if (string.IsNullOrEmpty(pattern)) continue;

            // 简易 glob 展开（支持 * 和 **）
            ExpandGlob(node, dir, pattern);
        }
    }

    private void ExpandGlob(TreeNodeViewModel parent, string baseDir, string pattern)
    {
        try
        {
            var dirPart = System.IO.Path.GetDirectoryName(pattern)?.Replace('\\', '/') ?? "";
            var filePart = System.IO.Path.GetFileName(pattern);
            var searchDir = string.IsNullOrEmpty(dirPart) ? baseDir : System.IO.Path.Combine(baseDir, dirPart);

            if (!Directory.Exists(searchDir)) return;

            if (filePart.Contains("**"))
            {
                foreach (var file in Directory.EnumerateFiles(searchDir, filePart.Replace("**\\", ""), System.IO.SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(baseDir, file);
                    parent.Children.Add(new TreeNodeViewModel(rel, false) { FullPath = file });
                }
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(searchDir, filePart))
                {
                    var rel = Path.GetRelativePath(baseDir, file);
                    parent.Children.Add(new TreeNodeViewModel(rel, false) { FullPath = file });
                }
            }
        }
        catch { /* glob 语法不支持时静默跳过 */ }
    }

    private void LoadFolderInto(TreeNodeViewModel parent, string folderPath)
    {
        var dirNode = new TreeNodeViewModel(System.IO.Path.GetFileName(folderPath), true) { FullPath = folderPath };
        parent.Children.Add(dirNode);
        LoadFolder(folderPath);
    }

    private void LoadFolder(string folderPath)
    {
        var name = System.IO.Path.GetFileName(folderPath);
        var node = new TreeNodeViewModel(name, true) { FullPath = folderPath };
        RootNodes.Add(node);

        foreach (var dir in Directory.EnumerateDirectories(folderPath))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var child = new TreeNodeViewModel(Path.GetFileName(dir), true) { FullPath = dir };
            node.Children.Add(child);
        }

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".co" or ".cs" or ".cod" or ".coproj" or ".cosln")
            {
                node.Children.Add(new TreeNodeViewModel(Path.GetFileName(file), false) { FullPath = file });
            }
        }
    }
}

public partial class TreeNodeViewModel : ObservableObject
{
    public string Name { get; }
    public bool IsExpandable { get; }
    public string? FullPath { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public TreeNodeViewModel(string name, bool isExpandable)
    {
        Name = name;
        IsExpandable = isExpandable;
    }
}
