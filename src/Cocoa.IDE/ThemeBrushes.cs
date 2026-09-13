using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cocoa.IDE;

/// <summary>M6：从应用主题资源取画刷（代码构建的对话框用），取不到时回退硬编码。</summary>
public static class ThemeBrushes
{
    public static IBrush Brush(string key, string fallback)
    {
        if (Application.Current is { } app &&
            app.TryFindResource(key, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}
