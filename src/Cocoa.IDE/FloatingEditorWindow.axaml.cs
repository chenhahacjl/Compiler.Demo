using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cocoa.IDE.Controls;
using Cocoa.IDE.ViewModels;

namespace Cocoa.IDE;

public partial class FloatingEditorWindow : Window
{
    public EditorTabsViewModel ViewTabs { get; } = new();

    private bool _closingConfirmed;
    private readonly Action<EditorTabViewModel, int, int> _navigateHandler;

    public FloatingEditorWindow()
    {
        InitializeComponent();
        Pane.AttachTabs(ViewTabs);

        // 浮窗内容变化同样触发实时诊断（与主窗口共享 MainViewModel.DiagnosticService）
        Pane.TextEdited += tab => OnPaneTextEdited(tab);

        // 错误列表等请求定位：若标签在本窗口则定位
        _navigateHandler = (tab, line, col) =>
        {
            if (ViewTabs.Tabs.Contains(tab))
                Pane.NavigateTo(tab, line, col);
        };
        EditorTabsRegistry.NavigateRequested += _navigateHandler;

        // F12 跳转定义：目标为其它窗口已打开文件则由注册表分发；否则由主窗口打开定位
        Pane.NavigationRequested += target =>
        {
            if (target == null) return;

            var main = MainViewModel.Shared;
            if (main == null) return;

            var own = ViewTabs.ActiveTab;
            var existing = main.FindOpenTabViewModel(target.FilePath);

            if (own != null && existing != null && ViewTabs.Tabs.Contains(existing))
            {
                // 目标就在本窗口 → 直接定位
                Pane.NavigateTo(existing, target.Line, target.Column);
            }
            else if (existing != null)
            {
                // 目标在其它窗口 → 广播定位
                EditorTabsRegistry.RequestNavigate(existing, target.Line, target.Column);
            }
            else
            {
                // 未打开 → 主窗口打开并定位
                main.OpenFile(target.FilePath);
                var tab = main.EditorTabs.ActiveTab;
                if (tab != null)
                    EditorTabsRegistry.RequestNavigate(tab, target.Line, target.Column);
            }
        };

        Closing += OnWindowClosing;
        Closed += (_, _) => DetachRegistry();
    }

    /// <summary>窗口关闭后清理静态注册表引用，避免已关闭窗口无法回收。</summary>
    private void DetachRegistry()
    {
        EditorTabsRegistry.NavigateRequested -= _navigateHandler;
        Pane.Detach();
        ViewTabs.DisposeSet();
    }

    /// <summary>由 MainWindow 在拖出标签后调用：把标签挂进本窗口并显示。</summary>
    public void AddTab(EditorTabViewModel tab)
    {
        ViewTabs.Tabs.Add(tab);
        ViewTabs.ActiveTab = tab;
        Title = tab.FileName;
    }

    private void OnPaneTextEdited(EditorTabViewModel tab)
    {
        if (tab.Dialect != null && MainViewModel.Shared != null)
            MainViewModel.Shared.DiagnosticService.TextChanged(tab.FilePath, tab.Content, tab.Dialect);
    }

    /// <summary>关闭浮窗：未保存修改需确认；结束后把剩余标签交还主窗口。</summary>
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;
        if (MainViewModel.Shared == null) return;

        var dirty = ViewTabs.Tabs.Where(t => t.IsModified).ToList();
        if (dirty.Count > 0)
        {
            // 先取消默认关闭，弹确认框后再关闭
            e.Cancel = true;

            var names = string.Join(", ", dirty.Select(t => t.FileName));
            var choice = await ShowCloseConfirmAsync(names);
            if (choice == null) return; // 取消 → 保持不关闭

            if (choice == true)
            {
                foreach (var tab in dirty)
                {
                    try { File.WriteAllText(tab.FilePath, tab.Content); tab.MarkSaved(); }
                    catch { /* 忽略个别保存失败 */ }
                }
            }
        }

        // 交还主窗口：把本窗口剩余标签移回主窗口标签集合
        var mainTabs = MainViewModel.Shared.EditorTabs;
        foreach (var tab in ViewTabs.Tabs.ToList())
        {
            if (!mainTabs.Tabs.Contains(tab))
                mainTabs.Tabs.Add(tab);
        }

        ViewTabs.DisposeSet();
        _closingConfirmed = true;
        Close();
    }

    /// <summary>三态确认。ShowDialog 是异步的，不能用同步方式取 Result。</summary>
    private Task<bool?> ShowCloseConfirmAsync(string names)
    {
        var dialog = new SavePromptWindow(names);
        var result = dialog.ShowDialog<bool?>(this);
        return result;
    }
}