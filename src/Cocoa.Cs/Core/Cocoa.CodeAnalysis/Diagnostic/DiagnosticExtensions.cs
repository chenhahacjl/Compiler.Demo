using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    public static class DiagnosticExtensions
    {
        public static bool HasErrors(this ImmutableArray<Diagnostic> diagnostics)
        {
            return diagnostics.Any(d => d.IsError);
        }

        public static bool HasErrors(this IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics.Any(d => d.IsError);
        }

        /// <summary>按诊断码过滤被 --nowarn 消除的警告（D3）：保留全部错误 + 未压制警告（Code 为空或不在 nowarn 集）。</summary>
        public static ImmutableArray<Diagnostic> ApplyNowarn(this IEnumerable<Diagnostic> diagnostics, ImmutableHashSet<string> nowarnCodes)
        {
            if (nowarnCodes.IsEmpty)
            {
                return diagnostics.ToImmutableArray();
            }

            return diagnostics.Where(d => d.IsError || (d.Code != null && !nowarnCodes.Contains(d.Code)))
                              .ToImmutableArray();
        }
    }
}
