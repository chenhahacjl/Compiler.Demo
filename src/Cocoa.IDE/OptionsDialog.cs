using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cocoa.IDE.Services;
using Cocoa.IDE.ViewModels;

namespace Cocoa.IDE;

/// <summary>M6：工具 → 选项（VS 风格模态对话框）。编辑主题/编辑器字体字号/启动行为并写回设置。</summary>
public sealed class OptionsDialog : Window
{
    private readonly ComboBox _themeBox;
    private readonly TextBox _fontFamilyBox;
    private readonly NumericUpDown _fontSizeBox;
    private readonly CheckBox _startDialogBox;

    public OptionsDialog()
    {
        Title = "选项";
        Width = 560;
        Height = 400;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrushes.Brush("EditorBackgroundBrush", "#1E1E1E");

        var settings = SettingsService.Current.Settings;

        _themeBox = new ComboBox
        {
            ItemsSource = new[] { "深色", "浅色" },
            SelectedIndex = settings.ThemeVariant == "Light" ? 1 : 0,
            Width = 200,
        };

        _fontFamilyBox = new TextBox { Text = settings.EditorFontFamily, Width = 320 };

        _fontSizeBox = new NumericUpDown
        {
            Value = (decimal)(settings.EditorFontSize > 1 ? settings.EditorFontSize : 14),
            Minimum = 8,
            Maximum = 40,
            Increment = 1,
            Width = 140,
        };

        _startDialogBox = new CheckBox
        {
            Content = "启动时显示最近/推荐项目窗口",
            IsChecked = settings.ShowStartDialog,
        };

        var clearBtn = new Button { Content = "清除最近列表", MinWidth = 120 };
        clearBtn.Click += (_, _) =>
        {
            settings.RecentProjects.Clear();
            settings.RecentFiles.Clear();
            SettingsService.Current.Save();
        };

        var okBtn = new Button { Content = "确定", MinWidth = 88, IsDefault = true };
        okBtn.Click += (_, _) => ApplyAndClose();

        var cancelBtn = new Button { Content = "取消", MinWidth = 88, IsCancel = true };
        cancelBtn.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Header("外观"),
                Row("主题", _themeBox),
                Row("编辑器字体", _fontFamilyBox),
                Row("编辑器字号", _fontSizeBox),
                Header("启动"),
                _startDialogBox,
                Row("最近列表", clearBtn),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { okBtn, cancelBtn },
                },
            },
        };
    }

    private void ApplyAndClose()
    {
        var settings = SettingsService.Current.Settings;
        settings.ThemeVariant = _themeBox.SelectedIndex == 1 ? "Light" : "Dark";
        settings.EditorFontFamily = string.IsNullOrWhiteSpace(_fontFamilyBox.Text)
            ? settings.EditorFontFamily
            : _fontFamilyBox.Text!;
        settings.EditorFontSize = (double)(_fontSizeBox.Value ?? 14);
        settings.ShowStartDialog = _startDialogBox.IsChecked == true;
        SettingsService.Current.Save();

        App.ApplyTheme(settings.ThemeVariant);
        SettingsService.Current.NotifyFontChanged();
        Dispatcher.UIThread.Post(() => MainViewModel.Shared?.NotifyThemeApplied());

        Close(true);
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12,
        Children =
        {
            new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };
}
