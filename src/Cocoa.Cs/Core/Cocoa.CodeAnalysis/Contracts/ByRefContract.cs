using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Contracts
{
    /// <summary>
    /// byref（out/ref 实参）跨后端语义契约（3.1）——copy-in/copy-out 装箱语义的唯一事实来源。
    /// 三后端（Evaluator / IL / Native）在 byref 实参路径必须遵守下述规则：
    /// <list type="number">
    /// <item>**copy-in**：调用前把目标存储的当前值复制入箱（out var 内联声明目标按默认值初始化）。</item>
    /// <item>**copy-out**：调用退出时把箱内最终值统一回写原存储（异常路径同样执行）。</item>
    /// <item>**别名去重**：同一次调用的相同存储共享同一箱（跨实参引用相等/元素索引共享）。</item>
    /// <item>**回写时序**：回写须在弹出被调者帧之后执行，否则写入落进被丢弃帧。</item>
    /// <item>**目标种类**：变量（局部/形参/全局）、静态字段、实例字段、数组元素四类 lvalue；string 元素禁作目标。</item>
    /// </list>
    /// 实现形态：Evaluator 用 <c>ByRefBox</c> + 回写动作表；IL 用托管指针（ldarga/ldloca/ldsflda/ldflda/ldelema）+ ldind/stind；
    /// Native 用帧槽地址/数据段符号地址/对象偏移 + Load/Store 穿透。形态各异，规则一致。
    /// </summary>
    public static class ByRefContract
    {
        /// <summary>byref 实参目标种类（三后端分类一致）。</summary>
        public enum LValueTarget
        {
            /// <summary>局部/形参/全局变量。</summary>
            Variable,

            /// <summary>静态字段（类名.s 或 全局 变量经 member access 表达）。</summary>
            StaticField,

            /// <summary>实例字段（接收者 + ldflda 偏移）。</summary>
            InstanceField,

            /// <summary>数组元素（数组 + 索引，CLR 自带越界检查 / native 显式检查）。</summary>
            Element,

            /// <summary>其他（绑定层已排除的形态）——后端应抛异常。</summary>
            Unsupported,
        }

        /// <summary>byref 目标分类（三后端 byref 发射共用，替代各自重复 switch）。</summary>
        public static LValueTarget ClassifyTarget(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundVariableExpression:
                    return LValueTarget.Variable;

                case BoundMemberAccessExpression member when member.Field is { IsStatic: true }:
                    return LValueTarget.StaticField;

                case BoundMemberAccessExpression member when member.Field != null:
                    return LValueTarget.InstanceField;

                case BoundElementAccessExpression element
                    when element.Target.Type != TypeSymbol.String && element.Target.Type.ElementType != null:
                    return LValueTarget.Element;

                default:
                    return LValueTarget.Unsupported;
            }
        }

        /// <summary>目标是否为合法 byref lvalue（string 元素除外）。</summary>
        public static bool IsValidLValue(BoundExpression expression)
        {
            return ClassifyTarget(expression) != LValueTarget.Unsupported;
        }

        /// <summary>out var 内联声明目标的 copy-in 默认值：值类型 → 0（按类型），引用/any → null。</summary>
        public static object? CopyInDefaultValue(TypeSymbol type)
        {
            if (type.IsValueType)
            {
                if (type == TypeSymbol.Double)
                {
                    return 0.0;
                }

                if (type == TypeSymbol.Float)
                {
                    return 0.0f;
                }

                if (type == TypeSymbol.Int64)
                {
                    return 0L;
                }

                if (type == TypeSymbol.Char)
                {
                    return '\0';
                }

                return 0;
            }

            return null;
        }

        /// <summary>byref 实参的 copy-in/copy-out 语义标记（文档/测试断言用）：out 与 ref 均 copy-out，仅 copy-in 差默认值。</summary>
        public static (bool CopyIn, bool CopyOut) Flow(bool isOut, bool isRef)
        {
            // out：copy-in 按默认值（入口未赋值契约）、copy-out 必回写；ref：copy-in 当前值、copy-out 必回写。
            return (CopyIn: true, CopyOut: true);
        }
    }
}
