using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// try/catch 中的 catch 子句（非独立语句，仅作为 BoundTryStatement 的数据载体）。
    /// </summary>
    internal sealed class BoundCatchClause
    {
        public BoundCatchClause(VariableSymbol variable, TypeSymbol catchType, BoundStatement body)
        {
            Variable = variable;
            CatchType = catchType;
            Body = body;
        }

        public VariableSymbol Variable { get; }
        public TypeSymbol CatchType { get; }
        public BoundStatement Body { get; }
    }
}
