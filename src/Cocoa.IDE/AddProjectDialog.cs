using Avalonia.Controls;
using Avalonia.Layout;
using Cocoa.IDE.Services;

namespace Cocoa.IDE;

/// <summary>“向当前解决方案添加新建项目”对话框：选择模板 + 输入项目名。</summary>
public sealed class AddProjectDialog : Window
{
    private readonly ComboBox _templates;
    private readonly TextBox _name;

    public AddProjectDialog()
    {
        Title = "新建项目";
        Width = 440;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var specs = NewProjectService.LoadSpecs()
            .Where(s => s.Special != "Solution")
            .ToList();

        var root = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 10 };
        root.Children.Add(new TextBlock { Text = "将新建项目添加到当前解决方案：" });

        root.Children.Add(new TextBlock { Text = "模板", Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        _templates = new ComboBox
        {
            ItemsSource = specs.Select(s => s.Label).ToList(),
            SelectedIndex = specs.Count > 0 ? 0 : -1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        root.Children.Add(_templates);

        root.Children.Add(new TextBlock { Text = "项目名称", Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        _name = new TextBox { Watermark = "MyLibrary" };
        root.Children.Add(_name);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
        };
        var ok = new Button { Content = "确定", MinWidth = 86 };
        ok.Click += (_, _) =>
        {
            var label = _templates.SelectedItem as string;
            var spec = specs.FirstOrDefault(s => s.Label == label) ?? specs.FirstOrDefault();
            var name = _name.Text?.Trim();
            if (spec == null || string.IsNullOrWhiteSpace(name)) return;
            Close(new NewProjectIntoResult(spec.Key, name));
        };
        var cancel = new Button { Content = "取消", MinWidth = 86 };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        _name.AttachedToVisualTree += (_, _) => _name.Focus();
    }
}

public sealed record NewProjectIntoResult(string Template, string Name);
