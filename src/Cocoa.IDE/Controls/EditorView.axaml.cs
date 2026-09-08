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

    public EditorView()
    {
        InitializeComponent();
        // 通过代码配置 AXAML 中不支持的属性
        TextEditor.WordWrap = false;
        TextEditor.ShowLineNumbers = true;
    }

    public void LoadFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var text = File.ReadAllText(filePath);
        TextEditor.Text = text;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var definition = ext switch
        {
            ".cs" => GetHighlighting("CSharp"),
            _     => GetHighlighting("Cocoa")
        };

        if (definition != null)
            TextEditor.SyntaxHighlighting = definition;

        TextEditor.Focus();
    }

    public void LoadText(string text, string? highlightingName = null)
    {
        TextEditor.Text = text;
        if (highlightingName != null)
        {
            var def = GetHighlighting(highlightingName);
            if (def != null) TextEditor.SyntaxHighlighting = def;
        }
    }

    public string GetText() => TextEditor.Text ?? "";

    public int GetCaretLine() => TextEditor.TextArea.Caret.Line;

    public int GetCaretColumn() => TextEditor.TextArea.Caret.Column;

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
