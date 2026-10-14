using System.Collections.ObjectModel;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Cocoa.IDE.LanguageServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

/// <summary>文档大纲面板：随活动标签内容刷新，展示符号树并支持点击跳转。</summary>
public partial class DocumentOutlineViewModel : ObservableObject
{
    public ObservableCollection<DocumentOutlineNode> Roots { get; } = new();

    /// <summary>大纲跳转请求（行号）。</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>内容或活动标签变化时刷新大纲。</summary>
    public void Refresh(string text, string? filePath)
    {
        Roots.Clear();

        if (string.IsNullOrWhiteSpace(text)) return;

        SyntaxTree tree;
        try
        {
            tree = SyntaxTree.Parse(SourceText.From(text, filePath ?? ""));
        }
        catch
        {
            return;
        }

        foreach (var item in DocumentOutlineProvider.Build(tree))
            Roots.Add(new DocumentOutlineNode(item));
    }

    public void NavigateTo(DocumentOutlineNode node)
    {
        if (node?.Line > 0) NavigateRequested?.Invoke(node.Line);
    }
}

/// <summary>大纲树节点（UI 绑定层，含子项展开）。</summary>
public sealed class DocumentOutlineNode
{
    public DocumentOutlineNode(DocumentOutlineItem item)
    {
        Name = item.Name;
        Kind = item.Kind;
        Line = item.Line;
        Column = item.Column;

        if (item.Children != null)
            foreach (var child in item.Children)
                Children.Add(new DocumentOutlineNode(child));
    }

    public string Name { get; }
    public OutlineKind Kind { get; }
    public int Line { get; }
    public int Column { get; }

    public ObservableCollection<DocumentOutlineNode> Children { get; } = new();

    public string KindText => Kind switch
    {
        OutlineKind.Namespace => "命名空间",
        OutlineKind.Class => "类",
        OutlineKind.Struct => "结构体",
        OutlineKind.Interface => "接口",
        OutlineKind.Enum => "枚举",
        OutlineKind.EnumMember => "枚举值",
        OutlineKind.Delegate => "委托",
        OutlineKind.Function => "函数",
        OutlineKind.Property => "属性",
        OutlineKind.Field => "字段",
        OutlineKind.Event => "事件",
        _ => "",
    };
}
