using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cocoa.CodeAnalysis.Syntax;
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
        Pane.TextEdited += tab => ViewModel.EditorTextChanged(tab);

        // 光标位置 → 状态栏
        Pane.CaretMoved += tab => ViewModel.StatusBar.CursorPosition = $"Ln {tab.CursorLine}, Col {tab.CursorColumn}";

        // 标签拖出 → 独立浮动窗口
        Pane.TabDetached += OnTabDetached;

        // 编辑命令 → 转发到编辑器
        ViewModel.EditActionRequested += action =>
        {
            switch (action)
            {
                case "Undo": Pane.EditUndo(); break;
                case "Redo": Pane.EditRedo(); break;
                case "Cut": Pane.EditCut(); break;
                case "Copy": Pane.EditCopy(); break;
                case "Paste": Pane.EditPaste(); break;
                case "SelectAll": Pane.EditSelectAll(); break;
            }
        };

        // F12 跳转定义
        Pane.NavigationRequested += target =>
        {
            if (target == null) return;

            // 若目标文件已在某窗口打开则激活并在该窗口定位，否则在主窗口打开
            var existing = ViewModel.FindOpenTabViewModel(target.FilePath);
            if (existing != null)
            {
                EditorTabsRegistry.RequestNavigate(existing, target.Line, target.Column);
            }
            else
            {
                ViewModel.OpenFile(target.FilePath);
                var tab = ViewModel.EditorTabs.ActiveTab;
                if (tab != null)
                    Pane.NavigateTo(tab, target.Line, target.Column);
            }
        };

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

        // 树右键：新建文件 / 移除
        ViewModel.SolutionTree.NewFileRequested += dir => NewFileIn(dir);
        ViewModel.SolutionTree.RemoveRequested += node => RemoveNode(node);

        Closing += OnWindowClosing;
    }

    /// <summary>右键菜单：从 sender.DataContext 取节点（ContextMenu 继承节点 DataContext）。</summary>
    private static TreeNodeViewModel? CtxNode(object? sender) =>
        (sender as Avalonia.Controls.MenuItem)?.DataContext as TreeNodeViewModel;

    private void OnCtxOpen(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { IsSource: true, FullPath: not null } node)
            ViewModel.OpenFile(node.FullPath);
    }

    private void OnCtxShowInExplorer(object? sender, RoutedEventArgs e)
    {
        CtxNode(sender)?.ShowInExplorer();
    }

    private void OnCtxCopyPath(object? sender, RoutedEventArgs e)
    {
        CtxNode(sender)?.CopyFullPath();
    }

    private void OnCtxProperties(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node)
            ViewModel.ShowNodeProperties(node);
    }

    private void OnCtxNewFile(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node)
            ViewModel.SolutionTree.RequestNewFile(node);
    }

    private void OnCtxRemove(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node)
            ViewModel.SolutionTree.RequestRemove(node);
    }

    /// <summary>在目录下新建源文件（简单对话框输入文件名）。</summary>
    private async void NewFileIn(string dir)
    {
        var name = await ShowTextInputAsync("新建文件", "文件名（.co / .cs）");
        if (string.IsNullOrWhiteSpace(name)) return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await ShowTextInputAsync("提示", "名称包含非法字符");
            return;
        }

        var path = Path.Combine(dir, name.EndsWith(".co", StringComparison.OrdinalIgnoreCase) ||
                                       name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? name : name + ".co");
        if (File.Exists(path))
        {
            await ShowTextInputAsync("提示", "文件已存在");
            return;
        }

        File.WriteAllText(path, "");
        ViewModel.OpenFile(path);
        ViewModel.SolutionTree.Refresh();
    }

    /// <summary>从项目移除源文件（确认后删除并刷新树）。</summary>
    private void RemoveNode(TreeNodeViewModel node)
    {
        if (node.FullPath == null || !File.Exists(node.FullPath)) return;
        File.Delete(node.FullPath);
        ViewModel.SolutionTree.Refresh();
    }

    /// <summary>轻量文本输入对话框。</summary>
    private Task<string?> ShowTextInputAsync(string title, string prompt)
    {
        var dialog = new TextInputDialog(title, prompt);
        return dialog.ShowDialog<string?>(this);
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

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SolutionTree.SelectedItem is TreeNodeViewModel node)
            ViewModel.ShowNodeProperties(node);
    }

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (SolutionTree.SelectedItem is not TreeNodeViewModel node) return;

        // 容器节点：展开/折叠
        if (node.Kind is NodeKind.Solution or NodeKind.Project or NodeKind.Folder or NodeKind.Dependencies)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
            return;
        }

        if (node.Kind != NodeKind.Source || node.FullPath == null) return;
        ViewModel.OpenFile(node.FullPath);
        e.Handled = true;
    }

    // ─── 资源管理器工具栏 ───

    private void OnTreeRefresh(object? sender, RoutedEventArgs e) => ViewModel.SolutionTree.Refresh();

    private void OnTreeCollapseAll(object? sender, RoutedEventArgs e) => ViewModel.SolutionTree.CollapseAll();

    private void OnTreeProperties(object? sender, RoutedEventArgs e)
    {
        if (SolutionTree.SelectedItem is TreeNodeViewModel node)
            ViewModel.ShowNodeProperties(node);
    }

    // ─── 引用增删 ───

    private async void OnCtxAddReference(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is not { } node) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加引用",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("引用程序集") { Patterns = new[] { "*.coa", "*.dll" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
            },
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
                ViewModel.SolutionTree.AddReferenceToProject(node, path);
        }
    }

    private void OnCtxRemoveReference(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node)
            ViewModel.SolutionTree.RemoveReferenceFromProject(node);
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

        // 覆盖所有窗口（主窗口 + 浮窗）的未保存标签：应用退出时浮窗 Closing 未必触发
        var dirty = EditorTabsRegistry.All
            .SelectMany(set => set.Tabs)
            .Where(t => t.IsModified)
            .Distinct()
            .ToList();
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

public sealed class TextInputDialog : Window
{
    private readonly TextBox _input;

    public TextInputDialog(string title, string prompt)
    {
        Title = title;
        Width = 380;
        MinHeight = 140;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };

        root.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        _input = new TextBox();
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var okBtn = new Button { Content = "确定", MinWidth = 80 };
        okBtn.Click += (_, _) => Close(_input.Text?.Trim());
        var cancelBtn = new Button { Content = "取消", MinWidth = 80 };
        cancelBtn.Click += (_, _) => Close();
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);

        root.Children.Add(buttons);
        Content = root;

        _input.Loaded += (_, _) => _input.Focus();
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