using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cocoa.IDE.Services;

namespace Cocoa.IDE;

/// <summary>VS2022 两步式新建项目向导：步骤 1 选模板，步骤 2 配置名称/位置/解决方案/目标框架。
/// 统一生成「解决方案 + 项目子目录」；空白解决方案模板只生成 .cosln。
/// Close(NewProjectResult) 返回生成结果。</summary>
public sealed class NewProjectDialog : Window
{
    private static readonly string[] TargetFrameworks =
        { "net48", "net9.0", "net8.0", "net6.0", "netcoreapp3.1" };

    private readonly IReadOnlyList<NewProjectService.TemplateOption> _allOptions;

    private readonly StackPanel _step1Panel;
    private readonly Grid _step2Panel;
    private readonly TextBlock _headerText;

    private readonly TextBox _searchBox;
    private readonly ListBox _templateList;
    private readonly PathIcon _detailIcon;
    private readonly TextBlock _detailName;
    private readonly TextBlock _detailDesc;

    private readonly TextBox _nameBox;
    private readonly TextBox _locationBox;
    private readonly TextBox _solutionBox;
    private readonly CheckBox _sameDirBox;
    private readonly ComboBox _tfmBox;

    private readonly TextBlock _errorText;
    private readonly Button _backBtn;
    private readonly Button _nextBtn;
    private readonly Button _createBtn;

    private NewProjectService.TemplateOption? _selected;
    private bool _solutionEdited;
    private bool _syncingSolution;
    public NewProjectDialog()
    {
        Title = "创建新项目";
        Width = 760;
        Height = 560;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        _allOptions = NewProjectService.TemplateOptions;

        // ── 步骤 1：模板选择 ──
        _searchBox = new TextBox { Watermark = "搜索模板", Margin = new Thickness(0, 0, 0, 8) };
        _searchBox.TextChanged += (_, _) => FilterTemplates();

        _templateList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _templateList.SelectionChanged += (_, _) => OnTemplateSelected();

        _detailIcon = new PathIcon { Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 10) };
        _detailName = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _detailDesc = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
            TextWrapping = TextWrapping.Wrap,
        };

        var detailPanel = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            Children = { _detailIcon, _detailName, _detailDesc },
        };
        var step1Grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*") };
        Grid.SetColumn(_templateList, 0);
        Grid.SetColumn(detailPanel, 1);
        step1Grid.Children.Add(_templateList);
        step1Grid.Children.Add(detailPanel);

        _step1Panel = new StackPanel { Children = { _searchBox, step1Grid } };

        // ── 步骤 2：配置 ──
        _nameBox = new TextBox();
        _nameBox.TextChanged += (_, _) => SyncSolutionName();

        _locationBox = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CocoaProjects"),
        };
        var browseBtn = new Button { Content = "浏览…", Margin = new Thickness(6, 0, 0, 0) };
        browseBtn.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择位置",
                AllowMultiple = false,
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } picked)
                _locationBox.Text = picked;
        };
        var locationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_locationBox, 0);
        Grid.SetColumn(browseBtn, 1);
        locationRow.Children.Add(_locationBox);
        locationRow.Children.Add(browseBtn);

        _solutionBox = new TextBox();
        _solutionBox.TextChanged += (_, _) =>
        {
            if (!_syncingSolution) _solutionEdited = true;
        };

        _sameDirBox = new CheckBox { Content = "将解决方案和项目放在同一目录中" };

        _tfmBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
        foreach (var tfm in TargetFrameworks) _tfmBox.Items.Add(tfm);
        _tfmBox.SelectedIndex = 0;

        _step2Panel = BuildForm(locationRow);
        _step2Panel.IsVisible = false;

        // ── 页脚 ──
        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#F48771")),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
        };
        _backBtn = new Button { Content = "上一步", MinWidth = 90, IsVisible = false };
        _backBtn.Click += (_, _) => SetStep(0);
        _nextBtn = new Button { Content = "下一步", MinWidth = 90 };
        _nextBtn.Click += (_, _) => SetStep(1);
        _createBtn = new Button { Content = "创建", MinWidth = 90, IsDefault = true, IsVisible = false };
        _createBtn.Click += (_, _) => Create();
        var cancelBtn = new Button { Content = "取消", MinWidth = 90 };
        cancelBtn.Click += (_, _) => Close();

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _backBtn, _nextBtn, _createBtn, cancelBtn },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetColumn(_errorText, 0);
        Grid.SetColumn(footerButtons, 1);
        footer.Children.Add(_errorText);
        footer.Children.Add(footerButtons);

        _headerText = new TextBlock { Text = "创建新项目", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) };

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(_headerText, 0);
        Grid.SetRow(_step1Panel, 1);
        Grid.SetRow(_step2Panel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(_headerText);
        root.Children.Add(_step1Panel);
        root.Children.Add(_step2Panel);
        root.Children.Add(footer);

        Content = root;

        FilterTemplates();
        if (_templateList.ItemCount > 0)
            _templateList.SelectedIndex = 0;
    }

    private Grid BuildForm(Grid locationRow)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
        };

        void AddRow(int row, string label, Control control)
        {
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 0, 0, 8);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }

        AddRow(0, "项目名称", _nameBox);
        AddRow(1, "位置", locationRow);
        AddRow(2, "解决方案名称", _solutionBox);
        AddRow(3, "目标框架", _tfmBox);

        Grid.SetRow(_sameDirBox, 4);
        Grid.SetColumn(_sameDirBox, 1);
        _sameDirBox.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(_sameDirBox);

        return grid;
    }

    private void SetStep(int step)
    {
        SetError(null);
        if (step == 1 && _selected == null)
        {
            SetError("请先选择模板");
            return;
        }

        var onConfig = step == 1;
        _step1Panel.IsVisible = !onConfig;
        _step2Panel.IsVisible = onConfig;
        _backBtn.IsVisible = onConfig;
        _createBtn.IsVisible = onConfig;
        _nextBtn.IsVisible = !onConfig;
        _headerText.Text = onConfig ? "配置新项目" : "创建新项目";

        if (onConfig && string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _nameBox.Text = _selected!.Key switch
            {
                "solution" => "MySolution",
                _ => "MyApp",
            };
        }
    }

    private void SetError(string? message) => _errorText.Text = message ?? "";

    private void FilterTemplates()
    {
        var query = _searchBox.Text?.Trim() ?? "";
        var filtered = _allOptions
            .Where(o => query.Length == 0
                        || o.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || o.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var previousKey = _selected?.Key;
        _templateList.Items.Clear();
        foreach (var option in filtered)
        {
            var (geometry, brush) = Icons.ForTemplate(option.Key);
            var icon = new PathIcon { Data = geometry, Foreground = brush, Width = 18, Height = 18 };
            var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            texts.Children.Add(new TextBlock { Text = option.Label });
            texts.Children.Add(new TextBlock
            {
                Text = option.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#999999")),
                TextWrapping = TextWrapping.Wrap,
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { icon, texts } };
            _templateList.Items.Add(new ListBoxItem { Content = row, Tag = option });
        }

        var target = previousKey == null
            ? 0
            : filtered.FindIndex(o => o.Key == previousKey);
        if (_templateList.ItemCount > 0)
            _templateList.SelectedIndex = target >= 0 ? target : 0;
        else
        {
            _selected = null;
            UpdateDetails();
        }
    }

    private void OnTemplateSelected()
    {
        if (_templateList.SelectedItem is ListBoxItem { Tag: NewProjectService.TemplateOption option })
            _selected = option;

        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_selected == null)
        {
            _detailIcon.Data = null;
            _detailName.Text = "";
            _detailDesc.Text = "";
            return;
        }

        var (geometry, brush) = Icons.ForTemplate(_selected.Key);
        _detailIcon.Data = geometry;
        _detailIcon.Foreground = brush;
        _detailName.Text = _selected.Label;
        _detailDesc.Text = _selected.Description;
    }

    private void SyncSolutionName()
    {
        if (_solutionEdited) return;
        _syncingSolution = true;
        _solutionBox.Text = _nameBox.Text?.Trim() ?? "";
        _syncingSolution = false;
    }

    private void Create()
    {
        if (_selected == null)
        {
            SetError("请先选择模板");
            return;
        }

        var projectName = _nameBox.Text?.Trim() ?? "";
        var location = _locationBox.Text?.Trim() ?? "";
        var solutionName = _solutionBox.Text?.Trim() ?? "";

        if (projectName.Length == 0)
        {
            SetError("请输入项目名称");
            return;
        }
        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetError("项目名称包含非法字符");
            return;
        }
        if (solutionName.Length == 0)
        {
            SetError("请输入解决方案名称");
            return;
        }
        if (solutionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetError("解决方案名称包含非法字符");
            return;
        }
        if (location.Length == 0)
        {
            SetError("请选择位置");
            return;
        }

        try
        {
            var tfm = _tfmBox.SelectedItem?.ToString();
            var result = NewProjectService.CreateWithSolution(
                _selected.Key, projectName, solutionName, location, _sameDirBox.IsChecked == true, tfm);
            Close(result);
        }
        catch (Exception ex)
        {
            SetError($"创建失败：{ex.Message}");
        }
    }
}
