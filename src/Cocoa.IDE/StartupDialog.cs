using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cocoa.IDE.Models;
using Cocoa.IDE.Services;

namespace Cocoa.IDE;

public enum StartupAction
{
    None,
    OpenRecent,
    NewProject,
    OpenSolution,
    OpenFile,
}

/// <summary>M6：启动「最近/固定项目」窗口。
/// 列出最近打开的 .cosln/.coproj，可双击打开、右键固定/移除/定位；并提供新建/打开入口。</summary>
public sealed class StartupDialog : Window
{
    private readonly ObservableCollection<RecentEntry> _entries = new();
    private readonly ListBox _listBox;

    public StartupAction Action { get; private set; } = StartupAction.None;

    public string? SelectedPath { get; private set; }

    public StartupDialog()
    {
        Title = "打开项目";
        Width = 640;
        Height = 460;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrushes.Brush("EditorBackgroundBrush", "#1E1E1E");

        _listBox = new ListBox
        {
            ItemsSource = _entries,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _listBox.ItemTemplate = new FuncDataTemplate<RecentEntry>((entry, _) => BuildRow(entry!));
        _listBox.DoubleTapped += (_, _) => OpenSelected();
        _listBox.ContextMenu = BuildContextMenu();

        // ── 顶部标题 ──
        var header = new TextBlock
        {
            Text = "最近使用的项目",
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(2, 0, 0, 8),
            Foreground = ThemeBrushes.Brush("TextBrush", "#CCCCCC"),
        };

        // ── 底部操作区 ──
        var newBtn = new Button { Content = "新建项目…", MinWidth = 110 };
        newBtn.Click += (_, _) => CloseWith(StartupAction.NewProject);

        var openSlnBtn = new Button { Content = "打开解决方案…", MinWidth = 120 };
        openSlnBtn.Click += (_, _) => CloseWith(StartupAction.OpenSolution);

        var openFileBtn = new Button { Content = "打开文件…", MinWidth = 100 };
        openFileBtn.Click += (_, _) => CloseWith(StartupAction.OpenFile);

        var closeBtn = new Button { Content = "关闭", MinWidth = 80 };
        closeBtn.Click += (_, _) => Close();

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(newBtn);
        buttonRow.Children.Add(openSlnBtn);
        buttonRow.Children.Add(openFileBtn);

        var showBox = new CheckBox
        {
            Content = "启动时显示此窗口",
            IsChecked = SettingsService.Current.Settings.ShowStartDialog,
        };
        showBox.IsCheckedChanged += (_, _) =>
        {
            SettingsService.Current.Settings.ShowStartDialog = showBox.IsChecked == true;
            SettingsService.Current.Save();
        };

        var bottomRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottomRow.Children.Add(showBox);
        Grid.SetColumn(closeBtn, 1);
        bottomRow.Children.Add(closeBtn);

        var bottom = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(buttonRow);
        bottom.Children.Add(bottomRow);

        var listBorder = new Border
        {
            Background = ThemeBrushes.Brush("PanelBrush", "#252526"),
            BorderBrush = ThemeBrushes.Brush("SeparatorBrush", "#3F3F46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = _listBox,
        };

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(listBorder);

        Content = root;

        RefreshList();
    }

    private void RefreshList()
    {
        _entries.Clear();
        var ordered = SettingsService.Current.Settings.RecentProjects
            .OrderByDescending(e => e.Pinned)
            .ThenByDescending(e => e.LastOpenedUtc);
        foreach (var entry in ordered)
            _entries.Add(entry);

        if (_entries.Count == 0)
            _entries.Add(new RecentEntry { Path = "（暂无最近项目，使用下方按钮新建或打开）" });
    }

    private Control BuildRow(RecentEntry entry)
    {
        var isPlaceholder = !entry.Path.Contains(Path.DirectorySeparatorChar) && !entry.Path.Contains('/');
        var exists = !isPlaceholder && File.Exists(entry.Path);

        var name = new TextBlock
        {
            Text = isPlaceholder ? entry.Path : Path.GetFileName(entry.Path),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrushes.Brush("TextBrush", "#CCCCCC"),
        };

        var path = new TextBlock
        {
            Text = exists
                ? entry.Path
                : (isPlaceholder ? "" : entry.Path + "  （找不到）"),
            FontSize = 11,
            Foreground = exists
                ? ThemeBrushes.Brush("SubtleTextBrush", "#999999")
                : ThemeBrushes.Brush("ErrorBrush", "#E51400"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(name);
        left.Children.Add(path);

        var pin = new TextBlock
        {
            Text = entry.Pinned ? "📌" : "",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Brush("SubtleTextBrush", "#999999"),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(pin, 1);
        grid.Children.Add(pin);
        return grid;
    }

    private ContextMenu BuildContextMenu()
    {
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => OpenSelected();

        var pin = new MenuItem { Header = "固定 / 取消固定" };
        pin.Click += (_, _) =>
        {
            if (Current is { } entry)
            {
                SettingsService.Current.TogglePin(SettingsService.Current.Settings.RecentProjects, entry.Path);
                RefreshList();
            }
        };

        var remove = new MenuItem { Header = "从列表移除" };
        remove.Click += (_, _) =>
        {
            if (Current is { } entry)
            {
                SettingsService.Current.RemoveRecent(SettingsService.Current.Settings.RecentProjects, entry.Path);
                RefreshList();
            }
        };

        var reveal = new MenuItem { Header = "在资源管理器中打开" };
        reveal.Click += (_, _) =>
        {
            if (Current is { } entry && File.Exists(entry.Path))
            {
                try { Process.Start("explorer.exe", $"/select,\"{entry.Path}\""); }
                catch { /* 忽略 */ }
            }
        };

        var copy = new MenuItem { Header = "复制路径" };
        copy.Click += async (_, _) =>
        {
            if (Current is { } entry && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(entry.Path);
        };

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(pin);
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        return menu;
    }

    private RecentEntry? Current => _listBox.SelectedItem as RecentEntry;

    private void OpenSelected()
    {
        if (Current is not { } entry || !File.Exists(entry.Path)) return;
        SelectedPath = entry.Path;
        SettingsService.Current.AddRecent(SettingsService.Current.Settings.RecentProjects, entry.Path);
        CloseWith(StartupAction.OpenRecent);
    }

    private void CloseWith(StartupAction action)
    {
        Action = action;
        Close(action);
    }
}
