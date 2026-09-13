using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.IDE.Controls;
using Cocoa.IDE.Services;
using Cocoa.IDE.ViewModels;
using System.ComponentModel;

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
                case "Find": Pane.ShowFind(); break;
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

        // M7：断点边距 → 服务；服务断点变化 → 编辑器边距；调试暂停 → 打开并高亮
        Pane.BreakpointToggled += line =>
        {
            var file = ViewModel.EditorTabs.ActiveTab?.FilePath;
            if (file != null) ViewModel.DebuggerService.ToggleBreakpoint(file, line);
        };
        ViewModel.DebuggerService.BreakpointsChanged += RefreshBreakpoints;
        ViewModel.DebuggerService.Resumed += () => Pane.SetCurrentDebugLine(null);
        ViewModel.DebuggerService.Exited += () => Pane.SetCurrentDebugLine(null);
        ViewModel.DebugPausedAt += NavigateDebug;
        ViewModel.EditorTabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorTabsViewModel.ActiveTab))
                RefreshBreakpoints(ViewModel.EditorTabs.ActiveTab?.FilePath);
        };

        // 错误列表等请求定位：若标签在本窗口则定位
        EditorTabsRegistry.NavigateRequested += (tab, line, col) =>
        {
            if (ViewModel.EditorTabs.Tabs.Contains(tab))
                Pane.NavigateTo(tab, line, col);
        };

        // 树右键：新建文件 / 移除
        ViewModel.SolutionTree.NewFileRequested += node => NewFileIn(node);
        ViewModel.SolutionTree.RemoveRequested += node => RemoveNode(node);

        // 输出自动滚动到底部
        ViewModel.Output.Lines.CollectionChanged += (_, _) =>
        {
            if (ViewModel.Output.IsAutoScroll && OutputList.ItemCount > 0)
                OutputList.ScrollIntoView(OutputList.ItemCount - 1);
        };

        Closing += OnWindowClosing;
    }

    // ─── 菜单/工具栏：退出、视图显隐、项目、帮助 ───

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleExplorer(object? sender, RoutedEventArgs e)
    {
        var visible = ViewExplorerItem.IsChecked == true;
        ExplorerPanel.IsVisible = visible;
        ExplorerSplitter.IsVisible = visible;
        MainContentGrid.ColumnDefinitions[0].Width = new GridLength(visible ? 280 : 0);
        MainContentGrid.ColumnDefinitions[1].Width = new GridLength(visible ? 4 : 0);
    }

    private void OnToggleProperties(object? sender, RoutedEventArgs e)
    {
        var visible = ViewPropertiesItem.IsChecked == true;
        PropertiesPanel.IsVisible = visible;
        PropertiesSplitter.IsVisible = visible;
        MainContentGrid.ColumnDefinitions[4].Width = new GridLength(visible ? 300 : 0);
        MainContentGrid.ColumnDefinitions[3].Width = new GridLength(visible ? 4 : 0);
    }

    private void OnToggleErrors(object? sender, RoutedEventArgs e)
    {
        if (ViewErrorItem.IsChecked == true) BottomTabs.SelectedIndex = 1;
        UpdateBottomVisibility();
    }

    private void OnToggleOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewOutputItem.IsChecked == true) BottomTabs.SelectedIndex = 0;
        UpdateBottomVisibility();
    }

    private void UpdateBottomVisibility()
    {
        var visible = ViewErrorItem.IsChecked == true || ViewOutputItem.IsChecked == true;
        BottomPanel.IsVisible = visible;
        ShellGrid.RowDefinitions[1].Height = new GridLength(visible ? 180 : 0);
    }

    private async void OnProjectAddExistingFile(object? sender, RoutedEventArgs e)
    {
        var node = SolutionTree.SelectedItem as TreeNodeViewModel;
        await AddExistingItemsToProjectAsync(node);
    }

    private void OnProjectRemoveFile(object? sender, RoutedEventArgs e)
    {
        if (SolutionTree.SelectedItem is TreeNodeViewModel { IsSource: true } node)
            RemoveNode(node);
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new Window
        {
            Title = "关于 Cocoa IDE",
            Width = 440,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Margin = new Avalonia.Thickness(20),
                FontSize = 13,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = "Cocoa IDE\n\n基于 Avalonia 11 + Cocoa 编译器内核的类 Visual Studio 桌面 IDE。\n"
                       + "支持解决方案/项目管理、语法着色、实时诊断、补全/Hover/F12、构建运行。",
            },
        };
        _ = about.ShowDialog(this);
    }

    private void OnErrorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ErrorListBox.SelectedItem is ErrorItemViewModel item)
            ViewModel.Properties.ShowError(item);
    }

    // ─── M7 调试 UI ───

    private void RefreshBreakpoints(string? file)
    {
        Pane.SetBreakpoints(file == null
            ? Array.Empty<int>()
            : ViewModel.DebuggerService.GetBreakpoints(file));
    }

    private void NavigateDebug(string file, int line)
    {
        ViewModel.OpenFile(file);
        var tab = ViewModel.EditorTabs.ActiveTab;
        if (tab != null && string.Equals(tab.FilePath, file, StringComparison.OrdinalIgnoreCase))
        {
            Pane.NavigateTo(tab, line, 1);
            Pane.SetCurrentDebugLine(line);
        }
    }

    /// <summary>右键菜单：优先取菜单项 Tag，其次 DataContext，最后回退当前选中节点。</summary>
    private TreeNodeViewModel? CtxNode(object? sender) =>
        (sender as Avalonia.Controls.MenuItem)?.Tag as TreeNodeViewModel
        ?? (sender as Avalonia.Controls.MenuItem)?.DataContext as TreeNodeViewModel
        ?? _ctxNode
        ?? SolutionTree.SelectedItem as TreeNodeViewModel;

    private TreeNodeViewModel? _ctxNode;

    /// <summary>打开右键菜单时按节点类型动态构建条目；分隔符只插入在可见分组之间。</summary>
    private void OnNodeContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        _ctxNode = menu.DataContext as TreeNodeViewModel
                   ?? (menu.Parent as Control)?.DataContext as TreeNodeViewModel
                   ?? SolutionTree.SelectedItem as TreeNodeViewModel;
        var node = _ctxNode;
        if (node == null) return;

        menu.Items.Clear();
        var groups = new List<List<Control>>();

        if (node.IsBuildable)
            groups.Add(new() { Item("生成", OnCtxBuild), Item("重新生成", OnCtxRebuild), Item("清理", OnCtxClean) });

        if (node.IsProject)
            groups.Add(new() { Item("设为启动项目", OnCtxSetStartup), Item("运行", OnCtxRun), Item("调试", OnCtxDebug) });

        if (node.CanAdd)
        {
            var add = new MenuItem { Header = "添加(_A)" };
            if (node.CanCreateFile)
            {
                add.Items.Add(Item("新建项…", OnCtxNewFile));
                add.Items.Add(Item("现有项…", OnCtxAddExistingItem));
            }
            if (node.IsProjectOrDependencies)
            {
                if (add.Items.Count > 0) add.Items.Add(new Separator());
                add.Items.Add(Item("引用…", OnCtxAddReference));
            }
            if (node.IsSolution)
            {
                if (add.Items.Count > 0) add.Items.Add(new Separator());
                add.Items.Add(Item("新建项目…", OnCtxAddNewProject));
                add.Items.Add(Item("现有项目…", OnCtxAddExistingProject));
            }
            if (add.Items.Count > 0) groups.Add(new() { add });
        }

        var fileGroup = new List<Control>();
        if (node.IsSource) { fileGroup.Add(Item("打开", OnCtxOpen)); fileGroup.Add(Item("从项目中移除", OnCtxRemove)); }
        if (node.IsProject) fileGroup.Add(Item("从解决方案中移除", OnCtxRemoveProject));
        if (node.IsReferenceNode) fileGroup.Add(Item("移除引用", OnCtxRemoveReference));
        if (fileGroup.Count > 0) groups.Add(fileGroup);

        if (node.HasPath)
            groups.Add(new() { Item("在资源管理器中显示", OnCtxShowInExplorer), Item("复制完整路径", OnCtxCopyPath) });

        groups.Add(new() { Item("属性", OnCtxProperties) });

        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0) menu.Items.Add(new Separator());
            foreach (var item in groups[i]) menu.Items.Add(item);
        }
    }

    private MenuItem Item(string header, EventHandler<RoutedEventArgs> handler)
    {
        var item = new MenuItem { Header = header, Tag = _ctxNode };
        item.Click += handler;
        return item;
    }

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
            ShowProperties(node);
    }

    /// <summary>项目节点 → 打开项目属性页；其它节点 → 右侧属性面板。</summary>
    private void ShowProperties(TreeNodeViewModel node)
    {
        if (node.IsProject)
            ViewModel.OpenProjectProperties(node);
        else
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

    // ─── 右键：生成/运行/添加 ───

    private async void OnCtxBuild(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node) await ViewModel.BuildNodeAsync(node);
    }

    private async void OnCtxRebuild(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node) await ViewModel.RebuildNodeAsync(node);
    }

    private void OnCtxClean(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node) ViewModel.CleanNode(node);
    }

    private void OnCtxSetStartup(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node) ViewModel.SetStartupProject(node);
    }

    private async void OnCtxRun(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node) await ViewModel.RunNodeAsync(node);
    }

    private void OnCtxDebug(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node) ViewModel.DebugNode(node);
    }

    private async void OnCtxAddExistingItem(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node)
            await AddExistingItemsToProjectAsync(node);
    }

    /// <summary>文件选择器 → 多个现有源文件显式加入指定节点的所属项目。</summary>
    private async Task AddExistingItemsToProjectAsync(TreeNodeViewModel? node)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加现有项",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cocoa 源文件") { Patterns = new[] { "*.co", "*.cs" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
            },
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
                ViewModel.SolutionTree.AddSourceToProject(node, path);
        }
    }

    /// <summary>向当前解决方案添加新建项目（模板 + 名称）。</summary>
    private async void OnCtxAddNewProject(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is not { IsSolution: true }) return;

        var solutionDir = ViewModel.SolutionTree.CurrentSolution?.Directory;
        if (solutionDir == null)
        {
            ViewModel.Output.AppendLine("error: 当前没有打开解决方案");
            return;
        }

        var dialog = new AddProjectDialog();
        var result = await dialog.ShowDialog<NewProjectIntoResult?>(this);
        if (result == null) return;

        try
        {
            var projectPath = NewProjectService.CreateProjectInto(result.Template, result.Name, solutionDir);
            if (ViewModel.SolutionTree.AddProjectToSolution(projectPath, out var error))
                ViewModel.Output.AppendLine($"已新建项目：{result.Name}");
            else
                ViewModel.Output.AppendLine($"error: {error}");
        }
        catch (Exception ex)
        {
            ViewModel.Output.AppendLine("error: " + ex.Message);
        }
    }

    private async void OnCtxAddExistingProject(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is not { IsSolution: true }) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加现有项目",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cocoa 项目") { Patterns = new[] { "*.coproj" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
            },
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;

            if (ViewModel.SolutionTree.AddProjectToSolution(path, out var error))
                ViewModel.Output.AppendLine($"已添加项目：{Path.GetFileName(path)}");
            else
                ViewModel.Output.AppendLine($"error: 添加项目失败：{error}");
        }
    }

    private void OnCtxRemoveProject(object? sender, RoutedEventArgs e)
    {
        if (CtxNode(sender) is { } node &&
            !ViewModel.SolutionTree.RemoveProjectFromSolution(node, out var error))
            ViewModel.Output.AppendLine($"error: {error}");
    }

    /// <summary>在节点目录下新建源文件（简单对话框输入文件名），并显式加入所属项目。</summary>
    private async void NewFileIn(TreeNodeViewModel node)
    {
        var dir = ViewModel.SolutionTree.DirectoryFor(node);
        if (dir == null) return;

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
        ViewModel.SolutionTree.AddSourceToProject(node, path);
        ViewModel.OpenFile(path);
    }

    /// <summary>从项目移除源文件（文本级改写 .coproj，不删除磁盘文件）。</summary>
    private void RemoveNode(TreeNodeViewModel node)
    {
        if (node.FullPath == null) return;
        if (!ViewModel.SolutionTree.RemoveSourceFromProject(node))
            ViewModel.Output.AppendLine("error: 该文件由通配符包含，无法单独移除（可修改 .coproj 的源文件模式）");
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
            ShowProperties(node);
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