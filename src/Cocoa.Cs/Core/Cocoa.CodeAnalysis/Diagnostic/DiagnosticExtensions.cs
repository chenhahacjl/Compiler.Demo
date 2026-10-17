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

        /// <summary>按诊断码过滤被 --nowarn 消除的警告（D3，C# 语义）：保留全部错误 + 全部无码警告 + 未压制警告。
        /// 注：无码警告恒保留——C# `-nowarn:X` 只抑制指定码，不因给了 nowarn 就吞掉未登记码的警告。</summary>
        public static ImmutableArray<Diagnostic> ApplyNowarn(this IEnumerable<Diagnostic> diagnostics, ImmutableHashSet<string> nowarnCodes)
        {
            if (nowarnCodes.IsEmpty)
            {
                return diagnostics.ToImmutableArray();
            }

            return diagnostics.Where(d => d.IsError || d.Code == null || !nowarnCodes.Contains(d.Code))
                              .ToImmutableArray();
        }
    }
}
