using Avalonia.Media;

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

    /// <summary>模板种类 →（图标, 强调色）。</summary>
    public static (Geometry Geometry, IBrush Brush) ForTemplate(string templateKey) => templateKey switch
    {
        "console" => (Get(Project), new SolidColorBrush(Color.Parse("#4EC9B0"))),
        "csharp" => (Get(File), new SolidColorBrush(Color.Parse("#9B4F96"))),
        "library" => (Get(Project), new SolidColorBrush(Color.Parse("#DCDCAA"))),
        "cocoa" => (Get(Project), new SolidColorBrush(Color.Parse("#E37933"))),
        "solution" => (Get(Solution), new SolidColorBrush(Color.Parse("#007ACC"))),
        _ => (Get(File), new SolidColorBrush(Color.Parse("#CCCCCC"))),
    };
}
