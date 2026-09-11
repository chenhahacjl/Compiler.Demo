using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.IDE.LanguageServices;

/// <summary>语义服务入口：为单个标签持有最新 Compilation + SemanticModel，
/// 提供光标位置 → 符号 的解析、Hover、F12、补全。</summary>
public sealed class SemanticModelHost
{
    private Compilation? _compilation;
    private SyntaxTree? _tree;

    /// <summary>用当前文本重建多文件编译（含工程内其它源文件，跨文件 F12/Hover 解析）。</summary>
    public void Update(string text, string fileName, Language language, IEnumerable<string>? otherSourceFiles = null)
    {
        _tree = SyntaxTree.Parse(Cocoa.CodeAnalysis.Text.SourceText.From(text, fileName), language);

        var trees = new List<SyntaxTree> { _tree };
        if (otherSourceFiles != null)
        {
            foreach (var path in otherSourceFiles)
            {
                if (string.Equals(path, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (System.IO.File.Exists(path))
                        trees.Add(SyntaxTree.Load(path));
                }
                catch { /* 忽略损坏文件 */ }
            }
        }

        _compilation = Compilation.Create(trees.ToArray());
    }

    public SyntaxTree? Tree => _tree;
    public Compilation? Compilation => _compilation;

    /// <summary>定位 offset 处最深的 SyntaxToken（跳过缺失令牌）。</summary>
    public static SyntaxToken? FindToken(SyntaxTree tree, int offset)
    {
        foreach (var token in tree.Root.DescendantTokens())
        {
            if (token.IsMissing) continue;
            if (token.Span.Start <= offset && offset <= token.Span.End)
                return token;
        }
        return null;
    }

    /// <summary>解析 offset 处的符号。优先 GetSymbolInfo（精确），声明仅名字位置采用，类型兜底。</summary>
    public (Symbol? symbol, SyntaxNode? node)? ResolveSymbolAt(int offset)
    {
        if (_tree == null || _compilation == null) return null;

        var token = FindToken(_tree, offset);
        if (token == null) return null;

        var model = _compilation.GetSemanticModel(_tree);
        var language = _tree.Language;

        var cursor = token.Parent;
        while (cursor != null && cursor.Kind != SyntaxKind.CompilationUnit)
        {
            // 1) 表达式/调用 → 符号（最精确）
            var info = model.GetSymbolInfo(cursor);
            if (info != null) return (info, cursor);

            // 2) 类型信息（仅当确实是名字/类型节点时）
            var type = model.GetTypeInfo(cursor);
            if (type != null && type != TypeSymbol.Error && cursor.Span.Start == token.Span.Start && cursor.Span.End == token.Span.End)
                return (type, cursor);

            // 3) 声明：仅当 token 位于声明名字处才返回（避免外层声明误命中）
            var declared = model.GetDeclaredSymbol(cursor);
            if (declared != null)
            {
                var nameLoc = language.GetDeclarationNameLocation(cursor);
                if (nameLoc != null && nameLoc.Value.Span.Start <= token.Span.Start && token.Span.Start <= nameLoc.Value.Span.End)
                    return (declared, cursor);
            }

            cursor = cursor.Parent;
        }

        // 兜底：无类型名称（如未知标识符）
        return (null, token.Parent);
    }

    /// <summary>补全候选：作用域内符号（全局函数/变量/类/枚举/内建函数/内建类型）。</summary>
    public IEnumerable<Symbol> GetScopeSymbols()
    {
        if (_compilation == null) yield break;

        foreach (var f in _compilation.Functions)
            if (f.ContainingClass == null && !f.IsConstructor && !f.IsLambda)
                yield return f;

        foreach (var v in _compilation.Variables)
            yield return v;

        foreach (var cls in _compilation.GlobalScope.Classes)
            yield return cls;

        foreach (var en in _compilation.GlobalScope.Enums)
            yield return en;

        foreach (var b in BuiltinFunctions.GetAll())
            yield return b;

        foreach (var t in _compilation.GlobalNamespace.GetTypeMembers())
            yield return t;
    }

    /// <summary>成员补全：类型的所有公开成员（含继承与 facade）。</summary>
    public static IEnumerable<(Symbol symbol, string insertText, bool isMethod)> GetMembers(TypeSymbol type, bool staticContext)
    {
        if (type is not NamedTypeSymbol named) yield break;

        foreach (var m in named.Methods)
        {
            if (IsUsableMember(m, staticContext) && !m.IsAccessor() && !m.IsConstructor && !m.IsLambda && !m.IsIndexer())
                yield return (m, m.Name, true);
        }

        foreach (var p in named.Properties)
        {
            if (p.IsStatic == staticContext && p.Visibility is Visibility.Public or Visibility.Internal)
                yield return (p, p.Name, false);
        }

        foreach (var f in named.Fields)
        {
            if (f.IsStatic == staticContext && f.Visibility is Visibility.Public or Visibility.Internal)
                yield return (f, f.Name, false);
        }

        foreach (var e in named.Events)
        {
            if (e.IsStatic == staticContext && e.Visibility is Visibility.Public or Visibility.Internal)
                yield return (e, e.Name, false);
        }

        if (named.FacadeCompanion != null && named.FacadeCompanion != named)
        {
            foreach (var item in GetMembers(named.FacadeCompanion, staticContext))
                yield return item;
        }
    }

    private static bool IsUsableMember(FunctionSymbol m, bool staticContext)
    {
        if (m.ContainingProperty != null) return false;
        if (m.IsAccessor() || m.IsConstructor || m.IsLambda || m.IsIndexer()) return false;
        if (m.IsStatic != staticContext) return false;
        return m.Visibility is Visibility.Public or Visibility.Internal;
    }

    private static bool IsTypeContext(SyntaxNode node)
    {
        // 粗略：节点本身就是类型声明或类型引用时不为补全返回类型符号
        return node is SyntaxToken t && t.IsMissing == false && node.Parent != null &&
               (node.Parent.Kind.ToString().Contains("Type")
                || node.Parent.Kind.ToString().Contains("Declaration"));
    }
}

internal static class MemberExtensions
{
    public static bool IsAccessor(this FunctionSymbol m) => m.IsPropertyAccessor || m.ContainingProperty != null;
    public static bool IsIndexer(this FunctionSymbol m) => m.ContainingProperty is { IsIndexer: true };
}