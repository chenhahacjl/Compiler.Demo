using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 求值结果
    /// </summary>
    public sealed class EvaluationResult
    {
        public EvaluationResult(ImmutableArray<Diagnostic> diagnostics, object value)
        {
            Diagnostics = diagnostics;
            Value = value;
        }

        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public object Value { get; }
    }
}
