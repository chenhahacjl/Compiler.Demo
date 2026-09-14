using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 数值装箱/取值工具（6e-M21 Phase 3）：常量折叠与求值器共用，保证编译期与运行期表示一致。
    /// 有符号整数运行时装箱 sbyte/short/int/long；无符号装箱 byte/ushort/uint/ulong；浮点 float/double。
    /// </summary>
    public static class NumericBox
    {
        /// <summary>按类型位宽与符号性归位装箱（接受有符号域计算结果）。</summary>
        public static object Box(TypeSymbol type, long value)
        {
            return type.IsSigned ? BoxSigned(type, value) : BoxUnsigned(type, unchecked((ulong)value));
        }

        /// <summary>按类型位宽与符号性归位装箱（接受无符号域计算结果）。</summary>
        public static object Box(TypeSymbol type, ulong value)
        {
            return type.IsSigned ? BoxSigned(type, unchecked((long)value)) : BoxUnsigned(type, value);
        }

        public static object BoxSigned(TypeSymbol type, long value)
        {
            // 注意：各 arm 必须显式转 object——否则 switch 表达式的自然类型会被推断为公共类型 long，
            // 导致 (int) 归位值被静默提升回 long（6e-M21 Phase2 踩坑记录）
            return type.BitWidth switch
            {
                8 => (object)(sbyte)value,
                16 => (object)(short)value,
                32 => (object)(int)value,
                _ => value,
            };
        }

        public static object BoxUnsigned(TypeSymbol type, ulong value)
        {
            // 同 BoxSigned：arm 显式转 object，避免公共类型推断为 ulong
            return type.BitWidth switch
            {
                8 => (object)(byte)value,
                16 => (object)(ushort)value,
                32 => (object)(uint)value,
                _ => value,
            };
        }

        public static long ToSigned64(object value) => value switch
        {
            int i => i,
            long l => l,
            char c => c,
            sbyte sb => sb,
            short s => s,
            uint u => unchecked((long)u),
            byte b => b,
            ushort us => us,
            ulong ul => unchecked((long)ul),
            _ => throw new System.InvalidOperationException($"Not an integer constant: {value}"),
        };

        public static ulong ToUnsigned64(object value) => value switch
        {
            uint u => u,
            ulong ul => ul,
            byte b => b,
            ushort us => us,
            int i => unchecked((ulong)i),
            long l => unchecked((ulong)l),
            char c => c,
            sbyte sb => unchecked((ulong)sb),
            short s => unchecked((ulong)s),
            _ => throw new System.InvalidOperationException($"Not an integer constant: {value}"),
        };
    }
}
