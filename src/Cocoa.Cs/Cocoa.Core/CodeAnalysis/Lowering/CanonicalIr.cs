using System.Collections.Generic;
using System.Diagnostics;
using Cocoa.CodeAnalysis.Binding;

namespace Cocoa.CodeAnalysis.Lowering
{
    /// <summary>
    /// 规范 IR 契约校验（Y A2-F1）：在消费边界确认函数体已无"高 Bound"节点——
    /// 语用：高节点只允许存活于"绑定后 → 规范化前"；cod / 三后端 / 求值只能见到规范形态。
    /// 清单随 F6（高/规范节点分离）扩充。
    /// </summary>
    internal static class CanonicalIr
    {
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
            Debug.Assert(node.Kind != BoundNodeKind.InterpolatedStringExpression,
                "规范 IR 契约违例：插值高 Bound 泄漏到消费边界（A2-F1）。");
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