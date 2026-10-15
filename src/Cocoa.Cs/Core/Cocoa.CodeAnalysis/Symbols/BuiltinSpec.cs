namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>内置函数规格：名称/签名 + 种类（功能层声明）。</summary>
    internal sealed record BuiltinSpec(BuiltinKind Kind, string Name, TypeSymbol ReturnType, (string Name, TypeSymbol Type)[] Parameters);
}