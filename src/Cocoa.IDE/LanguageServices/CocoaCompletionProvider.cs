using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.IDE.LanguageServices;

public enum CompletionKind { Symbol, Keyword, Builtin }

public sealed class CocoaCompletionItem : ICompletionData
{
    public CocoaCompletionItem(string text, string? description = null, string? insertSuffix = null, CompletionKind kind = CompletionKind.Symbol)
    {
        Text = text;
        Description = description;
        InsertSuffix = insertSuffix;
        Kind = kind;
    }

    public CompletionKind Kind { get; }
    private string? InsertSuffix { get; }
    public IImage? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object? Description { get; set; }
    public double Priority => Kind switch
    {
        CompletionKind.Keyword => 0,
        CompletionKind.Builtin => 1,
        _ => 2,
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // 替换待补全段为 Text(+suffix)
        var replaceText = Text + (InsertSuffix ?? "");
        textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, replaceText);
    }
}

/// <summary>Ctrl+Space 补全：作用域补全 + 成员访问补全。算法镜像 REPL CocoaCompletionProvider。</summary>
public static class CocoaCompletionProvider
{
    private static readonly string[] Keywords =
    {
        "function", "let", "var", "const", "if", "else", "while", "for", "do",
        "switch", "case", "default", "break", "continue", "return", "class",
        "struct", "enum", "interface", "extends", "implements", "new", "this",
        "base", "null", "true", "false", "static", "abstract", "virtual",
        "override", "sealed", "extern", "import", "using", "namespace",
        "event", "delegate", "try", "catch", "finally", "throw", "is", "as",
        "typeof", "sizeof", "ref", "out", "in", "params",
    };

    public static List<CocoaCompletionItem> GetCompletions(SemanticModelHost host, int offset, string text)
    {
        var items = new List<CocoaCompletionItem>();
        var tree = host.Tree;
        var compilation = host.Compilation;
        if (tree == null || compilation == null) return items;

        // 1) 成员访问：'前缀.' 之后补全
        var (dotOffset, prefix) = FindMemberAccess(tree, offset);
        if (dotOffset >= 0)
        {
            if (TryGetMembers(host, tree, compilation, dotOffset, out var memberItems))
            {
                items.AddRange(memberItems
                    .Where(i => string.IsNullOrEmpty(prefix) || i.Text.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderByDescending(i => i.Priority));
                return items;
            }
        }

        // 2) 作用域补全
        var scopeItems = BuildScopeItems(host);
        items.AddRange(scopeItems
            .Where(i => string.IsNullOrEmpty(prefix) || i.Text.StartsWith(prefix, StringComparison.Ordinal)));

        // 3) 关键字补全
        if (string.IsNullOrEmpty(prefix))
        {
            items.AddRange(Keywords.Select(k => new CocoaCompletionItem(k, null, null, CompletionKind.Keyword)));
        }

        return items.DistinctBy(i => i.Text).OrderByDescending(i => i.Priority).ThenBy(i => i.Text).ToList();
    }

    private static List<CocoaCompletionItem> BuildScopeItems(SemanticModelHost host)
    {
        var result = new List<CocoaCompletionItem>();
        foreach (var sym in host.GetScopeSymbols())
        {
            var desc = sym.ToString();
            var method = sym is FunctionSymbol f && !f.IsPropertyAccessor && !f.IsConstructor;
            result.Add(new CocoaCompletionItem(sym.Name, desc, method && !desc.Contains("(") ? "()" : null,
                sym is FunctionSymbol { ContainingClass: null } ? CompletionKind.Symbol : CompletionKind.Symbol));
        }

        // 内建类型
        foreach (var t in new[] { "int", "long", "float", "double", "string", "bool", "byte", "char", "object", "any" })
        {
            result.Add(new CocoaCompletionItem(t, $"type: {t}", null, CompletionKind.Builtin));
        }
        return result;
    }

    /// <summary>从 offset 往前找 '标识符.' 结构，返回 (dotOffset, 前缀)。</summary>
    private static (int, string) FindMemberAccess(SyntaxTree tree, int offset)
    {
        var text = tree.Text;
        if (text.Length == 0) return (-1, "");

        // 前一个非空白字符
        var i = offset - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0 || text[i] != '.') return (-1, "");

        // dotOffset
        var dotOffset = i;

        // 前缀：dot 之后
        var j = dotOffset + 1;
        var prefix = "";
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
        {
            prefix += text[j];
            j++;
        }

        return (dotOffset, prefix);
    }

    private static bool TryGetMembers(SemanticModelHost host, SyntaxTree tree, Compilation compilation,
        int dotOffset, out List<CocoaCompletionItem> members)
    {
        members = new List<CocoaCompletionItem>();

        // 定位 dot token
        SyntaxToken? dotToken = tree.Root.DescendantTokens()
            .FirstOrDefault(t => !t.IsMissing && t.Kind == SyntaxKind.DotToken &&
                                 t.Span.Start == dotOffset);
        if (dotToken is null or { IsMissing: true }) return false;

        var receiver = dotToken.Parent?.GetChildren().FirstOrDefault(c => c is not SyntaxToken);
        if (receiver == null) return false;

        var model = compilation.GetSemanticModel(tree);
        var type = model.GetTypeInfo(receiver);
        var symbol = model.GetSymbolInfo(receiver);
        var staticContext = symbol is TypeSymbol;

        if (type == null || type == TypeSymbol.Error)
        {
            if (symbol is TypeSymbol ts)
                type = ts;
            else
                return false;
        }

        foreach (var (member, insertText, isMethod) in SemanticModelHost.GetMembers(type, staticContext))
        {
            var desc = member.ToString() ?? member.Name;
            members.Add(new CocoaCompletionItem(insertText, desc, isMethod ? "()" : null));
        }

        // 命名空间成员补全
        if (symbol is NamespaceSymbol ns)
        {
            foreach (var sub in ns.GetNamespaceMembers())
                members.Add(new CocoaCompletionItem(sub.Name, "namespace: " + sub.FullName));
            foreach (var t in ns.GetTypeMembers())
                members.Add(new CocoaCompletionItem(t.Name, "type: " + (t as NamedTypeSymbol)?.FullName ?? t.Name));
        }

        return members.Count > 0;
    }
}