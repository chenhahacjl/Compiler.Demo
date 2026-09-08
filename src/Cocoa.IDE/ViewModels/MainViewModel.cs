using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public SolutionTreeViewModel SolutionTree { get; } = new();
    public EditorTabsViewModel EditorTabs { get; } = new();
    public ErrorListViewModel ErrorList { get; } = new();
    public OutputViewModel Output { get; } = new();
    public StatusBarViewModel StatusBar { get; } = new();

    private Window? MainWindow => App.Current?.ApplicationLifetime is
        IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (MainWindow == null) return;

        var files = await MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cocoa 文件") { Patterns = new[] { "*.co", "*.cs" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
                EditorTabs.OpenFile(path);
        }
    }

    [RelayCommand]
    private async Task OpenSolutionPickerAsync()
    {
        if (MainWindow == null) return;

        var files = await MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开解决方案",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cocoa 解决方案") { Patterns = new[] { "*.cosln", "*.coproj" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null)
                SolutionTree.LoadPath(path);
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (EditorTabs.ActiveTab == null) return;
        var tab = EditorTabs.ActiveTab;
        File.WriteAllText(tab.FilePath, tab.Content);
        tab.MarkSaved();
    }

    [RelayCommand]
    private void SaveAll()
    {
        foreach (var tab in EditorTabs.Tabs)
        {
            if (tab.IsModified)
            {
                File.WriteAllText(tab.FilePath, tab.Content);
                tab.MarkSaved();
            }
        }
    }

    [RelayCommand]
    private void CloseTab()
    {
        if (EditorTabs.ActiveTab != null)
            EditorTabs.CloseTab(EditorTabs.ActiveTab);
    }
}
