using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using CocoaUsing = Cocoa.CodeAnalysis.Cocoa.Syntax.UsingDirectiveSyntax;
using CSharpUsing = Cocoa.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax;

namespace Cocoa.IDE.LanguageServices;

public enum CompletionKind { Snippet, Symbol, Namespace, Builtin, Keyword }

public sealed class CocoaCompletionItem : ICompletionData
{
    public CocoaCompletionItem(string text, string? description = null, string? insertSuffix = null,
        CompletionKind kind = CompletionKind.Symbol, string? snippet = null)
    {
        Text = text;
        Description = description;
        InsertSuffix = insertSuffix;
        Kind = kind;
        Snippet = snippet;
    }

    public CompletionKind Kind { get; }
    private string? InsertSuffix { get; }
    public string? Snippet { get; }
    public IImage? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object? Description { get; set; }
    public double Priority => Kind switch
    {
        CompletionKind.Snippet => 4,
        CompletionKind.Symbol => 3,
        CompletionKind.Namespace => 2,
        CompletionKind.Builtin => 1,
        _ => 0,
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // 片段：插入展开文本，并把光标落到 '$' 占位处
        if (Snippet != null)
        {
            var caretInSnippet = Snippet.IndexOf('$');
            var body = Snippet.Replace("$", "");
            textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, body);
            if (caretInSnippet >= 0)
                textArea.Caret.Offset = completionSegment.Offset + caretInSnippet;
            return;
        }

        textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, Text + (InsertSuffix ?? ""));
    }
}

/// <summary>VS 式语境补全：using / 成员访问 / 类型位置 / 声明名抑制 / 语句-表达式关键字 / 片段。
/// 算法镜像 REPL <c>CocoaCompletionProvider</c>，并接入 <see cref="SemanticModelHost"/> 的工程上下文与缓存模型。</summary>
public static class CocoaCompletionProvider
{
    private static readonly string[] BuiltinTypeNames =
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "float", "double",
        "bool", "char", "string", "object", "any", "nint", "nuint", "i32", "i64",
    };

    private static readonly (string Text, string Body)[] CocoaSnippets =
    {
        ("if", "if ($)\n{\n}"),
        ("else", "else\n{\n}"),
        ("for", "for var i = 0 to $\n{\n}"),
        ("while", "while ($)\n{\n}"),
        ("switch", "switch ($)\n{\n    case : break;\n    default: break;\n}"),
        ("function", "function $()\n{\n}"),
        ("class", "class $\n{\n}"),
        ("try", "try\n{\n}\ncatch\n{\n}"),
    };

    private static readonly (string Text, string Body)[] CSharpSnippets =
    {
        ("if", "if ($)\n{\n}"),
        ("else", "else\n{\n}"),
        ("for", "for (var i = 0; i < $; i++)\n{\n}"),
        ("foreach", "foreach (var item in $)\n{\n}"),
        ("while", "while ($)\n{\n}"),
        ("switch", "switch ($)\n{\n    case : break;\n    default: break;\n}"),
        ("function", "function $()\n{\n}"),
        ("class", "class $\n{\n}"),
        ("try", "try\n{\n}\ncatch\n{\n}"),
    };

    private static readonly string[] StatementKeywords =
    {
        "abstract", "as", "base", "break", "case", "catch", "cdecl", "class",
        "const", "constructor", "continue", "default", "delegate", "do", "else",
        "enum", "event", "extends", "extern", "facade", "false", "finally",
        "for", "foreach", "function", "get", "if", "import", "in", "interface",
        "internal", "is", "let", "namespace", "new", "null", "out", "override",
        "partial", "private", "property", "protected", "public", "readonly",
        "ref", "return", "sealed", "set", "static", "stdcall", "step", "struct",
        "switch", "syscall", "this", "throw", "to", "true", "try", "using",
        "var", "virtual", "when", "where", "while",
    };

    private static readonly string[] ExpressionKeywords = { "true", "false", "null", "new" };

    public static List<CocoaCompletionItem> GetCompletions(SemanticModelHost host, int offset, string text, string dialect)
    {
        var items = new List<CocoaCompletionItem>();
        var tree = host.Tree;
        var compilation = host.Compilation;
        if (tree == null || compilation == null) return items;

        // 触发一次绑定，使 GlobalNamespace/GlobalScope 反映源码声明的命名空间与符号
        try { _ = compilation.GlobalScope; } catch { }

        try
        {
            // 1) using 语句上下文：命名空间补全
            var usingItems = GetUsingCompletions(compilation, text, offset);
            if (usingItems != null) return usingItems;

            var prefix = ExtractPrefix(text, offset);

            // 2) 成员访问上下文
            var (dotOffset, memberPrefix) = ExtractMemberAccess(text, offset);
            if (dotOffset >= 0)
            {
                var memberItems = GetMemberCompletions(host, tree, compilation, text, dotOffset, memberPrefix);
                if (memberItems.Count > 0) return memberItems;
            }

            // 3) 声明名输入位置：补全无意义
            if (IsDeclarationNameContext(text, offset)) return items;

            // 4) 类型位置（':'/new 之后）：仅类型 + 内建类型
            if (IsTypePosition(text, offset))
            {
                foreach (var sym in host.GetScopeSymbols())
                    if (sym is NamedTypeSymbol type && Matches(type.Name, prefix))
                        items.Add(new CocoaCompletionItem(type.Name, type.ToString(), null, CompletionKind.Symbol));
                AddBuiltinTypes(items, prefix);
                return Distinct(items);
            }

            // 5) 语句/表达式上下文：片段 + 符号 + 类型 + 命名空间 + 关键字
            var isStatement = IsStatementContext(text, offset);
            if (isStatement) AddSnippets(items, prefix, dialect);

            foreach (var sym in host.GetScopeSymbols(offset))
            {
                if (!Matches(sym.Name, prefix)) continue;
                var desc = sym.ToString();
                var isMethod = sym is FunctionSymbol f && !f.IsPropertyAccessor && !f.IsConstructor;
                items.Add(new CocoaCompletionItem(sym.Name, desc, isMethod ? "()" : null, CompletionKind.Symbol));
            }

            AddBuiltinTypes(items, prefix);

            foreach (var ns in compilation.GlobalNamespace.GetNamespaceMembers())
                if (Matches(ns.Name, prefix))
                    items.Add(new CocoaCompletionItem(ns.Name, "namespace", null, CompletionKind.Namespace));

            AddKeywords(items, isStatement, prefix);

            return Distinct(items);
        }
        catch
        {
            return items;
        }
    }

    private static void AddBuiltinTypes(List<CocoaCompletionItem> items, string prefix)
    {
        foreach (var t in BuiltinTypeNames)
            if (Matches(t, prefix))
                items.Add(new CocoaCompletionItem(t, $"type: {t}", null, CompletionKind.Builtin));
    }

    private static void AddSnippets(List<CocoaCompletionItem> items, string prefix, string dialect)
    {
        var set = string.Equals(dialect, "CSharp", StringComparison.OrdinalIgnoreCase) ? CSharpSnippets : CocoaSnippets;
        foreach (var (text, body) in set)
            if (Matches(text, prefix))
                items.Add(new CocoaCompletionItem(text, "snippet", null, CompletionKind.Snippet, body));
    }

    private static void AddKeywords(List<CocoaCompletionItem> items, bool isStatementContext, string prefix)
    {
        var keywords = isStatementContext ? StatementKeywords : ExpressionKeywords;
        foreach (var kw in keywords)
            if (Matches(kw, prefix))
                items.Add(new CocoaCompletionItem(kw, "keyword", null, CompletionKind.Keyword));
    }

    private static bool Matches(string candidate, string prefix) =>
        prefix.Length == 0 || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static List<CocoaCompletionItem> Distinct(List<CocoaCompletionItem> items) =>
        items.GroupBy(i => i.Text, StringComparer.Ordinal)
             .Select(g => g.OrderByDescending(i => i.Priority).First())
             .OrderByDescending(i => i.Priority)
             .ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
             .ToList();

    // ─── 成员访问 ───

    private static List<CocoaCompletionItem> GetMemberCompletions(
        SemanticModelHost host, SyntaxTree tree, Compilation compilation, string text, int dotOffset, string prefix)
    {
        var result = new List<CocoaCompletionItem>();

        // 容错：在点后插入占位标识符重建语义——`obj.` 这类未完成语法会让整句绑定失败
        var probe = host.WithInsertion(dotOffset + 1, "__cocoa_probe") ?? host;
        var probeTree = probe.Tree ?? tree;
        var model = probe.Model;
        if (model == null) return result;

        SyntaxNode? receiver = null;
        foreach (var token in probeTree.Root.DescendantTokens())
        {
            if (!token.IsMissing && token.Kind == SyntaxKind.DotToken && token.Span.Start == dotOffset)
            {
                receiver = FindReceiverExpression(token.Parent);
                break;
            }
        }
        if (receiver == null) return result;

        var type = model.GetTypeInfo(receiver);
        var symbol = model.GetSymbolInfo(receiver);
        var staticContext = symbol is TypeSymbol;

        if (type == null || type == TypeSymbol.Error)
        {
            // 绑定树不映射成员访问的接收者节点 → 按名/所在作用域解析（类型/命名空间/参数/局部/全局/成员）
            var scopeType = ResolveReceiverByScope(model, probeTree, compilation, receiver, out var scopeIsType, out var ns);

            if (scopeType != null)
            {
                type = scopeType;
                staticContext = scopeIsType;
            }
            else
            {
                if (ns == null)
                {
                    var receiverName = GetReceiverName(text, dotOffset);
                    ns = receiverName.Length > 0 ? compilation.GetNamespace(receiverName) : null;
                }

                if (ns != null)
                {
                    foreach (var child in ns.GetNamespaceMembers())
                        if (Matches(child.Name, prefix))
                            result.Add(new CocoaCompletionItem(child.Name, child.FullName, null, CompletionKind.Namespace));
                    foreach (var t in ns.GetTypeMembers())
                        if (Matches(t.Name, prefix))
                            result.Add(new CocoaCompletionItem(t.Name, t.ToString(), null, CompletionKind.Symbol));
                }
                return result;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (member, insertText, isMethod) in SemanticModelHost.GetMembers(type, staticContext))
        {
            if (!Matches(insertText, prefix)) continue;
            if (!seen.Add(insertText)) continue;
            result.Add(new CocoaCompletionItem(insertText, member.ToString(), isMethod ? "()" : null, CompletionKind.Symbol));
        }

        return result;
    }

    private static TypeSymbol? ResolveNamedReceiver(Compilation compilation, string name)
    {
        if (name.Length == 0) return null;

        if (compilation.GetTypeByMetadataName(name) is TypeSymbol globalType)
            return globalType;

        foreach (var nsName in CollectUsingNamespaces(compilation))
            if (compilation.GetNamespace(nsName)?.TryGetType(name) is TypeSymbol type)
                return type;

        return null;
    }

    /// <summary>接收者名字 → 类型/命名空间：类型 → 命名空间 → 形参 → 局部变量 → 全局 → 实例成员。</summary>
    private static TypeSymbol? ResolveReceiverByScope(
        SemanticModel model, SyntaxTree tree, Compilation compilation, SyntaxNode receiver,
        out bool isType, out NamespaceSymbol? ns)
    {
        isType = false;
        ns = null;

        var name = receiver.DescendantTokens()
            .FirstOrDefault(t => t.Kind == SyntaxKind.IdentifierToken && !t.IsMissing)?.Text;
        if (string.IsNullOrEmpty(name)) return null;

        var staticType = ResolveNamedReceiver(compilation, name!);
        if (staticType != null) { isType = true; return staticType; }

        ns = compilation.GetNamespace(name!);
        if (ns != null) return null;

        var fnDecl = FindEnclosing(receiver, "FunctionDeclaration");

        if (fnDecl != null && model.GetDeclaredSymbol(fnDecl) is FunctionSymbol fn)
            foreach (var p in fn.Parameters)
                if (p.Name == name) return p.Type;

        // 局部变量：扫绑定树（probe 已成功绑定），取 receiver 之前声明的同变量
        foreach (var node in tree.Root.DescendantNodes())
            if (model.GetOperation(node) is BoundVariableDeclaration { Variable: var v } bvd
                && v.Name == name && bvd.Syntax != null && bvd.Syntax.Span.Start < receiver.Span.Start)
                return v.Type;

        foreach (var v in compilation.Variables)
            if (v.Name == name) return v.Type;

        var classDecl = fnDecl != null ? FindEnclosing(fnDecl, "ClassDeclaration") : null;
        if (classDecl != null && model.GetDeclaredSymbol(classDecl) is NamedTypeSymbol cls)
        {
            foreach (var f in cls.Fields) if (f.Name == name) return f.Type;
            foreach (var p in cls.Properties) if (p.Name == name) return p.Type;
        }

        return null;
    }

    private static SyntaxNode? FindEnclosing(SyntaxNode node, string kindName)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
            if (current.Kind.ToString().Contains(kindName, StringComparison.Ordinal))
                return current;
        return null;
    }

    /// <summary>编译单元中全部 using 导入的命名空间名（排除 using static / 别名导入）。</summary>
    private static IEnumerable<string> CollectUsingNamespaces(Compilation compilation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            foreach (var node in tree.Root.DescendantNodes())
            {
                string? name = null;
                if (node is CocoaUsing cu && cu.StaticKeyword == null && cu.AliasToken == null)
                    name = cu.Name;
                else if (node is CSharpUsing cs && cs.StaticKeyword == null && cs.AliasToken == null)
                    name = cs.Name;

                if (!string.IsNullOrEmpty(name) && seen.Add(name!))
                    yield return name!;
            }
        }
    }

    private static SyntaxNode? FindReceiverExpression(SyntaxNode? node)
    {
        if (node == null) return null;
        foreach (var child in node.GetChildren())
        {
            if (child is SyntaxToken) continue;
            return child;
        }
        return null;
    }

    // ─── using 上下文 ───

    private static List<CocoaCompletionItem>? GetUsingCompletions(Compilation compilation, string text, int cursorPosition)
    {
        if (!IsUsingContext(text, cursorPosition)) return null;

        var i = cursorPosition;
        while (i > 0 && (IsIdentifierPart(text[i - 1]) || text[i - 1] == '.')) i--;
        var nsPrefix = text.Substring(i, cursorPosition - i);

        var all = new SortedSet<string>(StringComparer.Ordinal);
        CollectNamespaceNames(compilation.GlobalNamespace, all);

        var items = new List<CocoaCompletionItem>();

        if (nsPrefix.EndsWith(".", StringComparison.Ordinal))
        {
            var parent = nsPrefix.Substring(0, nsPrefix.Length - 1);
            foreach (var ns in all)
            {
                if (!ns.StartsWith(parent + ".", StringComparison.Ordinal)) continue;
                var child = ns.Substring(parent.Length + 1);
                if (child.Contains('.')) continue;
                items.Add(new CocoaCompletionItem(child, ns, null, CompletionKind.Namespace));
            }
        }
        else
        {
            var parent = nsPrefix.Contains('.') ? nsPrefix.Substring(0, nsPrefix.LastIndexOf('.') + 1) : "";
            var lastSeg = nsPrefix.Substring(parent.Length);

            foreach (var ns in all)
            {
                if (!ns.StartsWith(parent, StringComparison.Ordinal)) continue;
                var rest = ns.Substring(parent.Length);
                if (rest.Length == 0 || rest.Contains('.')) continue;
                if (!rest.StartsWith(lastSeg, StringComparison.Ordinal)) continue;
                items.Add(new CocoaCompletionItem(ns, "namespace", null, CompletionKind.Namespace));
            }
        }

        return items;
    }

    private static void CollectNamespaceNames(NamespaceSymbol ns, SortedSet<string> result)
    {
        foreach (var child in ns.GetNamespaceMembers())
        {
            result.Add(child.FullName);
            CollectNamespaceNames(child, result);
        }
    }

    private static bool IsUsingContext(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && (IsIdentifierPart(text[i - 1]) || text[i - 1] == '.')) i--;

        var nsPrefix = text.Substring(i, cursorPosition - i);
        if (nsPrefix.StartsWith(".", StringComparison.Ordinal)) return false;

        var j = i;
        while (j > 0 && (text[j - 1] == ' ' || text[j - 1] == '\t')) j--;

        var k = j;
        while (k > 0 && IsIdentifierPart(text[k - 1])) k--;
        if (k == j) return false;

        return text.Substring(k, j - k) == "using";
    }

    // ─── 位置判定 ───

    private static bool IsTypePosition(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
        if (i == 0) return false;

        if (text[i - 1] == ':') return true;

        var j = i;
        while (j > 0 && IsIdentifierPart(text[j - 1])) j--;
        return j < i && text.Substring(j, i - j) == "new";
    }

    private static bool IsStatementContext(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t' || text[i - 1] == '\r' || text[i - 1] == '\n')) i--;
        if (i == 0) return true;
        var c = text[i - 1];
        return c == ';' || c == '}' || c == '{';
    }

    private static bool IsDeclarationNameContext(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;

        var j = i;
        while (j > 0 && IsIdentifierPart(text[j - 1])) j--;
        if (j == i) return false;

        var prevWord = text.Substring(j, i - j);
        return prevWord is "function" or "class" or "struct" or "interface" or "enum"
            or "facade" or "delegate" or "event" or "property"
            or "let" or "var" or "const";
    }

    private static (int DotOffset, string Prefix) ExtractMemberAccess(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        if (i > 0 && text[i - 1] == '.')
            return (i - 1, text.Substring(i, cursorPosition - i));
        return (-1, "");
    }

    private static string GetReceiverName(string text, int dotOffset)
    {
        var start = dotOffset;
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        return text.Substring(start, dotOffset - start);
    }

    private static string ExtractPrefix(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        return i < cursorPosition ? text.Substring(i, cursorPosition - i) : "";
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
