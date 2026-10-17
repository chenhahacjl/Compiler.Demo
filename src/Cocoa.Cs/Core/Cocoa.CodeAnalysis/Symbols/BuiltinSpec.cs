namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>内置函数规格：名称/签名 + 种类 + 分类（功能层声明）。单一描述符表见 <see cref="BuiltinFunctions"/>。</summary>
    internal sealed record BuiltinSpec(BuiltinKind Kind, string Name, TypeSymbol ReturnType, (string Name, TypeSymbol Type)[] Parameters, BuiltinCategory Category = BuiltinCategory.Function);
}
