using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 解析器公共接口（S-5 P2-2 语言中性化）：两方独立解析器（CocoaParser / CSharpParser）均实现此接口，
    /// 供 Language.CreateParser 工厂与 SyntaxTree 消费；产出抽象 <see cref="SyntaxNode"/>（语言节点统一视图）。
    /// </summary>
    internal interface IParser
    {
        SyntaxNode ParseCompilationUnit();
        DiagnosticBag Diagnostics { get; }
    }
}
