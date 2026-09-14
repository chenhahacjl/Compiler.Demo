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

    /// <summary>用户点击断点边距切换某行断点。</summary>
    public event Action<int>? BreakpointToggled;

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
            {
                UpdateSquiggles(active);
                EditorHost.SetSyntaxTree(active.SyntaxTree);
            }
        };
        EditorTabsRegistry.DiagnosticsApplied += _diagnosticsHandler;

        // Ctrl+Space 补全
        EditorHost.Editor.TextArea.KeyDown += OnEditorKeyDown;

        // 输入 '.' 自动触发成员补全
        EditorHost.Editor.TextArea.TextEntered += OnEditorTextEntered;

        // 悬停签名提示
        EditorHost.Editor.TextArea.TextView.PointerHover += OnEditorPointerHover;
        EditorHost.Editor.TextArea.TextView.PointerHoverStopped += OnEditorPointerHoverStopped;

        // 断点边距点击转发
        EditorHost.BreakpointToggled += line => BreakpointToggled?.Invoke(line);
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
        else if (e.Key == Key.F9 && e.KeyModifiers == KeyModifiers.None)
        {
            EditorHost.ToggleBreakpointAtCaret();
            e.Handled = true;
        }
    }

    private void OnEditorTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        if (e.Text == ".")
        {
            ShowCompletion();
            return;
        }

        // M5c：输入标识符字符自动弹补全；已开窗则重算候选（保证局部变量等新候选能出现）
        if (IsIdentifierStart(e.Text[0]))
        {
            if (_completionWindow == null)
                ShowCompletion();
            else
                RefreshCompletion();
        }
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>已开窗时按最新文本重算候选并保留前缀过滤。</summary>
    private void RefreshCompletion()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null || _completionWindow == null) return;

        var host = EnsureSemanticHostReusable();
        if (host == null) return;

        var caretOffset = EditorHost.Editor.TextArea.Caret.Offset;
        var text = EditorHost.Editor.Text ?? "";
        var dialect = tab.Dialect;

        var list = _completionWindow.CompletionList;
        list.CompletionData.Clear();
        foreach (var item in CocoaCompletionProvider.GetCompletions(host, caretOffset, text, dialect))
            list.CompletionData.Add(item);

        if (list.CompletionData.Count == 0)
        {
            _completionWindow.Close();
            return;
        }

        list.SelectItem(ExtractPrefix(text, caretOffset));
    }

    private static string ExtractPrefix(string text, int offset)
    {
        var i = offset;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) i--;
        return i < offset ? text.Substring(i, offset - i) : "";
    }

    /// <summary>按需（文件或内容变化时）重建语义宿主；Hover 等高频调用复用缓存，避免重复解析全目录。</summary>
    private SemanticModelHost? EnsureSemanticHost()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null) return null;

        if (_hostFile == tab.FilePath && _hostText == tab.Content)
            return _semanticHost;

        var context = MainViewModel.Shared?.SolutionTree.GetContext(tab.FilePath);
        _semanticHost.Update(tab.Content, tab.FilePath, context);
        _hostFile = tab.FilePath;
        _hostText = tab.Content;
        return _semanticHost;
    }

    /// <summary>M5c：自动补全用宿主——同文件复用已缓存语义（树可略旧），避免逐键重解析/重绑整个工程。</summary>
    private SemanticModelHost? EnsureSemanticHostReusable()
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null) return null;
        return _hostFile == tab.FilePath ? _semanticHost : EnsureSemanticHost();
    }

    private void ShowCompletion(bool reuseStale = false)
    {
        var tab = EditorTabs?.ActiveTab;
        if (tab == null) return;

        _completionWindow?.Close();
        var window = new CompletionWindow(EditorHost.Editor.TextArea)
        {
            CloseAutomatically = true,
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window)) _completionWindow = null;
        };
        _completionWindow = window;

        var caretOffset = EditorHost.Editor.TextArea.Caret.Offset;
        var text = EditorHost.Editor.Text ?? "";

        // 替换段起点 = 前缀起点，否则已输入的字符会被保留导致重复（如 r + result → rresult）
        window.StartOffset = caretOffset - ExtractPrefix(text, caretOffset).Length;

        var host = reuseStale ? EnsureSemanticHostReusable() : EnsureSemanticHost();
        if (host != null)
        {
            var dialect = tab.Dialect;
            foreach (var item in CocoaCompletionProvider.GetCompletions(host, caretOffset, text, dialect))
                window.CompletionList.CompletionData.Add(item);
        }

        // 无候选不弹出空框
        if (window.CompletionList.CompletionData.Count == 0)
        {
            window.Close();
            return;
        }

        window.Show();
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

    /// <summary>打开文件内查找面板（Ctrl+F）。</summary>
    public void ShowFind() => EditorHost.ShowFind();

    /// <summary>设置当前文件断点行。</summary>
    public void SetBreakpoints(IEnumerable<int> lines) => EditorHost.SetBreakpoints(lines);

    /// <summary>设置调试暂停行（黄色高亮）。</summary>
    public void SetCurrentDebugLine(int? line) => EditorHost.SetCurrentDebugLine(line);

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

        // 项目属性虚拟标签：显示属性页（覆盖编辑器）
        if (tab is { IsVirtual: true })
        {
            _syncingEditor = true;
            PropertiesDocPanel.DataContext = tab.ProjectProperties;
            PropertiesDocPanel.IsVisible = true;
            EmptyStatePanel.IsVisible = false;
            _syncingEditor = false;
            return;
        }

        PropertiesDocPanel.IsVisible = false;

        if (tab == null)
        {
            _syncingEditor = false;
            EditorHost.LoadText("", null, null);
            EditorHost.SetSyntaxTree(null);
            UpdateEmptyState();
            return;
        }

        _syncingEditor = true;
        EditorHost.LoadText(tab.Content, tab.FilePath, tab.Dialect);
        _syncingEditor = false;

        // 恢复该文件的诊断波浪线 + 语义着色
        EditorHost.SetDiagnostics(tab.Diagnostics);
        EditorHost.SetSyntaxTree(tab.SyntaxTree);
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
        // 无标签时用不透明空态面板覆盖编辑器。
        EmptyStatePanel.IsVisible = EditorTabs?.ActiveTab == null;
    }

    // ─────────── 标签拖拽 → 独立窗口 ───────────

    private EditorTabViewModel? _dragTab;
    private Point _dragStart;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: EditorTabViewModel tab } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
