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

    /// <summary>关闭前的确认回调（返回 true 表示继续关闭）；由持有窗口的 EditorPane 设置。</summary>
    public Func<EditorTabViewModel, Task<bool>>? ConfirmClose { get; set; }

    /// <summary>请求关闭标签：脏标签先经 <see cref="ConfirmClose"/> 确认，再移除。</summary>
    public async Task RequestCloseAsync(EditorTabViewModel? tab)
    {
        if (tab == null || !Tabs.Contains(tab)) return;
        if (tab.IsModified && ConfirmClose != null && !await ConfirmClose(tab)) return;
        CloseTab(tab);
    }

    [RelayCommand]
    private async Task CloseAsync(EditorTabViewModel? tab) => await RequestCloseAsync(tab);
}