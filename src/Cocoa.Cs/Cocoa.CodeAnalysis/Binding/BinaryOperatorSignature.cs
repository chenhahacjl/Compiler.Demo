using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Bound;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>二元运算符签名（规则表条目）：语义 kind + 操作数/返回类型 + 发射键（阶段 2 符号化预留）。</summary>
    public readonly record struct BinaryOperatorSignature(
        BoundBinaryOperatorKind Kind,
        TypeSymbol LeftType,
        TypeSymbol RightType,
        TypeSymbol ResultType,
        Cocoa.CodeAnalysis.Symbols.BuiltinKind? EmitKind = null);
}