using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cocoa.IDE.ViewModels;

namespace Cocoa.IDE;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private bool _syncingEditor;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        EditorHost.TextChanged += (_, _) => OnEditorTextChanged();
        EditorHost.CaretChanged += (_, _) => OnEditorCaretChanged();

        ViewModel.EditorTabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorTabsViewModel.ActiveTab))
                OnActiveTabChanged();
        };

        ViewModel.EditorContent += (file, line, col) => NavigateToEditor(file, line, col);

        UpdateEmptyState();
        Closing += OnWindowClosing;
    }

    private void OnActiveTabChanged()
    {
        var tab = ViewModel.EditorTabs.ActiveTab;
        if (tab == null)
        {
            _syncingEditor = false;
            EditorHost.LoadText("", null, null);
            UpdateEmptyState();
            return;
        }

        // 同步编辑器内容 = 标签内容（连续同步，无需额外保存）
        _syncingEditor = true;
        EditorHost.LoadText(tab.Content, tab.FilePath, tab.Dialect);
        _syncingEditor = false;
        UpdateEmptyState();
    }

    private void OnEditorTextChanged()
    {
        if (_syncingEditor) return;
        var tab = ViewModel.EditorTabs.ActiveTab;
        if (tab != null)
            tab.Content = EditorHost.GetText();
    }

    private void OnEditorCaretChanged()
    {
        var tab = ViewModel.EditorTabs.ActiveTab;
        if (tab == null) return;

        tab.CursorLine = EditorHost.GetCaretLine();
        tab.CursorColumn = EditorHost.GetCaretColumn();
        ViewModel.StatusBar.CursorPosition = $"Ln {tab.CursorLine}, Col {tab.CursorColumn}";
    }

    private void UpdateEmptyState()
    {
        // 编辑器控件常驻可见，避免 AvaloniaEdit 子控件在 IsVisible=false→true 后不再参与布局。
        // 只切换空态提示文字的显隐。
        EmptyStateText.IsVisible = ViewModel.EditorTabs.ActiveTab == null;
    }

    private void NavigateToEditor(string file, int line, int col)
    {
        // 打开文件后定位到行列
        OnActiveTabChanged();
        EditorHost.SetCaret(line, col);
        EditorHost.Focus();
    }

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (SolutionTree.SelectedItem is not TreeNodeViewModel node) return;
        if (node.Kind != NodeKind.Source || node.FullPath == null) return;

        ViewModel.EditorTabs.OpenFile(node.FullPath);
        e.Handled = true;
    }

    private void OnErrorDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is ErrorItemViewModel item)
        {
            ViewModel.ErrorList.ActivateCommand.Execute(item);
            e.Handled = true;
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;

        var dirty = ViewModel.EditorTabs.Tabs.Where(t => t.IsModified).ToList();
        if (dirty.Count == 0) return;

        // 先取消默认关闭，弹确认框后再关闭
        e.Cancel = true;

        var names = string.Join(", ", dirty.Select(t => t.FileName));
        var choice = await ShowCloseConfirmAsync(names);
        if (choice == null) return; // 取消 → 保持不关闭

        _closingConfirmed = true;
        if (choice == true)
        {
            foreach (var tab in dirty)
            {
                try { File.WriteAllText(tab.FilePath, tab.Content); tab.MarkSaved(); }
                catch { /* 忽略个别保存失败 */ }
            }
        }

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

public sealed class SavePromptWindow : Window
{
    public SavePromptWindow(string fileNames)
    {
        Title = "保存更改";
        Width = 420;
        MinHeight = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };

        root.Children.Add(new TextBlock
        {
            Text = $"是否保存对以下文件的更改：\n{fileNames}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 380
        });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        // Close(bool?) 会作为 ShowDialog<bool?>(owner) 的返回值
        var saveBtn = new Button { Content = "保存", MinWidth = 80 };
        saveBtn.Click += (_, _) => Close(true);
        var dontSaveBtn = new Button { Content = "不保存", MinWidth = 80 };
        dontSaveBtn.Click += (_, _) => Close(false);
        var cancelBtn = new Button { Content = "取消", MinWidth = 80 };
        cancelBtn.Click += (_, _) => Close();

        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(dontSaveBtn);
        buttons.Children.Add(saveBtn);

        root.Children.Add(buttons);
        Content = root;
    }
}