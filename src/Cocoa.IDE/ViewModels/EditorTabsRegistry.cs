using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis;

namespace Cocoa.IDE.ViewModels;

/// <summary>全局编辑器标签集合注册表。多窗口（主窗口 + 浮窗）各自持有 <see cref="EditorTabsViewModel"/>，
/// 诊断结果按文件路径派发到所有包含该文件的窗口。</summary>
public static class EditorTabsRegistry
{
    private static readonly List<EditorTabsViewModel> _sets = new();

    /// <summary>任一窗口的实时诊断就绪后广播，供各窗口重绘波浪线。</summary>
    public static event Action<string, ImmutableArray<Diagnostic>>? DiagnosticsApplied;

    /// <summary>请求定位到某标签的行列（错误列表双击等），各窗口自行判断是否拥有该标签。</summary>
    public static event Action<EditorTabViewModel, int, int>? NavigateRequested;

    public static void RequestNavigate(EditorTabViewModel tab, int line, int col)
    {
        NavigateRequested?.Invoke(tab, line, col);
    }

    public static void Register(EditorTabsViewModel set)
    {
        lock (_sets) _sets.Add(set);
    }

    public static void Unregister(EditorTabsViewModel set)
    {
        lock (_sets) _sets.Remove(set);
    }

    public static IReadOnlyList<EditorTabsViewModel> All
    {
        get
        {
            lock (_sets) return _sets.ToList();
        }
    }

    public static void PublishDiagnostics(string filePath, ImmutableArray<Diagnostic> diagnostics)
    {
        DiagnosticsApplied?.Invoke(filePath, diagnostics);
    }
}
