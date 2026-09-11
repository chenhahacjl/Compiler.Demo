using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.IDE.LanguageServices;

public sealed record GoToTarget(string FilePath, int Line, int Column);

/// <summary>F12 跳转：解析光标处符号 → 定位声明位置（函数/类型用 Declaration，变量用绑定树扫锚）。</summary>
public static class GoToDefinitionProvider
{
    public static GoToTarget? FindTarget(SemanticModelHost host, int offset)
    {
        var resolved = host.ResolveSymbolAt(offset);
        if (resolved is not { symbol: { } symbol } r) return null;
        var node = r.node;
        var tree = host.Tree;
        var compilation = host.Compilation;
        if (tree == null || compilation == null) return null;

        // 1) 函数 / 类型：直接用 Declaration。Declaration.Location 的 FileName/Span
        switch (symbol)
        {
            case FunctionSymbol fn when fn.Declaration != null:
                return TargetFrom(fn.Declaration);
            case NamedTypeSymbol cls when cls.Declaration != null:
                return TargetFrom(cls.Declaration);
        }

        // 2) 变量/参数/字段：无 Declaration → 扫绑定树按引用同一符号定位 BoundVariableDeclaration.Syntax
        if (symbol is VariableSymbol var)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var candidate in tree.Root.DescendantNodes())
            {
                var op = model.GetOperation(candidate);
                if (op is BoundVariableDeclaration { Variable: var v } bvd && ReferenceEquals(v, var))
                {
                    if (bvd.Syntax != null) return TargetFrom(bvd.Syntax);
                }
            }
        }

        // 3) 类成员：父类型声明处（粗定位）
        if (symbol is FunctionSymbol method && method.ContainingClass?.Declaration != null)
            return TargetFrom(method.ContainingClass.Declaration);

        return null;
    }

    private static GoToTarget? TargetFrom(SyntaxNode declaration)
    {
        var loc = declaration.Location;
        if (loc.Text == null) return null;

        // 定位到声明名字 token（更精确）：Language.GetDeclarationNameLocation 可选，先退化到整体
        var startLine = loc.StartLine;
        var startChar = loc.StartCharacter;

        var lineIndex = loc.Text.GetLineIndex(loc.Span.Start);
        var line = loc.Text.Lines[lineIndex];
        // 跳到标识符起始：声明首行内找第一个字母
        var lineText = line.Text;
        var col = 1;
        for (var i = 0; i < lineText.Length; i++)
        {
            if (char.IsLetter(lineText[i]))
            {
                col = i + 1;
                break;
            }
        }

        _ = startLine;
        _ = startChar;

        return new GoToTarget(loc.FileName, lineIndex + 1, col);
    }
}