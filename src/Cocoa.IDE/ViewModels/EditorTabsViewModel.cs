using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cocoa.IDE.ViewModels;

public partial class EditorTabsViewModel : ObservableObject
{
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

    public EditorTabsViewModel()
    {
        EditorTabsRegistry.Register(this);
    }

    public void OpenFile(string filePath)
    {
        var existing = Tabs.FirstOrDefault(t => t.FilePath == filePath);
        if (existing != null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new EditorTabViewModel(filePath);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    public void Activate(EditorTabViewModel tab)
    {
        if (Tabs.Contains(tab))
            ActiveTab = tab;
    }

    public void CloseTab(EditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ActiveTab == tab)
        {
            // 优先选择原本相邻的右侧标签，否则左侧；无剩余则置空
            var next = index < Tabs.Count ? Tabs[index] : Tabs.LastOrDefault();
            ActiveTab = next;
        }
    }

    public void CloseAll()
    {
        Tabs.Clear();
        ActiveTab = null;
    }

    /// <summary>窗口关闭时调用，从全局注册表移除本集合。</summary>
    public void DisposeSet()
    {
        EditorTabsRegistry.Unregister(this);
    }

    [RelayCommand]
    private void Close(EditorTabViewModel? tab)
    {
        if (tab != null && Tabs.Contains(tab))
            CloseTab(tab);
    }
}