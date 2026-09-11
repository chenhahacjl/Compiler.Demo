using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cocoa.IDE.Controls;
using Cocoa.IDE.ViewModels;

namespace Cocoa.IDE;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // 主窗口绑定共享 ViewModel 的标签集合
        Pane.AttachTabs(ViewModel.EditorTabs);

        // 编辑器内容变化 → 实时诊断
        Pane.TextEdited += tab =>
        {
            if (tab.Dialect != null)
                ViewModel.DiagnosticService.TextChanged(tab.FilePath, tab.Content, tab.Dialect);
        };

        // 光标位置 → 状态栏
        Pane.CaretMoved += tab => ViewModel.StatusBar.CursorPosition = $"Ln {tab.CursorLine}, Col {tab.CursorColumn}";

        // 标签拖出 → 独立浮动窗口
        Pane.TabDetached += OnTabDetached;

        ViewModel.Output.CopyRequested += async text =>
        {
            var clipboard = Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        };

        ViewModel.FileActivated += path =>
        {
            OnActiveTabChanged();
        };

        // 错误列表等请求定位：若标签在本窗口则定位
        EditorTabsRegistry.NavigateRequested += (tab, line, col) =>
        {
            if (ViewModel.EditorTabs.Tabs.Contains(tab))
                Pane.NavigateTo(tab, line, col);
        };

        Closing += OnWindowClosing;
    }

    /// <summary>标签被拖出：从主窗口移除，放入新浮动窗口。</summary>
    private void OnTabDetached(EditorTabViewModel tab)
    {
        if (tab == null) return;

        // 从主窗口标签集合移除
        if (ViewModel.EditorTabs.Tabs.Contains(tab))
            ViewModel.EditorTabs.CloseTab(tab);

        var floatWin = new FloatingEditorWindow();
        floatWin.AddTab(tab);
        floatWin.Show();
    }

    private void OnActiveTabChanged()
    {
        // 标签集合变化后主窗口状态栏联动
        var tab = ViewModel.EditorTabs.ActiveTab;
        if (tab == null)
        {
            ViewModel.StatusBar.ResetActiveDocument();
            return;
        }
        ViewModel.StatusBar.Language = tab.Dialect ?? "";
        ViewModel.StatusBar.CursorPosition = $"Ln {tab.CursorLine}, Col {tab.CursorColumn}";
    }

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (SolutionTree.SelectedItem is not TreeNodeViewModel node) return;
        if (node.Kind != NodeKind.Source || node.FullPath == null) return;

        ViewModel.OpenFile(node.FullPath);
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
        // 浮窗关闭各自处理；主窗口只负责自己的标签
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