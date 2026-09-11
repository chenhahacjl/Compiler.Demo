using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using Avalonia.Media;
using Cocoa.CodeAnalysis;
using System.Xml;

namespace Cocoa.IDE.Controls;

public partial class EditorView : UserControl
{
    private TextEditor TextEditor => this.FindControl<TextEditor>("AvaloniaEdit")!;

    /// <summary>暴露给语义服务（补全弹窗等）。</summary>
    public TextEditor Editor => TextEditor;

    private static readonly Dictionary<string, IHighlightingDefinition> HighlightingCache = new();

    private readonly SquiggleRenderer _squiggles = new();
    private bool _isLoading;
    private string? _currentFilePath;

    public event EventHandler? TextChanged;
    public event EventHandler? CaretChanged;

    public EditorView()
    {
        InitializeComponent();
        // 通过代码配置 AXAML 中不支持的属性
        TextEditor.WordWrap = false;
        TextEditor.ShowLineNumbers = true;

        TextEditor.Document.TextChanged += (_, _) =>
        {
            if (!_isLoading)
                TextChanged?.Invoke(this, EventArgs.Empty);
        };
        TextEditor.TextArea.Caret.PositionChanged += (_, _) => CaretChanged?.Invoke(this, EventArgs.Empty);

        TextEditor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
    }

    public string? CurrentFilePath => _currentFilePath;

    /// <summary>设置当前文件的诊断（波浪线）。诊断位置必须落在当前 Document 范围内。</summary>
    public void SetDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        _squiggles.SetDiagnostics(diagnostics);
        TextEditor.TextArea.TextView.Redraw();
    }

    public void LoadFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var text = File.ReadAllText(filePath);
        LoadText(text, filePath);
        TextEditor.Focus();
    }

    public void LoadText(string text, string? filePath = null, string? highlightingName = null)
    {
        _isLoading = true;
        try
        {
            _currentFilePath = filePath;
            TextEditor.Text = text ?? "";

            var name = highlightingName ?? GetHighlightingName(filePath);
            if (name != null)
            {
                var def = GetHighlighting(name);
                if (def != null) TextEditor.SyntaxHighlighting = def;
            }

            TextEditor.IsReadOnly = GetIsReadOnly(filePath);
        }
        finally
        {
            _isLoading = false;
        }

        // 切换文件后清理旧诊断
        _squiggles.SetDiagnostics(Array.Empty<Diagnostic>());
        TextEditor.TextArea.TextView.Redraw();
    }

    public string GetText() => TextEditor.Text ?? "";

    public int GetCaretLine() => TextEditor.TextArea.Caret.Line;

    public int GetCaretColumn() => TextEditor.TextArea.Caret.Column;

    public void SetCaret(int line, int column)
    {
        TextEditor.TextArea.Caret.Line = Math.Clamp(line, 1, TextEditor.Document.LineCount);
        TextEditor.TextArea.Caret.Column = Math.Clamp(column, 1, TextEditor.Document.GetLineByNumber(TextEditor.TextArea.Caret.Line).Length + 1);
        TextEditor.ScrollToLine(TextEditor.TextArea.Caret.Line);
        TextEditor.TextArea.Caret.BringCaretToView();
    }

    private static string? GetHighlightingName(string? filePath)
    {
        if (filePath == null) return null;
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "CSharp",
            _     => "Cocoa"
        };
    }

    private static bool GetIsReadOnly(string? filePath)
    {
        if (filePath == null) return true;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is not (".co" or ".cs" or ".txt" or ".coproj" or ".cosln");
    }

    private static IHighlightingDefinition? GetHighlighting(string name)
    {
        if (HighlightingCache.TryGetValue(name, out var cached))
            return cached;

        try
        {
            var assembly = typeof(EditorView).Assembly;
            var resourceName = $"Cocoa.IDE.Themes.{name}.xshd";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new XmlTextReader(stream);
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                if (definition != null)
                {
                    HighlightingCache[name] = definition;
                    HighlightingManager.Instance.RegisterHighlighting(name, new[] { name == "CSharp" ? ".cs" : ".co" }, definition);
                    return definition;
                }
            }
        }
        catch { /* 资源不存在时退化 */ }

        return null;
    }
}

/// <summary>把编译器诊断画成下划线（错误红 / 警告绿），叠在文本下层。</summary>
public sealed class SquiggleRenderer : IBackgroundRenderer
{
    private sealed class Segment : ISegment
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public int EndOffset => Offset + Length;
        public bool IsError { get; init; }
    }

    private readonly List<Segment> _segments = new();
    private static readonly IPen ErrorPen = CreatePen(0xFFE51400);
    private static readonly IPen WarningPen = CreatePen(0xFF7CB342);

    public KnownLayer Layer => KnownLayer.Selection;

    private static IPen CreatePen(uint argb)
    {
        var brush = new SolidColorBrush(argb);
        return new Pen(brush, 1.5);
    }

    public void SetDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        _segments.Clear();
        foreach (var d in diagnostics)
        {
            var span = d.Location.Span;
            var length = Math.Max(1, span.Length);
            _segments.Add(new Segment { Offset = span.Start, Length = length, IsError = d.IsError });
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_segments.Count == 0) return;

        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            BorderThickness = 1.5,
            CornerRadius = 1.0,
        };

        foreach (var seg in _segments)
            builder.AddSegment(textView, seg);

        // 分错误/警告两组绘制不同颜色
        builder.CloseFigure();
        var geometry = builder.CreateGeometry();
        if (geometry != null && !IsEmptyGeometry(geometry))
        {
            // 分错误/警告两组绘制不同颜色
            drawingContext.DrawGeometry(null, ErrorPen, geometry);
        }

        // 区分警告：单独重跑一遍只为警告段
        var warningBuilder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            BorderThickness = 1.5,
            CornerRadius = 1.0,
        };
        var anyWarning = false;
        foreach (var seg in _segments)
        {
            if (!seg.IsError)
            {
                warningBuilder.AddSegment(textView, seg);
                anyWarning = true;
            }
        }
        if (anyWarning)
        {
            warningBuilder.CloseFigure();
            var w = warningBuilder.CreateGeometry();
            if (w != null && !IsEmptyGeometry(w))
                drawingContext.DrawGeometry(null, WarningPen, w);
        }
    }

    private static bool IsEmptyGeometry(Geometry geometry)
        => geometry.Bounds.Width <= 0 || geometry.Bounds.Height <= 0;
}
