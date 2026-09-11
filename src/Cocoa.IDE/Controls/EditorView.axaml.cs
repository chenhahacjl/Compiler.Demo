using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using System.Xml;

namespace Cocoa.IDE.Controls;

public partial class EditorView : UserControl
{
    private TextEditor TextEditor => this.FindControl<TextEditor>("AvaloniaEdit")!;

    private static readonly Dictionary<string, IHighlightingDefinition> HighlightingCache = new();

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
    }

    public string? CurrentFilePath => _currentFilePath;

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
