using System.Reflection;
using System.Runtime.Loader;
using Avalonia;

namespace Cocoa.IDE;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // VS/dotnet 风格目录布局：编译器程序集部署在 exe 旁的 `Compiler/` 子目录
        // （`dotnet publish` 聚合，见 tools/publish-ide.ps1）。默认 ALC 从 BaseDirectory 解析
        // 依赖程序集；子目录内的编译器 DLL 需经 Resolving 事件按名加载，缺失（开发期输出在
        // 根目录）时回退默认解析。SDK（System*.coa）由 SystemLibrary 经 ./SDK 发现。
        RegisterCompilerProbingPath();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void RegisterCompilerProbingPath()
    {
        var compilerDir = Path.Combine(AppContext.BaseDirectory, "Compiler");
        if (!Directory.Exists(compilerDir)) return;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var simpleName = name.Name;
            if (simpleName == null || !simpleName.StartsWith("Cocoa.", StringComparison.Ordinal))
                return null;

            var candidate = Path.Combine(compilerDir, simpleName + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
