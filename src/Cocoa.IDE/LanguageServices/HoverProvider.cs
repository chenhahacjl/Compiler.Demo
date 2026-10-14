using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.IDE.LanguageServices;

public sealed record HoverInfo(string Display, string FileName, int Line, int Column, int EndLine, int EndColumn);

/// <summary>悬停提示：将光标位置解析为符号并生成签名/类型显示。</summary>
public static class HoverProvider
{
    public static HoverInfo? GetHover(SemanticModelHost host, int offset)
    {
        var resolved = host.ResolveSymbolAt(offset);
        if (resolved is not { } r || r.symbol == null) return null;
        var symbol = r.symbol;

        var display = BuildDisplay(symbol);
        if (display == null) return null;

        var node = r.node;
        var tree = host.Tree;
        if (tree == null || node == null) return null;

        var loc = node.Location;
        if (loc.Text == null) return null;

        return new HoverInfo(
            display,
            loc.FileName,
            loc.StartLine + 1,
            loc.StartCharacter + 1,
            loc.EndLine + 1,
            loc.EndCharacter + 1);
    }

    private static string? BuildDisplay(Symbol symbol)
    {
        // 用符号自身的 ToString()（经 SymbolPrinter 美化）+ 文档注释摘要
        var signature = symbol.ToString();
        if (string.IsNullOrEmpty(signature)) return null;

        var doc = symbol.DocumentationText;
        if (string.IsNullOrWhiteSpace(doc)) return signature;

        // 提取 <summary> 摘要：取首行或 <summary> 内容；无标签则整段裁剪。
        var summary = ExtractSummary(doc);
        if (string.IsNullOrEmpty(summary)) return signature;

        // 多行折叠：ToolTip 单行文本换行显示，保留语义
        return signature + "\n" + summary;
    }

    private static string? ExtractSummary(string doc)
    {
        // 支持 `<summary>…</summary>` 与裸文本两种形态；取首个标签内容或整段首行
        var start = doc.IndexOf("<summary>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var end = doc.IndexOf("</summary>", start, StringComparison.OrdinalIgnoreCase);
            var len = end < 0 ? doc.Length - (start + "<summary>".Length) : end - (start + "<summary>".Length);
            var body = doc.Substring(start + "<summary>".Length, len);
            return Collapse(body);
        }

        return Collapse(doc);
    }

    /// <summary>裁剪首行/多行空白折叠；空返回 null。</summary>
    private static string? Collapse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        // 折叠换行与连续空白为单空格（保留段落语义）
        var sb = new System.Text.StringBuilder(trimmed.Length);
        var pendingSpace = false;
        foreach (var c in trimmed)
        {
            if (c == '\r' || c == '\n' || char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
            else
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
            }
        }

        return sb.ToString();
    }
}