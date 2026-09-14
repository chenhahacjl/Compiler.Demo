using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.IDE.LanguageServices;

public sealed record GoToTarget(string FilePath, int Line, int Column);

/// <summary>F12 跳转：解析光标处符号 → 定位声明名字 token。
/// 先精确匹配声明节点内与符号同名的标识符 token 的 Location（行列取自名字 span），
/// 再退化到语言钩子/声明起点，避免落错列。</summary>
public static class GoToDefinitionProvider
{
    public static GoToTarget? FindTarget(SemanticModelHost host, int offset)
    {
        var resolved = host.ResolveSymbolAt(offset);
        if (resolved is not { symbol: { } symbol } r) return null;
        var tree = host.Tree;
        var compilation = host.Compilation;
        if (tree == null || compilation == null) return null;

        var declaration = FindDeclaration(symbol, host.Model, tree);
        if (declaration == null) return null;

        return TargetFrom(declaration, symbol.Name);
    }

    /// <summary>找到符号的声明语法节点（函数/类型自带 Declaration；参数扫函数表；局部变量扫绑定树；类成员回落父类型）。</summary>
    private static SyntaxNode? FindDeclaration(Symbol symbol, SemanticModel? model, SyntaxTree tree)
    {
        // 1) 函数 / 类型：Declaration 即名字所在节点
        if (symbol is FunctionSymbol fn && fn.Declaration != null) return fn.Declaration;
        if (symbol is NamedTypeSymbol type && type.Declaration != null) return type.Declaration;

        // 2) 参数：扫全部函数，按引用相等定位所属函数声明
        if (symbol is ParameterSymbol parameter)
        {
            foreach (var function in model?.Compilation.Functions ?? default)
            {
                foreach (var p in function.Parameters)
                {
                    if (ReferenceEquals(p, parameter) && function.Declaration != null)
                        return function.Declaration;
                }
            }
        }

        // 3) 局部变量：扫绑定树，取引用同一符号的 BoundVariableDeclaration 语法（须用同一语义模型实例）
        if (symbol is VariableSymbol && model != null)
        {
            foreach (var candidate in tree.Root.DescendantNodes())
            {
                if (model.GetOperation(candidate) is BoundVariableDeclaration { Variable: var v } bvd
                    && ReferenceEquals(v, symbol) && bvd.Syntax != null)
                    return bvd.Syntax;
            }
        }

        // 4) 类成员（字段/属性/事件）无绑定声明时回落父类型声明，名字由 Token 搜索精确定位
        return symbol switch
        {
            FieldSymbol field => field.ContainingClass?.Declaration,
            PropertySymbol property => property.ContainingClass?.Declaration,
            EventSymbol evt => evt.ContainingClass?.Declaration,
            _ => null,
        };
    }

    private static GoToTarget? TargetFrom(SyntaxNode declaration, string name)
    {
        // 优先：声明节点内与符号同名的标识符 token（精确列）
        var location = FindIdentifier(declaration, name)
            ?? declaration.GetDeclarationNameLocation()
            ?? declaration.Location;

        var text = location.Text;
        if (text == null) return null;

        var span = location.Span;
        var lineIndex = text.GetLineIndex(span.Start);
        var line = text.Lines[lineIndex];
        var column = span.Start - line.Start + 1;

        return new GoToTarget(location.FileName, lineIndex + 1, column);
    }

    /// <summary>在声明节点内按名精确匹配标识符 token（前序，跳过缺失令牌）。</summary>
    private static TextLocation? FindIdentifier(SyntaxNode declaration, string name)
    {
        foreach (var token in declaration.DescendantTokens())
        {
            if (token.Kind == SyntaxKind.IdentifierToken && !token.IsMissing && token.Text == name)
                return token.Location;
        }
        return null;
    }
}
