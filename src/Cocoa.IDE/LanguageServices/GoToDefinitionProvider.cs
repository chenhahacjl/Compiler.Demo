using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.IDE.LanguageServices;

public sealed record GoToTarget(string FilePath, int Line, int Column);

/// <summary>F12 跳转：解析光标处符号 → 定位声明名字 token（函数/类型用 Declaration，
/// 变量用绑定树扫锚），行列取自名字 span，避免落错列。</summary>
public static class GoToDefinitionProvider
{
    public static GoToTarget? FindTarget(SemanticModelHost host, int offset)
    {
        var resolved = host.ResolveSymbolAt(offset);
        if (resolved is not { symbol: { } symbol } r) return null;
        var tree = host.Tree;
        var compilation = host.Compilation;
        if (tree == null || compilation == null) return null;

        var language = tree.Language;

        // 1) 函数 / 类型：直接用 Declaration（名字 token 精确定位）
        switch (symbol)
        {
            case FunctionSymbol fn when fn.Declaration != null:
                return TargetFrom(fn.Declaration, language);
            case NamedTypeSymbol cls when cls.Declaration != null:
                return TargetFrom(cls.Declaration, language);
        }

        // 2) 变量/参数/字段：无 Declaration → 扫绑定树按引用同一符号定位 BoundVariableDeclaration.Syntax
        if (symbol is VariableSymbol variable)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var candidate in tree.Root.DescendantNodes())
            {
                var op = model.GetOperation(candidate);
                if (op is BoundVariableDeclaration { Variable: var v } bvd && ReferenceEquals(v, variable))
                {
                    if (bvd.Syntax != null) return TargetFrom(bvd.Syntax, language);
                }
            }
        }

        // 3) 类成员：父类型声明处（粗定位）
        if (symbol is FunctionSymbol method && method.ContainingClass?.Declaration != null)
            return TargetFrom(method.ContainingClass.Declaration, language);

        return null;
    }

    private static GoToTarget? TargetFrom(SyntaxNode declaration, Language language)
    {
        // 优先取声明名字 token 的位置（精确列），退化到整个声明起点
        var location = language.GetDeclarationNameLocation(declaration) ?? declaration.Location;
        var text = location.Text;
        if (text == null) return null;

        var span = location.Span;
        var lineIndex = text.GetLineIndex(span.Start);
        var line = text.Lines[lineIndex];
        var column = span.Start - line.Start + 1;

        return new GoToTarget(location.FileName, lineIndex + 1, column);
    }
}
