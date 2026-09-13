using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cocoa.IDE.Services;

namespace Cocoa.IDE;

/// <summary>VS2022 两步式新建项目向导：
/// 步骤 1 选择项目类别（Console / Library / Cocoa Assembly / BlankSolution）；
/// 步骤 2 配置语言（Cocoa/C#）、后端（托管/原生）及名称/位置/解决方案/目标框架。
/// Close(NewProjectResult) 返回生成结果。</summary>
public sealed class NewProjectDialog : Window
{
    private static readonly string[] TargetFrameworks =
        { "net48", "net9.0", "net8.0", "net6.0", "netcoreapp3.1" };

    private static readonly string[] CategoryOrder =
        { "Console", "Library", "Cocoa Assembly", "Solution" };

    private static readonly string[] BackendLabels = { "托管 (Managed)", "原生 (Native)" };

    private readonly IReadOnlyList<NewProjectService.TemplateOption> _allOptions;
    private readonly List<string> _categories;

    private readonly StackPanel _step1Panel;
    private readonly Grid _step2Panel;
    private readonly TextBlock _headerText;

    private readonly ListBox _categoryList;
    private readonly PathIcon _detailIcon;
    private readonly TextBlock _detailName;
    private readonly TextBlock _detailDesc;

    private readonly ComboBox _languageBox;
    private readonly ComboBox _backendBox;
    private readonly TextBox _nameBox;
    private readonly TextBox _locationBox;
    private readonly TextBox _solutionBox;
    private readonly CheckBox _sameDirBox;
    private readonly ComboBox _tfmBox;

    private readonly TextBlock _errorText;
    private readonly Button _backBtn;
    private readonly Button _nextBtn;
    private readonly Button _createBtn;

    private string? _selectedCategory;
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
        _categories = OrderCategories(_allOptions.Select(o => o.Category).Distinct());

        // ── 步骤 1：类别选择 ──
        _categoryList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _categoryList.SelectionChanged += (_, _) => OnCategorySelected();

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
        Grid.SetColumn(_categoryList, 0);
        Grid.SetColumn(detailPanel, 1);
        step1Grid.Children.Add(_categoryList);
        step1Grid.Children.Add(detailPanel);

        _step1Panel = new StackPanel { Children = { step1Grid } };

        // ── 步骤 2：配置 ──
        _languageBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
        _languageBox.SelectionChanged += (_, _) => UpdateDetails();

        _backendBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
        foreach (var b in BackendLabels) _backendBox.Items.Add(b);
        _backendBox.SelectedIndex = 0;

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

        PopulateCategories();
        if (_categoryList.ItemCount > 0)
            _categoryList.SelectedIndex = 0;
    }

    private static List<string> OrderCategories(IEnumerable<string> categories)
    {
        var present = categories.ToList();
        var ordered = new List<string>();
        foreach (var c in CategoryOrder)
            if (present.Remove(c)) ordered.Add(c);
        ordered.AddRange(present);
        return ordered;
    }

    private Grid BuildForm(Grid locationRow)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
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

        AddRow(0, "语言", _languageBox);
        AddRow(1, "后端", _backendBox);
        AddRow(2, "项目名称", _nameBox);
        AddRow(3, "位置", locationRow);
        AddRow(4, "解决方案名称", _solutionBox);
        AddRow(5, "目标框架", _tfmBox);

        Grid.SetRow(_sameDirBox, 6);
        Grid.SetColumn(_sameDirBox, 1);
        _sameDirBox.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(_sameDirBox);

        return grid;
    }

    private void SetStep(int step)
    {
        SetError(null);
        if (step == 1 && _selectedCategory == null)
        {
            SetError("请先选择项目类别");
            return;
        }

        var onConfig = step == 1;
        _step1Panel.IsVisible = !onConfig;
        _step2Panel.IsVisible = onConfig;
        _backBtn.IsVisible = onConfig;
        _createBtn.IsVisible = onConfig;
        _nextBtn.IsVisible = !onConfig;
        _headerText.Text = onConfig ? "配置新项目" : "创建新项目";

        if (onConfig)
        {
            PopulateLanguages();
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
                _nameBox.Text = IsSolutionCategory ? "MySolution" : "MyApp";
        }
    }

    private bool IsSolutionCategory =>
        string.Equals(_selectedCategory, "Solution", StringComparison.Ordinal);

    private void SetError(string? message) => _errorText.Text = message ?? "";

    private void PopulateCategories()
    {
        _categoryList.Items.Clear();
        foreach (var category in _categories)
        {
            var option = _allOptions.FirstOrDefault(o => o.Category == category);
            var (geometry, brush) = Icons.ForTemplate(option?.Key ?? "console");
            var icon = new PathIcon { Data = geometry, Foreground = brush, Width = 18, Height = 18 };
            var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            texts.Children.Add(new TextBlock { Text = category });
            texts.Children.Add(new TextBlock
            {
                Text = option?.Description ?? "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#999999")),
                TextWrapping = TextWrapping.Wrap,
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { icon, texts } };
            _categoryList.Items.Add(new ListBoxItem { Content = row, Tag = category });
        }
    }

    private void PopulateLanguages()
    {
        var languages = _allOptions
            .Where(o => o.Category == _selectedCategory)
            .Select(o => o.Language)
            .Distinct()
            .ToList();

        _languageBox.Items.Clear();
        foreach (var lang in languages) _languageBox.Items.Add(lang);
        _languageBox.SelectedIndex = languages.Count > 0 ? 0 : -1;
        _languageBox.IsEnabled = languages.Count > 1;

        _backendBox.IsEnabled = !IsSolutionCategory;
        _tfmBox.IsEnabled = !IsSolutionCategory;
    }

    private NewProjectService.TemplateOption? ResolveTemplate()
    {
        if (_selectedCategory == null) return null;
        if (IsSolutionCategory)
            return _allOptions.FirstOrDefault(o => o.IsSolution);

        var language = _languageBox.SelectedItem?.ToString();
        return _allOptions.FirstOrDefault(o => o.Category == _selectedCategory && o.Language == language)
               ?? _allOptions.FirstOrDefault(o => o.Category == _selectedCategory);
    }

    private void OnCategorySelected()
    {
        if (_categoryList.SelectedItem is ListBoxItem { Tag: string category })
            _selectedCategory = category;

        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var option = ResolveTemplate();
        if (option == null)
        {
            _detailIcon.Data = null;
            _detailName.Text = _selectedCategory ?? "";
            _detailDesc.Text = "";
            return;
        }

        var (geometry, brush) = Icons.ForTemplate(option.Key);
        _detailIcon.Data = geometry;
        _detailIcon.Foreground = brush;
        _detailName.Text = option.Label;
        _detailDesc.Text = option.Description;
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
        var option = ResolveTemplate();
        if (option == null)
        {
            SetError("请先选择项目类别");
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
            var backend = IsSolutionCategory ? null : (_backendBox.SelectedIndex == 1 ? "Native" : "Managed");
            var result = NewProjectService.CreateWithSolution(
                option.Key, projectName, solutionName, location, _sameDirBox.IsChecked == true, tfm, backend);
            Close(result);
        }
        catch (Exception ex)
        {
            SetError($"创建失败：{ex.Message}");
        }
    }
}
