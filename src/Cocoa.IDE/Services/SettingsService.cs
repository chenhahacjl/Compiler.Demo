using System.Text.Json;
using Cocoa.IDE.Models;

namespace Cocoa.IDE.Services;

/// <summary>M6：IDE 设置读写单例。路径 %LOCALAPPDATA%\Cocoa\IDE\settings.json，
/// 容错读取（缺失/损坏回退默认）、原子写入（临时文件替换）。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public static SettingsService Current { get; } = new();

    public IdeSettings Settings { get; private set; } = new();

    private SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cocoa", "IDE");
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    public string FilePath => _path;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Settings = JsonSerializer.Deserialize<IdeSettings>(File.ReadAllText(_path), JsonOptions)
                           ?? new IdeSettings();
        }
        catch
        {
            Settings = new IdeSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 设置写入失败不致命（磁盘占用/权限）
        }
    }

    // ─── 最近列表维护 ───

    public void AddRecent(List<RecentEntry> list, string path, int max = 10)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = list.FirstOrDefault(e => PathEquals(e.Path, path));
        if (existing != null)
        {
            existing.LastOpenedUtc = DateTime.UtcNow;
        }
        else
        {
            list.Add(new RecentEntry { Path = path, LastOpenedUtc = DateTime.UtcNow });
        }

        var pinned = list.Where(e => e.Pinned).ToList();
        var recent = list.Where(e => !e.Pinned)
                         .OrderByDescending(e => e.LastOpenedUtc)
                         .Take(max)
                         .ToList();

        list.Clear();
        list.AddRange(pinned.Concat(recent));
        Save();
    }

    public void RemoveRecent(List<RecentEntry> list, string path)
    {
        list.RemoveAll(e => PathEquals(e.Path, path));
        Save();
    }

    public void TogglePin(List<RecentEntry> list, string path)
    {
        var entry = list.FirstOrDefault(e => PathEquals(e.Path, path));
        if (entry == null) return;
        entry.Pinned = !entry.Pinned;
        Save();
    }

    public bool IsPinned(List<RecentEntry> list, string path)
        => list.Any(e => e.Pinned && PathEquals(e.Path, path));

    private static bool PathEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
