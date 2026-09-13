using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cocoa.IDE;

/// <summary>内置矢量图标（24x24 填充路径），无外部资源、随主题/前景色渲染。</summary>
public static class Icons
{
    // 单闭合填充形状，避免多子路径填充异常
    public const string Solution = "M12 2 22 12 12 22 2 12Z";
    public const string Project = "M3 4H11L13 7H21V20H3Z";
    public const string Folder = "M3 5H10L12 8H21V19H3Z";
    public const string Reference = "M4 4H20V20H4Z";
    public const string File = "M6 2H15L20 7V22H6Z";

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static Geometry Get(string data)
    {
        if (Cache.TryGetValue(data, out var geometry)) return geometry;
        geometry = Geometry.Parse(data);
        Cache[data] = geometry;
        return geometry;
    }

    /// <summary>按当前主题取色（深色/浅色两套，保证浅色下图标可见）。</summary>
    private static bool IsLight => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    private static IBrush B(string dark, string light) => new SolidColorBrush(Color.Parse(IsLight ? light : dark));

    /// <summary>模板种类 →（图标, 强调色）。</summary>
    public static (Geometry Geometry, IBrush Brush) ForTemplate(string templateKey) => templateKey switch
    {
        "console" => (Get(Project), B("#4EC9B0", "#267F99")),
        "library" => (Get(Project), B("#DCDCAA", "#8A6D1B")),
        "cocoa" => (Get(Project), B("#E37933", "#B85C00")),
        "solution" => (Get(Solution), B("#007ACC", "#007ACC")),
        _ => (Get(File), B("#CCCCCC", "#555555")),
    };

    /// <summary>树节点种类 →（图标, 强调色）；源文件按扩展名区色。</summary>
    public static (Geometry Geometry, IBrush Brush) ForNode(ViewModels.NodeKind kind, string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return kind switch
        {
            ViewModels.NodeKind.Solution => (Get(Solution), B("#007ACC", "#007ACC")),
            ViewModels.NodeKind.Project => (Get(Project), B("#DCDCAA", "#8A6D1B")),
            ViewModels.NodeKind.Folder => (Get(Folder), B("#DCDCAA", "#8A6D1B")),
            ViewModels.NodeKind.Dependencies => (Get(Folder), B("#AAAAAA", "#777777")),
            ViewModels.NodeKind.Reference => (Get(Reference), B("#4EC9B0", "#267F99")),
            _ => ext switch
            {
                ".co" => (Get(File), B("#E37933", "#B85C00")),
                _ => (Get(File), B("#CCCCCC", "#555555")),
            },
        };
    }
}
