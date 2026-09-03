using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Lowering
{
    /// <summary>
    /// 共享规范化 pass（Y A2-F1）：把插值字符串高 Bound（<see cref="BoundInterpolatedStringExpression"/>）
    /// 降为 <see cref="BoundFormatExpression"/> / string 拼接（+），语义与绑定内联旧行为逐一对应，
    /// 仅迁移"组装时机"（洞的绑定/常量校验/字符串转换仍在 Binder 完成）。
    /// </summary>
    internal static class InterpolationNormalizer
    {
        public static BoundStatement Rewrite(BoundStatement body)
        {
            return new Rewriter().RewriteStatement(body);
        }

        private sealed class Rewriter : BoundTreeRewriter
        {
            protected override BoundExpression RewriteInterpolatedStringExpression(BoundInterpolatedStringExpression node)
            {
                BoundExpression? result = null;
                foreach (var item in node.Items)
                {
                    var value = RewriteExpression(item.Value);
                    BoundExpression right = item.IsHole && (item.Width != null || item.Format != null)
                        // 带对齐/格式的洞 → BoundFormatExpression（保留原类型供各后端按类型格式化）
                        ? new BoundFormatExpression(item.Syntax, value, item.Width, item.Format)
                        // 文本段 或 已转 string 的洞 → 原样
                        : value;

                    result = result == null
                        ? right
                        : new BoundBinaryExpression(item.Syntax, result,
                            BoundBinaryOperator.Bind(SyntaxKind.PlusToken, TypeSymbol.String, TypeSymbol.String)!, right);
                }

                return result ?? new BoundLiteralExpression(node.Syntax, "");
            }
        }
    }
}