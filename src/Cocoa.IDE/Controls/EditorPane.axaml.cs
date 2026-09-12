using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaEdit.CodeCompletion;
using Cocoa.CodeAnalysis;
using Cocoa.IDE.LanguageServices;
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

    /// <summary>请求跳转到某文件的行列（F12 / 错误），供宿主窗口处理（可能在其它窗口打开）。</summary>
    public event Action<GoToTarget>? NavigationRequested;

    private CompletionWindow? _completionWindow;

    private readonly Action<string, ImmutableArray<Diagnostic>> _diagnosticsHandler;
    private readonly SemanticModelHost _semanticHost = new();
    private string? _hostFile;
    private string? _hostText;

    public EditorPane()
    {
        InitializeComponent();
        EditorHost.TextChanged += (_, _) => OnEditorTextChanged();
        EditorHost.CaretChanged += (_, _) => OnEditorCaretChanged();

        // 跨窗口诊断广播：若命中的文件是本窗活动标签，重绘波浪线
        _diagnosticsHandler = (filePath, diagnostics) =>
        {
            var active = EditorTabs?.ActiveTab;
            if (active != null && active.FilePath == filePath)
                UpdateSquiggles(active);
        };
        EditorTabsRegistry.DiagnosticsApplied += _diagnosticsHandler;

        // Ctrl+Space 补全
        EditorHost.Editor.TextArea.KeyDown += OnEditorKeyDown;

        // 悬停签名提示
        EditorHost.Editor.TextArea.TextView.PointerHover += OnEditorPointerHover;
        EditorHost.Editor.TextArea.TextView.PointerHoverStopped += OnEditorPointerHoverStopped;
    }

    /// <summary>窗口关闭时调用：退订静态事件，避免已关闭窗口被静态注册表长期引用。</summary>
    public void Detach()
    {
        EditorTabsRegistry.DiagnosticsApplied -= _diagnosticsHandler;
        EditorTabs = null;
    }

    /// <summary>关闭脏标签的三态确认（保存/不保存/取消）。返回 true 表示可继续关闭。</summary>
    private async Task<bool> ConfirmCloseAsync(EditorTabViewModel tab)
    {
        if (!tab.IsModified) return true;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return true;

        var dialog = new SavePromptWindow(tab.FileName);
        var choice = await dialog.ShowDialog<bool?>(owner);
        if (choice == null) return false; // 取消
        if (choice == true)
        {
            try
            {
                await File.WriteAllTextAsync(tab.FilePath, tab.Content);
                tab.MarkSaved();
            }
            catch
            {
                return false; // 保存失败则不关闭
            }
        }

        return true;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            ShowCompletion();
            e.Handled = true;
        }
        else if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            GoToDefinition();
            e.Handled = true;
        }
    }

    /// <summary>按需（文件或内容变化时）重建语义宿主；Hover 等高频调用复用缓存，避免重复解析全目录。</summary>
    private SemanticModelHost? EnsureSemanticHost()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null || tab.Dialect == null) return null;

        if (_hostFile == tab.FilePath && _hostText == tab.Content)
            return _semanticHost;

        var language = tab.Dialect == "CSharp" ? Language.CSharp : Language.Cocoa;
        var context = MainViewModel.Shared?.SolutionTree.GetContext(tab.FilePath);
        _semanticHost.Update(tab.Content, tab.FilePath, language, context);
        _hostFile = tab.FilePath;
        _hostText = tab.Content;
        return _semanticHost;
    }

    private void ShowCompletion()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null) return;

        _completionWindow?.Close();
        _completionWindow = new CompletionWindow(EditorHost.Editor.TextArea)
        {
            CloseAutomatically = true,
        };

        var host = EnsureSemanticHost();
        if (host != null)
        {
            var caretOffset = EditorHost.Editor.TextArea.Caret.Offset;
            var text = EditorHost.Editor.Text ?? "";
            foreach (var item in CocoaCompletionProvider.GetCompletions(host, caretOffset, text))
                _completionWindow.CompletionList.CompletionData.Add(item);
        }

        _completionWindow.Show();
    }

    private void GoToDefinition()
    {
        var host = EnsureSemanticHost();
        if (host == null) return;

        var caretOffset = EditorHost.Editor.TextArea.Caret.Offset;
        var target = GoToDefinitionProvider.FindTarget(host, caretOffset);
        if (target == null)
            return;

        NavigationRequested?.Invoke(target);
    }

    // ─── 编辑命令（菜单/工具栏转发到编辑器）───

    public void EditUndo() => EditorHost.Undo();
    public void EditRedo() => EditorHost.Redo();
    public void EditCut() => EditorHost.Cut();
    public void EditCopy() => EditorHost.Copy();
    public void EditPaste() => EditorHost.Paste();
    public void EditSelectAll() => EditorHost.SelectAll();

    /// <summary>保存：内容已在 TextEdited 中连续同步到 tab.Content，这里直接写盘。</summary>
    public void SaveActiveTab()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null || string.IsNullOrEmpty(tab.FilePath)) return;
        try
        {
            File.WriteAllText(tab.FilePath, tab.Content);
            tab.MarkSaved();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"save failed: {ex.Message}");
        }
    }

    private void OnEditorPointerHover(object? sender, PointerEventArgs e)
    {
        var host = EnsureSemanticHost();
        if (host == null) return;

        var pos = e.GetPosition(EditorHost.Editor.TextArea.TextView);
        var viewPos = EditorHost.Editor.TextArea.TextView.GetPositionFloor(pos);
        if (viewPos == null) return;

        var offset = EditorHost.Editor.Document.GetOffset(viewPos.Value.Location);

        var hover = HoverProvider.GetHover(host, offset);
        if (hover == null) return;

        ToolTip.SetTip(EditorHost.Editor.TextArea, hover.Display);
        ToolTip.SetPlacement(EditorHost.Editor.TextArea, PlacementMode.Pointer);
        ToolTip.SetIsOpen(EditorHost.Editor.TextArea, true);
    }

    private void OnEditorPointerHoverStopped(object? sender, PointerEventArgs e)
    {
        ToolTip.SetIsOpen(EditorHost.Editor.TextArea, false);
    }

    public void AttachTabs(EditorTabsViewModel tabs)
    {
        EditorTabs = tabs;
        DataContext = tabs;
        tabs.ConfirmClose = ConfirmCloseAsync;

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
