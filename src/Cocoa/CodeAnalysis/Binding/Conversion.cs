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

            if (from == TypeSymbol.Boolean || from == TypeSymbol.Int32 || from == TypeSymbol.Byte)
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

                if (to == TypeSymbol.Byte)
                {
                    return Conversion.Explicit;
                }

                if (to == TypeSymbol.Double)
                {
                    return Conversion.Implicit;
                }
            }

            if (from == TypeSymbol.Byte)
            {
                if (to == TypeSymbol.Int32 || to == TypeSymbol.Double)
                {
                    return Conversion.Implicit;
                }
            }

            if (from == TypeSymbol.Double)
            {
                if (to == TypeSymbol.Int32 || to == TypeSymbol.Byte || to == TypeSymbol.String)
                {
                    return Conversion.Explicit;
                }
            }

            if (from == TypeSymbol.String)
            {
                if (to == TypeSymbol.Boolean || to == TypeSymbol.Int32)
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
