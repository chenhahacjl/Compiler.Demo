using System.Collections.ObjectModel;
using Cocoa.Build;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public sealed record PropertyItemViewModel(string Category, string Name, string Value);

/// <summary>VS 风格属性窗口：根据当前选中项（树节点/编辑器标签/错误）自动填充属性。</summary>
public partial class PropertiesViewModel : ObservableObject
{
    public ObservableCollection<PropertyItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private string _title = "属性";

    [ObservableProperty]
    private string? _selectedObjectName;

    public void Clear()
    {
        Items.Clear();
        Title = "属性";
        SelectedObjectName = null;
    }

    public void ShowNode(TreeNodeViewModel node)
    {
        Items.Clear();
        Title = "属性";
        SelectedObjectName = node.Name;

        Items.Add(new PropertyItemViewModel("常规", "名称", node.Name));
        Items.Add(new PropertyItemViewModel("常规", "类型", node.Kind switch
        {
            NodeKind.Solution => "解决方案",
            NodeKind.Project => "项目",
            NodeKind.Folder => "文件夹",
            NodeKind.Source => "源文件",
            _ => node.Kind.ToString(),
        }));
        Items.Add(new PropertyItemViewModel("常规", "完整路径", node.FullPath ?? ""));

        if (node.Kind == NodeKind.Project && node.FullPath != null && File.Exists(node.FullPath))
        {
            try
            {
                var p = CocoaProjectFile.Load(node.FullPath);
                Items.Add(new PropertyItemViewModel("项目", "语言", p.Language.ToString()));
                Items.Add(new PropertyItemViewModel("项目", "输出类型", p.Output.ToString()));
                Items.Add(new PropertyItemViewModel("项目", "平台", p.Platform));
                Items.Add(new PropertyItemViewModel("项目", "目标框架", p.DotnetRuntime ?? "(默认)"));
                Items.Add(new PropertyItemViewModel("项目", "配置", p.Configuration.ToString()));
                Items.Add(new PropertyItemViewModel("项目", "输出路径", p.GetOutputDirectory()));
                Items.Add(new PropertyItemViewModel("项目", "源文件数", p.SourcePatterns.Length.ToString()));
            }
            catch { /* 损坏项目忽略 */ }
        }

        if (node.Kind == NodeKind.Source && node.FullPath != null && File.Exists(node.FullPath))
        {
            var info = new FileInfo(node.FullPath);
            Items.Add(new PropertyItemViewModel("文件", "大小", $"{info.Length} 字节"));
            Items.Add(new PropertyItemViewModel("文件", "修改时间", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
            Items.Add(new PropertyItemViewModel("文件", "只读", info.IsReadOnly ? "是" : "否"));
            Items.Add(new PropertyItemViewModel("文件", "扩展名", info.Extension));
        }
    }

    public void ShowDocument(EditorTabViewModel tab)
    {
        Items.Clear();
        Title = "属性";
        SelectedObjectName = tab.FileName;

        Items.Add(new PropertyItemViewModel("文档", "文件名", tab.FileName));
        Items.Add(new PropertyItemViewModel("文档", "完整路径", tab.FilePath));
        Items.Add(new PropertyItemViewModel("文档", "方言", tab.Dialect ?? "(无)"));
        Items.Add(new PropertyItemViewModel("文档", "已修改", tab.IsModified ? "是" : "否"));
        Items.Add(new PropertyItemViewModel("文档", "光标", $"第 {tab.CursorLine} 行，第 {tab.CursorColumn} 列"));

        if (File.Exists(tab.FilePath))
        {
            var info = new FileInfo(tab.FilePath);
            Items.Add(new PropertyItemViewModel("文档", "大小", $"{info.Length} 字节"));
            Items.Add(new PropertyItemViewModel("文档", "修改时间", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
        }
    }

    public void ShowError(ErrorItemViewModel item)
    {
        Items.Clear();
        Title = "错误详情";
        SelectedObjectName = System.IO.Path.GetFileName(item.FilePath);

        Items.Add(new PropertyItemViewModel("错误", "严重级别", item.Severity.ToString()));
        Items.Add(new PropertyItemViewModel("错误", "消息", item.Message));
        Items.Add(new PropertyItemViewModel("错误", "文件", item.FilePath));
        Items.Add(new PropertyItemViewModel("错误", "位置", $"{item.Line}:{item.Column}"));
    }

    public void ShowDiagnostic(Cocoa.CodeAnalysis.Diagnostic diag)
    {
        Items.Clear();
        Title = "诊断详情";
        SelectedObjectName = System.IO.Path.GetFileName(diag.Location.FileName);

        Items.Add(new PropertyItemViewModel("诊断", "级别", diag.IsError ? "错误" : "警告"));
        Items.Add(new PropertyItemViewModel("诊断", "消息", diag.Message));
        Items.Add(new PropertyItemViewModel("诊断", "文件", diag.Location.FileName));
        Items.Add(new PropertyItemViewModel("诊断", "位置",
            $"{diag.Location.StartLine + 1}:{diag.Location.StartCharacter + 1}"));
    }
}