using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using CocoaUsingSyntax = Cocoa.CodeAnalysis.Cocoa.Syntax.UsingDirectiveSyntax;

namespace Cocoa.Compiler.Terminal;

internal sealed class CocoaCompletionProvider : ICompletionProvider
{
    private SyntaxTree? _liveTree;
    private Compilation? _liveCompilation;

    private static readonly Compilation emptyCompilation = Compilation.CreateScript(null);

    private static readonly (string Text, string Snippet)[] Snippets =
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

    /// <summary>复用引擎已解析的 live 语法树/编译，避免每次按键重复 Parse。</summary>
    public void SetLiveState(SyntaxTree? liveTree, Compilation? liveCompilation)
    {
        _liveTree = liveTree;
        _liveCompilation = liveCompilation;
    }

    public IReadOnlyList<CompletionItem> GetCompletions(string text, int cursorPosition)
    {
        // using 语句上下文优先（"using System." 的点不能走成员补全）
        var usingItems = GetUsingCompletions(text, cursorPosition);
        if (usingItems != null)
            return usingItems;

        var prefix = ExtractPrefix(text, cursorPosition);

        // 成员访问上下文（"Console." / "Console.Wr"）：提示 receiver 的成员
        var (memberDotOffset, memberPrefix) = ExtractMemberAccess(text, cursorPosition);
        if (memberDotOffset >= 0)
            return GetMemberCompletions(text, memberDotOffset, memberPrefix);

        // 声明关键字后正在输入新名字（如 "function Test"），不弹补全
        if (IsDeclarationNameContext(text, cursorPosition))
            return Array.Empty<CompletionItem>();

        var compilation = _liveCompilation ?? emptyCompilation;
        var items = new List<CompletionItem>();
        var symbols = GetSuggestableSymbols(compilation);
        var types = GetSuggestableTypes(compilation);
        var addedNames = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(prefix))
        {
            AddSnippets(items, null);
            foreach (var symbol in symbols)
            {
                if (addedNames.Add(symbol.Name))
                    items.Add(new CompletionItem(symbol.Name) { Detail = symbol.ToString() });
            }
            foreach (var type in types)
            {
                if (addedNames.Add(type.Name))
                    items.Add(new CompletionItem(type.Name) { Detail = type.ToString() });
            }
            AddNamespaces(items, compilation, prefix, addedNames);

            AddKeywords(items, IsStatementContext(text, cursorPosition), null);
        }
        else
        {
            AddSnippets(items, prefix);
            foreach (var symbol in symbols)
            {
                if (symbol.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && addedNames.Add(symbol.Name))
                    items.Add(new CompletionItem(symbol.Name) { Detail = symbol.ToString() });
            }
            foreach (var type in types)
            {
                if (type.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && addedNames.Add(type.Name))
                    items.Add(new CompletionItem(type.Name) { Detail = type.ToString() });
            }
            AddNamespaces(items, compilation, prefix, addedNames);

            AddKeywords(items, IsStatementContext(text, cursorPosition), prefix);
        }

        return items;
    }

    /// <summary>顶级命名空间候选（System 等）：支持 System.Console 限定写法。</summary>
    private static void AddNamespaces(
        List<CompletionItem> items, Compilation compilation, string prefix, HashSet<string> addedNames)
    {
        foreach (var ns in compilation.GlobalNamespace.GetNamespaceMembers())
        {
            if (ns.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && addedNames.Add(ns.Name))
                items.Add(new CompletionItem(ns.Name) { Detail = "namespace" });
        }
    }

    /// <summary>可裸名使用的类型：全局命名空间成员 + 提交链上 using 过的命名空间成员（源 + .coa 库）。</summary>
    private IEnumerable<Symbol> GetSuggestableTypes(Compilation compilation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in compilation.GlobalNamespace.GetTypeMembers())
        {
            if (seen.Add(type.Name))
                yield return type;
        }

        foreach (var nsName in CollectUsingNamespaces(compilation))
        {
            var ns = compilation.GetNamespace(nsName);
            if (ns == null) continue;

            foreach (var type in ns.GetTypeMembers())
            {
                if (seen.Add(type.Name))
                    yield return type;
            }
        }
    }

    /// <summary>提交链上全部 using 导入的命名空间名（排除 using static / 别名导入）。</summary>
    private static IEnumerable<string> CollectUsingNamespaces(Compilation compilation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var submission = compilation;
        while (submission != null)
        {
            foreach (var tree in submission.SyntaxTrees)
            {
                foreach (var node in tree.Root.DescendantNodes())
                {
                    if (node is CocoaUsingSyntax usingDirective &&
                        usingDirective.StaticKeyword == null &&
                        usingDirective.AliasToken == null &&
                        usingDirective.Name.Length > 0 &&
                        seen.Add(usingDirective.Name))
                    {
                        yield return usingDirective.Name;
                    }
                }
            }

            submission = submission.Previous;
        }
    }

    /// <summary>可建议的符号：跨提交链的用户函数/变量（与 #ls/#dump 同源，见 ReplSession.EnumerateUserSymbols；
    /// 内置函数不可裸调，不进补全）。</summary>
    private static IEnumerable<Symbol> GetSuggestableSymbols(Compilation compilation) =>
        ReplSession.EnumerateUserSymbols(compilation);

    /// <summary>光标是否处于 using 语句的命名空间输入位置（"using " / "using Sy" / "using System."）。</summary>
    public bool IsUsingContext(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && (IsIdentifierPart(text[i - 1]) || text[i - 1] == '.'))
            i--;

        var nsPrefix = text.Substring(i, cursorPosition - i);
        if (nsPrefix.StartsWith(".", StringComparison.Ordinal))
            return false;

        var j = i;
        while (j > 0 && (text[j - 1] == ' ' || text[j - 1] == '\t'))
            j--;

        var k = j;
        while (k > 0 && IsIdentifierPart(text[k - 1]))
            k--;
        if (k == j)
            return false;

        return text.Substring(k, j - k) == "using";
    }

    /// <summary>成员访问上下文提取：从光标回扫标识符段，前一字符为 '.' 即成立。
    /// 返回（点号偏移, 已输入的成员前缀）；非成员上下文返回 (-1, "")。</summary>
    private static (int DotOffset, string Prefix) ExtractMemberAccess(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1]))
            i--;

        if (i > 0 && text[i - 1] == '.')
            return (i - 1, text.Substring(i, cursorPosition - i));

        return (-1, "");
    }

    /// <summary>光标是否处于成员访问位置（"Console." / "Console.Wr"）。</summary>
    public bool IsMemberAccessContext(string text, int cursorPosition) =>
        ExtractMemberAccess(text, cursorPosition).DotOffset >= 0;

    /// <summary>点号左侧的纯标识符名（非标识符 receiver 返回空串）。</summary>
    private static string GetReceiverName(string text, int dotOffset)
    {
        var start = dotOffset;
        while (start > 0 && IsIdentifierPart(text[start - 1]))
            start--;
        return text.Substring(start, dotOffset - start);
    }

    /// <summary>按名解析类型：全局命名空间 → 提交链上 using 过的命名空间（源 + .coa 库）。</summary>
    private TypeSymbol? ResolveNamedReceiver(string name)
    {
        if (name.Length == 0 || _liveCompilation == null)
            return null;

        if (_liveCompilation.GetTypeByMetadataName(name) is TypeSymbol globalType)
            return globalType;

        foreach (var nsName in CollectUsingNamespaces(_liveCompilation))
        {
            if (_liveCompilation.GetNamespace(nsName)?.TryGetType(name) is TypeSymbol type)
                return type;
        }

        return null;
    }

    /// <summary>命名空间 receiver 的成员候选：子命名空间 + 该空间下的类型。</summary>
    private IReadOnlyList<CompletionItem> BuildNamespaceMemberItems(NamespaceSymbol ns, string prefix)
    {
        var items = new List<CompletionItem>();

        foreach (var child in ns.GetNamespaceMembers())
        {
            if (child.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                items.Add(new CompletionItem(child.Name) { Detail = child.FullName });
        }

        foreach (var type in ns.GetTypeMembers())
        {
            if (type.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                items.Add(new CompletionItem(type.Name) { Detail = type.ToString() });
        }

        return items;
    }

    /// <summary>using 上下文的命名空间补全（源 + .coa 库）：无点前缀提示顶级命名空间，
    /// 点结尾提示子命名空间（插入子段名，Detail 显示全名）。</summary>
    private IReadOnlyList<CompletionItem>? GetUsingCompletions(string text, int cursorPosition)
    {
        if (_liveCompilation == null || !IsUsingContext(text, cursorPosition))
            return null;

        var i = cursorPosition;
        while (i > 0 && (IsIdentifierPart(text[i - 1]) || text[i - 1] == '.'))
            i--;
        var nsPrefix = text.Substring(i, cursorPosition - i);

        var all = new SortedSet<string>(StringComparer.Ordinal);
        CollectNamespaceNames(_liveCompilation.GlobalNamespace, all);

        var items = new List<CompletionItem>();

        if (nsPrefix.EndsWith(".", StringComparison.Ordinal))
        {
            var parent = nsPrefix.Substring(0, nsPrefix.Length - 1);
            foreach (var ns in all)
            {
                if (!ns.StartsWith(parent + ".", StringComparison.Ordinal)) continue;
                var child = ns.Substring(parent.Length + 1);
                if (child.Contains('.')) continue;
                items.Add(new CompletionItem(child) { Detail = ns });
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
                items.Add(new CompletionItem(ns) { Detail = "namespace" });
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

    public string? GetSignatureHint(string text, int cursorPosition)
    {
        if (_liveTree == null || _liveCompilation == null) return null;

        try
        {
            var invocationHint = GetInvocationSignatureHint(text, cursorPosition);
            if (invocationHint != null) return invocationHint;

            return GetMemberAccessSignatureHint(text, cursorPosition);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>光标紧跟 '(' 时显示被调函数的签名；定义场景经按名回退解析。</summary>
    private string? GetInvocationSignatureHint(string text, int cursorPosition)
    {
        var tree = _liveTree!;
        var compilation = _liveCompilation!;

        var offset = cursorPosition;
        while (offset > 0 && (text[offset - 1] == ' ' || text[offset - 1] == '\t'))
            offset--;

        if (offset == 0 || text[offset - 1] != '(')
            return null;

        var openParenOffset = offset - 1;

        var start = openParenOffset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.'))
            start--;

        if (start == openParenOffset)
            return null;

        var name = text.Substring(start, openParenOffset - start);

        foreach (var token in tree.Root.DescendantTokens())
        {
            if (!token.IsMissing && token.Kind == SyntaxKind.OpenParenthesisToken && token.Span.Start == openParenOffset)
            {
                var callNode = FindReceiverExpression(token.Parent);
                if (callNode != null)
                {
                    var model = compilation.GetSemanticModel(tree);
                    if (model.GetSymbolInfo(callNode) is FunctionSymbol function)
                        return function.ToString();
                }

                break;
            }
        }

        return compilation.GetSymbols()
            .OfType<FunctionSymbol>()
            .FirstOrDefault(f => f.Name == name)
            ?.ToString();
    }

    private string? GetMemberAccessSignatureHint(string text, int cursorPosition)
    {
        var (dotOffset, _) = ExtractMemberAccess(text, cursorPosition);
        if (dotOffset < 0) return null;

        SyntaxNode? receiver = null;
        foreach (var token in _liveTree!.Root.DescendantTokens())
        {
            if (!token.IsMissing && token.Kind == SyntaxKind.DotToken && token.Span.Start == dotOffset)
            {
                receiver = FindReceiverExpression(token.Parent);
                break;
            }
        }

        if (receiver == null) return null;

        var model = _liveCompilation!.GetSemanticModel(_liveTree);
        var type = model.GetTypeInfo(receiver);
        if (type == null || type == TypeSymbol.Error) return null;

        var symbol = model.GetSymbolInfo(receiver);
        var staticContext = symbol is TypeSymbol;

        var memberName = ExtractMemberName(text, cursorPosition);
        if (string.IsNullOrEmpty(memberName)) return null;

        var member = FindMember(type, memberName, staticContext);
        if (member == null) return null;

        return member.ToString();
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

    private static CompletionItem? FindMember(TypeSymbol type, string name, bool staticContext)
    {
        if (type is not NamedTypeSymbol named) return null;

        foreach (var field in named.Fields)
        {
            if (IsVisible(field.Visibility) && field.IsStatic == staticContext && field.Name == name)
                return new CompletionItem(field.Name) { Detail = field.ToString() };
        }

        foreach (var method in named.Methods)
        {
            if (IsVisible(method.Visibility) && method.IsStatic == staticContext &&
                !method.IsPropertyAccessor && !method.IsConstructor && !method.IsLambda && method.Name == name)
                return new CompletionItem(method.Name) { Detail = method.ToString(), InsertSuffix = "()" };
        }

        foreach (var property in named.Properties)
        {
            if (IsVisible(property.Visibility) && property.IsStatic == staticContext && !property.IsIndexer && property.Name == name)
                return new CompletionItem(property.Name) { Detail = property.ToString() };
        }

        foreach (var @event in named.Events)
        {
            if (IsVisible(@event.Visibility) && @event.IsStatic == staticContext && @event.Name == name)
                return new CompletionItem(@event.Name) { Detail = @event.ToString() };
        }

        return null;
    }

    private IReadOnlyList<CompletionItem> GetMemberCompletions(string text, int dotOffset, string prefix)
    {
        if (_liveTree == null || _liveCompilation == null)
            return Array.Empty<CompletionItem>();

        try
        {
            SyntaxNode? receiver = null;
            foreach (var token in _liveTree.Root.DescendantTokens())
            {
                if (!token.IsMissing && token.Kind == SyntaxKind.DotToken && token.Span.Start == dotOffset)
                {
                    // 取点号左侧表达式（整个成员访问节点右侧缺失，直接绑会失败）
                    receiver = FindReceiverExpression(token.Parent);
                    break;
                }
            }

            if (receiver == null) return Array.Empty<CompletionItem>();

            var model = _liveCompilation.GetSemanticModel(_liveTree);
            var type = model.GetTypeInfo(receiver);
            var symbol = model.GetSymbolInfo(receiver);
            var staticContext = symbol is TypeSymbol;

            // 语义模型回落不感知 using 命名空间类型（GetTypeByMetadataName 只查全局空间）——补解析
            if (type == null || type == TypeSymbol.Error)
            {
                var receiverName = GetReceiverName(text, dotOffset);
                var resolved = ResolveNamedReceiver(receiverName);
                if (resolved != null)
                {
                    type = resolved;
                    staticContext = true;
                }
                else
                {
                    // 命名空间 receiver（"System."）：提示子命名空间 + 该空间下的类型
                    var ns = receiverName.Length > 0 ? _liveCompilation.GetNamespace(receiverName) : null;
                    return ns != null ? BuildNamespaceMemberItems(ns, prefix) : Array.Empty<CompletionItem>();
                }
            }

            // 同名重载合并为一条，Detail 标注重载数；按已输入前缀过滤
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<CompletionItem>();

            foreach (var item in EnumerateMembers(type, staticContext))
            {
                if (!item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                counts[item.Text] = counts.GetValueOrDefault(item.Text) + 1;
                if (seen.Add(item.Text))
                    result.Add(item);
            }

            for (var i = 0; i < result.Count; i++)
            {
                var overloads = counts[result[i].Text];
                if (overloads > 1)
                {
                    result[i] = new CompletionItem(result[i].Text)
                    {
                        Detail = (result[i].Detail ?? "") + $" (+{overloads - 1})",
                        InsertSuffix = result[i].InsertSuffix,
                    };
                }
            }

            return result;
        }
        catch
        {
            return Array.Empty<CompletionItem>();
        }
    }

    private static IEnumerable<CompletionItem> EnumerateMembers(TypeSymbol type, bool staticContext)
    {
        if (type is not NamedTypeSymbol named) yield break;

        foreach (var field in named.Fields)
        {
            if (IsVisible(field.Visibility) && field.IsStatic == staticContext)
                yield return new CompletionItem(field.Name) { Detail = field.ToString() };
        }

        foreach (var method in named.Methods)
        {
            if (IsVisible(method.Visibility) && method.IsStatic == staticContext &&
                !method.IsPropertyAccessor && !method.IsConstructor && !method.IsLambda)
                yield return new CompletionItem(method.Name) { Detail = method.ToString(), InsertSuffix = "()" };
        }

        foreach (var property in named.Properties)
        {
            if (IsVisible(property.Visibility) && property.IsStatic == staticContext && !property.IsIndexer)
                yield return new CompletionItem(property.Name) { Detail = property.ToString() };
        }

        foreach (var @event in named.Events)
        {
            if (IsVisible(@event.Visibility) && @event.IsStatic == staticContext)
                yield return new CompletionItem(@event.Name) { Detail = @event.ToString() };
        }

        var facade = named.FacadeCompanion;
        if (facade != null && !ReferenceEquals(facade, named))
        {
            foreach (var item in EnumerateMembers(facade, staticContext))
                yield return item;
        }
    }

    private static bool IsVisible(Visibility visibility) =>
        visibility == Visibility.Public || visibility == Visibility.Internal;

    private static bool IsStatementContext(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t' || text[i - 1] == '\r' || text[i - 1] == '\n')) i--;
        if (i == 0) return true;
        var c = text[i - 1];
        return c == ';' || c == '}' || c == '{';
    }

    /// <summary>光标是否处于声明名的输入位置（声明关键字的紧后面），此时补全无意义。</summary>
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

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ExtractPrefix(string text, int cursorPosition)
    {
        var i = cursorPosition;
        while (i > 0 && IsIdentifierPart(text[i - 1])) i--;
        return i < cursorPosition ? text.Substring(i, cursorPosition - i) : "";
    }

    private static void AddSnippets(List<CompletionItem> items, string? prefix)
    {
        foreach (var (text, snippet) in Snippets)
        {
            if (prefix == null || text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                items.Add(new CompletionItem(text) { Detail = "snippet", Snippet = snippet });
        }
    }

    private void AddKeywords(List<CompletionItem> items, bool isStatementContext, string? prefix)
    {
        var statementKeywords = new[] {
            "abstract", "as", "base", "break", "case", "catch", "cdecl", "class",
            "const", "constructor", "continue", "default", "delegate", "do", "else",
            "enum", "event", "extends", "extern", "facade", "false", "finally",
            "for", "foreach", "function", "get", "if", "import", "in", "interface",
            "internal", "is", "let", "namespace", "new", "null", "out", "override",
            "partial", "private", "property", "protected", "public", "readonly",
            "ref", "return", "sealed", "set", "static", "stdcall", "step", "struct",
            "switch", "syscall", "this", "throw", "to", "true", "try", "using",
            "var", "virtual", "when", "where", "while"
        };
        var expressionKeywords = new[] { "true", "false", "null", "new" };
        var keywords = isStatementContext ? statementKeywords : expressionKeywords;
        foreach (var kw in keywords)
        {
            if (prefix == null || kw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                items.Add(new CompletionItem(kw) { Detail = "keyword" });
        }
    }

    private static string? ExtractMemberName(string text, int cursorPosition)
    {
        var start = cursorPosition;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        return start < cursorPosition ? text.Substring(start, cursorPosition - start) : null;
    }
}
