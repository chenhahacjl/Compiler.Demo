using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class Conversion
    {
        public static readonly Conversion None = new Conversion(exists: false, isIdentity: false, isImplicit: false);
        public static readonly Conversion Identity = new Conversion(exists: true, isIdentity: true, isImplicit: true);
        public static readonly Conversion Implicit = new Conversion(exists: true, isIdentity: false, isImplicit: true);
        public static readonly Conversion Explicit = new Conversion(exists: true, isIdentity: false, isImplicit: false);

        private Conversion(bool exists, bool isIdentity, bool isImplicit)
        {
            Exists = exists;
            IsIdentity = isIdentity;
            IsImplicit = isImplicit;
        }

        public bool Exists { get; }
        public bool IsIdentity { get; }
        public bool IsImplicit { get; }
        public bool IsExplicit => Exists && !IsImplicit;

        public static Conversion Classify(TypeSymbol from, TypeSymbol to)
        {
            if (from == to)
            {
                return Conversion.Identity;
            }

            // 6e-M19 M5-a：null 字面量 → 可空引用型（any/类/接口/string/数组）隐式；
            // 其余目标（数值/bool/char/void）不存在转换。必须置于 any 双向规则之前，
            // 否则 Null→any 被通用"非 void→any"吞掉、而 any→Null 会经 ③ 泄漏成合法显式转换。
            if (from == TypeSymbol.Null)
            {
                if (to == TypeSymbol.Any || to == TypeSymbol.String || to is NamedTypeSymbol || to.ElementType != null)
                {
                    return Conversion.Implicit;
                }

                return Conversion.None;
            }

            if (from != TypeSymbol.Void && to == TypeSymbol.Any)
            {
                return Conversion.Implicit;
            }

            if (from == TypeSymbol.Any && to != TypeSymbol.Void && to != TypeSymbol.Null)
            {
                return Conversion.Explicit;
            }

            if (from == TypeSymbol.Boolean || from == TypeSymbol.Int32 || from == TypeSymbol.UInt8 || from == TypeSymbol.Int64)
            {
                if (to == TypeSymbol.String)
                {
                    return Conversion.Explicit;
                }
            }

            if (from == TypeSymbol.Char)
            {
                if (to == TypeSymbol.Int32)
                {
                    return Conversion.Implicit;
                }

                if (to == TypeSymbol.String)
                {
                    return Conversion.Implicit;
                }
            }

            if (from == TypeSymbol.Int32)
            {
                if (to == TypeSymbol.Char)
                {
                    return Conversion.Explicit;
                }

                if (to == TypeSymbol.UInt8)
                {
                    return Conversion.Explicit;
                }

                if (to == TypeSymbol.Double)
                {
                    return Conversion.Implicit;
                }

                if (to == TypeSymbol.Int64)
                {
                    // 符号扩展（C# int→long 隐式）
                    return Conversion.Implicit;
                }
            }

            if (from == TypeSymbol.UInt8)
            {
                if (to == TypeSymbol.Int32 || to == TypeSymbol.Double || to == TypeSymbol.Int64)
                {
                    return Conversion.Implicit;
                }
            }

            if (from == TypeSymbol.Char && to == TypeSymbol.Int64)
            {
                // char → long 零扩展（与 char → int 一致）
                return Conversion.Implicit;
            }

            if (from == TypeSymbol.Int64)
            {
                if (to == TypeSymbol.Int32 || to == TypeSymbol.UInt8 || to == TypeSymbol.Char)
                {
                    return Conversion.Explicit;
                }

                if (to == TypeSymbol.Double)
                {
                    return Conversion.Implicit;
                }
            }

            if (from == TypeSymbol.Double)
            {
                if (to == TypeSymbol.Int32 || to == TypeSymbol.UInt8 || to == TypeSymbol.String || to == TypeSymbol.Int64)
                {
                    return Conversion.Explicit;
                }
            }

            // 6e-M21 Phase 1：数值类型系统化转换（按位宽/有无符号/是否浮点判定）
            // 隐式（拓宽）：同符号位宽不降；unsigned(n)→signed(>n)；任意数值→浮点（含 f32→f64）。
            // 显式（窄化）：其余数值↔数值组合（含 signed→unsigned、浮点→整数、f64→f32）。
            if (from.IsNumeric && to.IsNumeric && !from.IsPlaceholder128 && !to.IsPlaceholder128)
            {
                if (to.IsFloat)
                {
                    // → 浮点：位宽不降则隐式（int→f32/f64、long→f64、f32→f64），降宽（f64→f32）显式
                    return from.BitWidth <= to.BitWidth ? Conversion.Implicit : Conversion.Explicit;
                }

                if (from.IsFloat)
                {
                    // 浮点→整数：一律显式
                    return Conversion.Explicit;
                }

                if (from.IsSigned == to.IsSigned)
                {
                    return from.BitWidth <= to.BitWidth ? Conversion.Implicit : Conversion.Explicit;
                }

                if (!from.IsSigned && to.IsSigned)
                {
                    // unsigned(n) → signed(>n)：无损失，隐式
                    return to.BitWidth > from.BitWidth ? Conversion.Implicit : Conversion.Explicit;
                }

                // signed → unsigned：可能丢负号，一律显式
                return Conversion.Explicit;
            }

            // 6e-M21 Phase 7：全部数值类型（含窄整型/无符号/f32）→ string 显式转换（ToString 同构）
            if (to == TypeSymbol.String &&
                ((from.IsInteger && !from.IsPlaceholder128) || from.IsFloat || from == TypeSymbol.Boolean))
            {
                return Conversion.Explicit;
            }

            if (from == TypeSymbol.String)
            {
                if (to == TypeSymbol.Boolean || to == TypeSymbol.Int32 || to == TypeSymbol.Int64)
                {
                    return Conversion.Explicit;
                }
            }

            if (from is NamedTypeSymbol { TypeKind: TypeKind.Enum } && to == TypeSymbol.Int32)
            {
                return Conversion.Explicit;
            }

            if (from == TypeSymbol.Int32 && to is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return Conversion.Explicit;
            }

            if (from is NamedTypeSymbol fromClass && to is NamedTypeSymbol toClass)
            {
                if (toClass.IsInterface)
                {
                    // 类/接口 → 其实现的接口（含继承链）：隐式
                    if (fromClass == toClass || fromClass.IsBaseOf(toClass) || fromClass.GetAllInterfaces().Contains(toClass))
                    {
                        return Conversion.Implicit;
                    }
                }
                else if (fromClass.IsInterface)
                {
                    // 接口 → 类：仅显式（cast）
                    if (toClass.IsBaseOf(fromClass) || toClass.GetAllInterfaces().Contains(fromClass))
                    {
                        return Conversion.Explicit;
                    }
                }
                else
                {
                    // 派生类 → 基类：隐式（IsBaseOf(t) = this 在 t 的继承链上）
                    if (toClass.IsBaseOf(fromClass))
                    {
                        return Conversion.Implicit;
                    }

                    // 基类 → 派生类：显式（cast）
                    if (fromClass.IsBaseOf(toClass))
                    {
                        return Conversion.Explicit;
                    }
                }
            }

            return Conversion.None;
        }
    }
}
