using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 解析器公共接口：两方独立解析器（CocoaParser / ParserCore）均实现此接口，
    /// 供 Language.CreateParser 工厂与 SyntaxTree 消费。
    /// </summary>
    internal interface IParser
    {
        CompilationUnitSyntax ParseCompilationUnit();
        DiagnosticBag Diagnostics { get; }
    }
}
