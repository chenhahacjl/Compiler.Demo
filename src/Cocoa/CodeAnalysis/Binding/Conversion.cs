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

            if (from != TypeSymbol.Void && to == TypeSymbol.Any)
            {
                return Conversion.Implicit;
            }

            if (from == TypeSymbol.Any && to != TypeSymbol.Void)
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

            if (from == TypeSymbol.String)
            {
                if (to == TypeSymbol.Boolean || to == TypeSymbol.Int32 || to == TypeSymbol.Int64)
                {
                    return Conversion.Explicit;
                }
            }

            if (from is EnumTypeSymbol && to == TypeSymbol.Int32)
            {
                return Conversion.Explicit;
            }

            if (from == TypeSymbol.Int32 && to is EnumTypeSymbol)
            {
                return Conversion.Explicit;
            }

            if (from is ClassTypeSymbol fromClass && to is ClassTypeSymbol toClass)
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
                    // 派生类 → 基类：隐式；基类 → 派生类：显式（cast）
                    if (fromClass.IsBaseOf(toClass))
                    {
                        return Conversion.Implicit;
                    }

                    if (toClass.IsBaseOf(fromClass))
                    {
                        return Conversion.Explicit;
                    }
                }
            }

            return Conversion.None;
        }
    }
}
