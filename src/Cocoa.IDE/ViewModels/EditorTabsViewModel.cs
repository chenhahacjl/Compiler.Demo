using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cocoa.IDE.ViewModels;

public partial class EditorTabsViewModel : ObservableObject
{
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

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
}