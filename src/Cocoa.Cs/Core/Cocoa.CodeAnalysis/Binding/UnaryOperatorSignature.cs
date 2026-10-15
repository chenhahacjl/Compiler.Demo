using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Bound;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>一元运算符签名（规则表条目）。</summary>
    public readonly record struct UnaryOperatorSignature(
        BoundUnaryOperatorKind Kind,
        TypeSymbol OperandType,
        TypeSymbol ResultType,
        Cocoa.CodeAnalysis.Symbols.BuiltinKind? EmitKind = null);
}