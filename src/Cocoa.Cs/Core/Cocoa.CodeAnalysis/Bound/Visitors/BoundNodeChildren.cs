using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// 绑定树直接子节点收集（重构阶段 1a/A1 + 4.1-P4/2.3）：集中声明表 <see cref="BoundNodeSchema"/> 驱动。
    /// 子节点清单与 <see cref="BoundTreeRewriter"/> 强一致——新增节点需在 Schema 登记，否则 <c>BoundNodeSchemaTests</c>
    /// 报"枚举值未声明覆盖"（构建期失败），而非静默返回空集。
    /// </summary>
    public static class BoundNodeChildren
    {
        /// <summary>返回 <paramref name="root"/> 的直接子节点（保持遍历序）；叶子节点返回空集。</summary>
        public static IReadOnlyList<BoundNode> Of(BoundNode root)
        {
            return BoundNodeSchema.ChildrenOf(root);
        }
    }
}