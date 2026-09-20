using System;
using System.Text;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Documentation
{
    /// <summary>
    /// 6e-M24：.NET DocID 风格的文档标识符生成器（纯函数）。
    /// 为符号生成唯一 XML 成员名（如 <c>T:System.Console</c>、<c>M:MyLib.Add(i32,i32)</c>）。
    ///
    /// 命名规则（对齐 .NET XML Documentation 标记）：
    ///   T:Namespace.TypeName              类/接口/枚举/委托
    ///   M:Namespace.TypeName.MethodName(ParamType1,ParamType2)   方法（含静态/实例/构造）
    ///   F:Namespace.TypeName.FieldName     字段
    ///   P:Namespace.TypeName.PropertyName  属性
    ///   E:Namespace.TypeName.EventName     事件
    ///   N:Namespace                        命名空间
    ///
    /// 特殊处理：
    ///   构造函数 → <c>#ctor</c>
    ///   泛型类型 → 反引号+ arity（如 <c>MyLib.List`1</c>）
    ///   泛型方法 → 反引号+ arity（如 <c>MyLib.Map`2(TSource,TResult)</c>）
    ///   数组类型 → 全名（如 <c>System.Int32[]</c>）
    ///   函数类型 → 委托全名（如 <c>System.Func`2</c>）
    ///   命名空间限定：一律用 <c>.</c> 分隔（无 <c>/</c>）。
    /// </summary>
    public static class DocIdBuilder
    {
        /// <summary>
        /// 为符号生成 DocID 字符串。返回 null 当符号不支持文档化（如合成符号）。
        /// </summary>
        public static string? GetDocId(Symbol symbol)
        {
            return symbol switch
            {
                FunctionSymbol fn => BuildFunctionDocId(fn),
                NamedTypeSymbol type => BuildTypeDocId(type),
                FieldSymbol field => $"F:{GetFullTypeName(field.ContainingClass)}.{field.Name}",
                PropertySymbol prop => $"P:{GetFullTypeName(prop.ContainingClass)}.{prop.Name}",
                EventSymbol evt => $"E:{GetFullTypeName(evt.ContainingClass)}.{evt.Name}",
                ParameterSymbol par => null, // 参数不出现在顶层
                _ => null,
            };
        }

        private static string BuildFunctionDocId(FunctionSymbol fn)
        {
            var prefix = fn.IsConstructor ? "M" : "M";
            var containingType = fn.ContainingClass != null
                ? GetFullTypeName(fn.ContainingClass)
                : fn.Namespace;
            var name = fn.IsConstructor ? "#ctor" : fn.Name;

            // 泛型方法 arity
            if (fn.TypeParameters.Length > 0)
            {
                name += $"`{fn.TypeParameters.Length}";
            }

            var sb = new StringBuilder();
            sb.Append(prefix);
            sb.Append(':');
            if (containingType.Length > 0)
            {
                sb.Append(containingType);
                sb.Append('.');
            }

            sb.Append(name);
            if (fn.Parameters.Length > 0)
            {
                sb.Append('(');
                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(GetDocIdTypeName(fn.Parameters[i].Type));
                }

                sb.Append(')');
            }

            return sb.ToString();
        }

        private static string BuildTypeDocId(NamedTypeSymbol type)
        {
            var prefix = type.TypeKind switch
            {
                TypeKind.Enum => "T",
                TypeKind.Delegate => "T",
                _ => "T",
            };

            var fullName = GetFullTypeName(type);

            // 泛型 arity
            if (type.TypeParameters.Length > 0)
            {
                fullName += $"`{type.TypeParameters.Length}";
            }

            return $"{prefix}:{fullName}";
        }

        private static string GetFullTypeName(NamedTypeSymbol? type)
        {
            if (type == null)
            {
                return "";
            }

            var special = GetSpecialTypeFullName(type.SpecialType);
            if (special != null)
            {
                return special;
            }

            var ns = type.Namespace;
            var name = type.Name;

            if (string.IsNullOrEmpty(ns))
            {
                return name;
            }

            return $"{ns}.{name}";
        }

        /// <summary>
        /// 关键字别名 → .NET BCL 全名（设计 §5.2：i32/int → System.Int32）。
        /// 非知名 BCL 类型返回 null。
        /// </summary>
        private static string? GetSpecialTypeFullName(SpecialType specialType) => specialType switch
        {
            SpecialType.System_Object => "System.Object",
            SpecialType.System_String => "System.String",
            SpecialType.System_Char => "System.Char",
            SpecialType.System_Boolean => "System.Boolean",
            SpecialType.System_Int8 => "System.SByte",
            SpecialType.System_UInt8 => "System.Byte",
            SpecialType.System_Int16 => "System.Int16",
            SpecialType.System_UInt16 => "System.UInt16",
            SpecialType.System_Int32 => "System.Int32",
            SpecialType.System_UInt32 => "System.UInt32",
            SpecialType.System_Int64 => "System.Int64",
            SpecialType.System_UInt64 => "System.UInt64",
            SpecialType.System_Single => "System.Single",
            SpecialType.System_Double => "System.Double",
            SpecialType.System_Int128 => "System.Int128",
            SpecialType.System_UInt128 => "System.UInt128",
            SpecialType.System_IntPtr => "System.IntPtr",
            SpecialType.System_UIntPtr => "System.UIntPtr",
            SpecialType.System_Void => "System.Void",
            _ => null,
        };

        private static string GetDocIdTypeName(TypeSymbol type)
        {
            return type switch
            {
                NamedTypeSymbol named => GetFullTypeName(named) + (named.TypeParameters.Length > 0 ? $"`{named.TypeParameters.Length}" : ""),
                ArrayTypeSymbol array => $"{GetDocIdTypeName(array.ElementType!)}[]",
                FunctionTypeSymbol func => BuildFunctionTypeDocId(func),
                _ => type.Name,
            };
        }

        private static string BuildFunctionTypeDocId(FunctionTypeSymbol func)
        {
            var sb = new StringBuilder();
            sb.Append("System.Func`");
            sb.Append(func.ParameterTypes.Length + 1); // 参数 + 返回
            return sb.ToString();
        }
    }
}
