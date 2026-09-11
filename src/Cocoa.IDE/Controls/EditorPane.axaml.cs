using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cocoa.CodeAnalysis;
using Cocoa.IDE.ViewModels;

namespace Cocoa.IDE.Controls;

public partial class EditorPane : UserControl
{
    private bool _syncingEditor;

    /// <summary>绑定到 EditorTabsViewModel（外部赋值，通常是 MainViewModel.EditorTabs 或浮窗新建的）。</summary>
    public EditorTabsViewModel? EditorTabs { get; set; }

    /// <summary>编辑器内容变化（已同步回 tab.Content），供外部触发实时诊断。</summary>
    public event Action<EditorTabViewModel>? TextEdited;

    /// <summary>光标位置变化，供外部更新状态栏。</summary>
    public event Action<EditorTabViewModel>? CaretMoved;

    /// <summary>标签被拖出（detach），参数是被拖出的标签。</summary>
    public event Action<EditorTabViewModel>? TabDetached;

    public EditorPane()
    {
        InitializeComponent();
        EditorHost.TextChanged += (_, _) => OnEditorTextChanged();
        EditorHost.CaretChanged += (_, _) => OnEditorCaretChanged();

        // 跨窗口诊断广播：若命中的文件是本窗活动标签，重绘波浪线
        EditorTabsRegistry.DiagnosticsApplied += (filePath, diagnostics) =>
        {
            var active = EditorTabs?.ActiveTab;
            if (active != null && active.FilePath == filePath)
                UpdateSquiggles(active);
        };
    }

    public void AttachTabs(EditorTabsViewModel tabs)
    {
        EditorTabs = tabs;
        DataContext = tabs;

        if (TabList != null)
            TabList.SelectedItem = tabs.ActiveTab;

        tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorTabsViewModel.ActiveTab))
                OnActiveTabChanged();
        };
        OnActiveTabChanged();
    }

    /// <summary>外部请求定位（错误列表双击等）。</summary>
    public void NavigateTo(EditorTabViewModel tab, int line, int col)
    {
        EditorTabs?.Activate(tab);
        OnActiveTabChanged();
        EditorHost.SetCaret(line, col);
        EditorHost.Focus();
    }

    /// <summary>更新当前活动标签的波浪线（由外部诊断结果触发）。</summary>
    public void UpdateSquiggles(EditorTabViewModel tab)
    {
        if (EditorTabs?.ActiveTab != tab) return;
        EditorHost.SetDiagnostics(tab.Diagnostics);
    }

    private void OnActiveTabChanged()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null)
        {
            _syncingEditor = false;
            EditorHost.LoadText("", null, null);
            UpdateEmptyState();
            return;
        }

        _syncingEditor = true;
        EditorHost.LoadText(tab.Content, tab.FilePath, tab.Dialect);
        _syncingEditor = false;

        // 恢复该文件的诊断波浪线
        EditorHost.SetDiagnostics(tab.Diagnostics);
        UpdateEmptyState();
    }

    private void OnEditorTextChanged()
    {
        if (_syncingEditor) return;
        var tab = EditorTabs?.ActiveTab;
        if (tab == null) return;

        tab.Content = EditorHost.GetText();
        TextEdited?.Invoke(tab);
    }

    private void OnEditorCaretChanged()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null) return;

        tab.CursorLine = EditorHost.GetCaretLine();
        tab.CursorColumn = EditorHost.GetCaretColumn();
        CaretMoved?.Invoke(tab);
    }

    private void UpdateEmptyState()
    {
        // 编辑器控件常驻可见，避免 AvaloniaEdit 子控件在 IsVisible=false→true 后不再参与布局。
        // 只切换空态提示文字的显隐。
        EmptyStateText.IsVisible = EditorTabs?.ActiveTab == null;
    }

    // ─────────── 标签拖拽 → 独立窗口 ───────────

    private EditorTabViewModel? _dragTab;
    private Point _dragStart;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: EditorTabViewModel tab })
        {
            _dragTab = tab;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab == null) return;

        var pos = e.GetPosition(this);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;
        if (Math.Abs(dx) > 12 || Math.Abs(dy) > 12)
        {
            var tab = _dragTab;
            _dragTab = null;
            TabDetached?.Invoke(tab);
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragTab = null;
    }
}