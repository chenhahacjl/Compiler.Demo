using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Contracts
{
    /// <summary>
    /// 模式匹配（is/as/声明模式）跨后端语义契约（3.1）——静态折叠与动态判定的唯一事实来源。
    /// 三后端（Evaluator / IL / Native）在 is/as 路径必须遵守：
    /// <list type="number">
    /// <item>**判定**：<c>expr is T</c> 在运行时为类型测试——值类型目标对装箱实例判定，引用类型目标对引用判定。</item>
    /// <item>**值类型装箱形态**：值类型目标须经装箱表示测试（IL isinst 对值类型合法、返回装箱实例；unbox.any 拆回本体）。</item>
    /// <item>**as 失败**：<c>expr as T</c> 未命中返回 null（引用/any 域）。</item>
    /// <item>**声明模式**：<c>expr is T var</c> 命中后变量绑定目标值（值类型为拆箱本体）。</item>
    /// <item>**静态折叠**：两侧可折叠时编译器折叠为常量判定。</item>
    /// </list>
    /// 平台差异（Native）：值类型/string 目标需装箱运行时类型信息，native 后端暂不支持时经校验器拒绝。
    /// </summary>
    public static class PatternMatchContract
    {
        /// <summary>is/as 目标是否可静态折叠（可折叠 → 编译期常量判定，无需运行时发射）。</summary>
        public static bool CanFold(BoundExpression expression)
        {
            return expression.ConstantValue != null;
        }

        /// <summary>值类型目标的动态判定须经装箱形态（IL isinst 装箱返回；Evaluator 判装箱值类型）。</summary>
        public static bool RequiresBoxedForm(TypeSymbol targetType)
        {
            return targetType.IsValueType;
        }

        /// <summary>string 目标在 native 后端需要装箱运行时类型信息（不支持声明模式时校验器拒绝）。</summary>
        public static bool NeedsRuntimeTypeInfo(TypeSymbol targetType)
        {
            return targetType.IsValueType || targetType == TypeSymbol.String;
        }
    }
}
