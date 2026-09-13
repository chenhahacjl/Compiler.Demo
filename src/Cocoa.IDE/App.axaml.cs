using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Cocoa.IDE.Services;

namespace Cocoa.IDE;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 注册编译器后端（managed/native 发射 + 解释器 + C# 语言），否则 IDE 内构建报"后端未注册"
        CocoaServices.EnsureInitialized();

        // M6：启动时应用持久化主题（Dark/Light）
        ApplyTheme(SettingsService.Current.Settings.ThemeVariant);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>M6：应用主题变体（"Dark" / "Light"），DynamicResource 即时生效。</summary>
    public static void ApplyTheme(string? variant)
    {
        if (Current is not { } app) return;

        app.RequestedThemeVariant = string.Equals(variant, "Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}
