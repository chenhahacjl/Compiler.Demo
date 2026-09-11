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
        // 用符号自身的 ToString()（经 SymbolPrinter 美化）
        return symbol.ToString();
    }
}