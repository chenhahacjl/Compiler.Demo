using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Cocoa.CodeAnalysis.Authoring;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.IDE.Controls;

/// <summary>M6a3：基于编译器 <see cref="Classifier"/> 的语义着色。
/// 预分类整棵语法树并按行二分着色，颜色随主题（Dark/Light）取自命名画刷。</summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private ImmutableArray<ClassifiedSpan> _spans = ImmutableArray<ClassifiedSpan>.Empty;
    private ThemeVariant? _paletteTheme;
    private readonly Dictionary<Classification, IBrush> _brushes = new();

    /// <summary>替换语法树（null 清空着色）。调用方负责随后 Redraw。</summary>
    public void SetTree(SyntaxTree? tree)
    {
        _spans = tree == null
            ? ImmutableArray<ClassifiedSpan>.Empty
            : Classifier.Classify(tree, new TextSpan(0, tree.Text.Length));
    }

    /// <summary>主题切换后使调色板失效，下次着色重建。</summary>
    public void InvalidatePalette() => _paletteTheme = null;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.IsDefaultOrEmpty) return;
        EnsurePalette();

        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        // _spans 按 Start 升序：二分定位首个 End > lineStart 的段
        var lo = 0;
        var hi = _spans.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_spans[mid].Span.End <= lineStart) lo = mid + 1;
            else hi = mid;
        }

        for (var i = lo; i < _spans.Length && _spans[i].Span.Start < lineEnd; i++)
        {
            var span = _spans[i].Span;
            var start = Math.Max(span.Start, lineStart);
            var end = Math.Min(span.End, lineEnd);
            if (end <= start) continue;
            if (!_brushes.TryGetValue(_spans[i].Classification, out var brush)) continue;

            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private void EnsurePalette()
    {
        var theme = Application.Current?.ActualThemeVariant;
        if (_paletteTheme == theme && _brushes.Count > 0) return;

        _paletteTheme = theme;
        _brushes.Clear();
        _brushes[Classification.Keyword] = ThemeBrushes.Brush("SyntaxKeywordBrush", "#569CD6");
        _brushes[Classification.Identifier] = ThemeBrushes.Brush("SyntaxIdentifierBrush", "#DCDCDC");
        _brushes[Classification.Number] = ThemeBrushes.Brush("SyntaxNumberBrush", "#B5CEA8");
        _brushes[Classification.String] = ThemeBrushes.Brush("SyntaxStringBrush", "#D69D85");
        _brushes[Classification.Comment] = ThemeBrushes.Brush("SyntaxCommentBrush", "#57A64A");
        _brushes[Classification.Punctuation] = ThemeBrushes.Brush("SyntaxPunctuationBrush", "#DCDCDC");
        _brushes[Classification.Operator] = ThemeBrushes.Brush("SyntaxOperatorBrush", "#D4D4D4");
        _brushes[Classification.Text] = ThemeBrushes.Brush("SyntaxTextBrush", "#DCDCDC");
    }
}
