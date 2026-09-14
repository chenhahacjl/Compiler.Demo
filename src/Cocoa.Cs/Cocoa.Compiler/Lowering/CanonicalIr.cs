using System.Collections.Generic;
using System.Diagnostics;
using Cocoa.CodeAnalysis.Binding;

namespace Cocoa.CodeAnalysis.Lowering
{
    /// <summary>
    /// 规范 IR 契约校验（Y A2-F1 + HIR 净化 T0.5）：在消费边界确认函数体已无"高 Bound"节点——
    /// 语用：高节点只允许存活于"绑定后 → 规范化前"；cod / 三后端 / 求值只能见到规范形态。
    /// 清单随 F6（高/规范节点分离）扩充；高节点集以 <see cref="IsHighKind"/> 集中声明，便于 Phase 1 追加语言专属高节点。
    /// </summary>
    internal static class CanonicalIr
    {
        /// <summary>是否为高（未规范化）节点：当前仅插值字符串（A2-F1 ingress），后续 F6/Phase 1 追加语言专属高 Bound。</summary>
        private static bool IsHighKind(BoundNodeKind kind)
        {
            return kind switch
            {
                BoundNodeKind.InterpolatedStringExpression => true,
                _ => false,
            };
        }

        [Conditional("DEBUG")]
        public static void Verify(BoundProgram program)
        {
            if (program.Functions == null)
            {
                return;
            }

            foreach (var (_, body) in program.Functions)
            {
                VerifyBlock(body);
            }
        }

        private static void VerifyBlock(BoundBlockStatement block)
        {
            foreach (var statement in block.Statements)
            {
                VerifyNode(statement);
            }
        }

        private static void VerifyNode(BoundNode node)
        {
            if (IsHighKind(node.Kind))
            {
                Debug.Assert(false, $"规范 IR 契约违例：高 Bound {node.Kind} 泄漏到消费边界（A2-F1，位置 {node.Syntax?.Location}）。");
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                if (child is BoundBlockStatement nested)
                {
                    VerifyBlock(nested);
                }
                else
                {
                    VerifyNode(child);
                }
            }
        }
    }
}