using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Cocoa.IDE.Services;

namespace Cocoa.IDE;

/// <summary>新建项目向导对话框：模板选择 + 名称 + 输出目录。Close(string[]) 返回生成的文件路径。</summary>
public sealed class NewProjectDialog : Window
{
    private readonly ComboBox _templateBox;
    private readonly TextBox _nameBox;
    private readonly TextBox _dirBox;
    private readonly TextBlock _descText;

    public NewProjectDialog()
    {
        Title = "新建项目";
        Width = 520;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _templateBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        foreach (var t in NewProjectService.Templates)
            _templateBox.Items.Add(t);
        _templateBox.SelectedIndex = 0;
        _templateBox.SelectionChanged += (_, _) => UpdateDescription();

        _descText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 0),
        };

        _nameBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 0),
        };
        _nameBox.TextChanged += (_, _) => UpdateDescription();

        _dirBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 0),
            Text = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CocoaProjects"),
        };

        var browseBtn = new Button { Content = "浏览…", Margin = new Thickness(4, 0, 0, 0) };
        browseBtn.Click += async (_, _) =>
        {
            var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择输出目录",
                AllowMultiple = false,
            });
            if (folder.Count > 0)
                _dirBox.Text = folder[0].TryGetLocalPath() ?? _dirBox.Text;
        };

        var dirRow = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(_dirBox, 0);
        Grid.SetColumn(browseBtn, 1);
        dirRow.Children.Add(_dirBox);
        dirRow.Children.Add(browseBtn);

        var createBtn = new Button { Content = "创建", MinWidth = 90, IsDefault = true };
        createBtn.Click += async (_, _) => await CreateAsync();
        var cancelBtn = new Button { Content = "取消", MinWidth = 90 };
        cancelBtn.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(createBtn);

        var form = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 4,
        };
        form.Children.Add(new TextBlock { Text = "模板" });
        form.Children.Add(_templateBox);
        form.Children.Add(_descText);
        form.Children.Add(new TextBlock { Text = "名称", Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(_nameBox);
        form.Children.Add(new TextBlock { Text = "输出目录", Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(dirRow);
        form.Children.Add(buttons);

        Content = new ScrollViewer { Content = form };

        UpdateDescription();
    }

    private void UpdateDescription()
    {
        var t = SelectedTemplate;
        _descText.Text = NewProjectService.Describe(t);
        if (string.IsNullOrWhiteSpace(_nameBox.Text) && !string.IsNullOrEmpty(t))
            _nameBox.Text = t switch
            {
                "csharp" => "MyApp",
                "solution" => "MySolution",
                _ => "MyApp",
            };
    }

    private string SelectedTemplate => _templateBox.SelectedItem?.ToString() ?? "console";

    private async Task CreateAsync()
    {
        var template = SelectedTemplate;
        var name = _nameBox.Text?.Trim() ?? "";
        var dir = _dirBox.Text?.Trim() ?? "";

        if (name.Length == 0)
        {
            _descText.Text = "请输入项目名称";
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _descText.Text = "名称包含非法字符";
            return;
        }
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            _descText.Text = "输出目录不存在";
            return;
        }

        var targetDir = Path.Combine(dir, name);
        try
        {
            var created = NewProjectService.Create(template, name, targetDir);
            Close(created.ToArray());
        }
        catch (Exception ex)
        {
            _descText.Text = $"创建失败：{ex.Message}";
        }
    }
}