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
        // 避免重复打开
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

    public void CloseTab(EditorTabViewModel tab)
    {
        Tabs.Remove(tab);
        ActiveTab = Tabs.LastOrDefault();
    }
}
