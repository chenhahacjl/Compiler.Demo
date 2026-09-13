namespace Cocoa.IDE.Models;

/// <summary>M6：IDE 持久化设置（%LOCALAPPDATA%\Cocoa\IDE\settings.json）。</summary>
public sealed class IdeSettings
{
    /// <summary>"Dark" / "Light"。</summary>
    public string ThemeVariant { get; set; } = "Dark";

    public string EditorFontFamily { get; set; } = "Cascadia Code, Consolas, Courier New";

    public double EditorFontSize { get; set; } = 14;

    /// <summary>启动时是否弹出「最近/固定项目」窗口。</summary>
    public bool ShowStartDialog { get; set; } = true;

    public List<RecentEntry> RecentProjects { get; set; } = new();

    public List<RecentEntry> RecentFiles { get; set; } = new();
}

/// <summary>最近打开的解决方案/项目/文件（弹窗与「文件 → 最近」共用）。</summary>
public sealed class RecentEntry
{
    public string Path { get; set; } = "";

    public bool Pinned { get; set; }

    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}
