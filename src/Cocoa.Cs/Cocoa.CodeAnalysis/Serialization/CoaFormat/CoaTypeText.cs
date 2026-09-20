using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>类型→文本 写侧编解码（源 <c>CoaSerializer.Symbols.cs</c> 的 TypeRef 族收敛）：
    /// 基元 `@权威记法`、类/枚举全名或库限定 `库名!全名`、开放参数 `!属主.名`、
    /// 实例化 mangle、函数类型 `fnty{...}`。读侧对称解析在 <see cref="CoaTypeResolver"/>。</summary>
    internal static class CoaTypeText
    {
        /// <summary>类型的文本引用：内建/数组用短名（int / int[][]），类/枚举用全名。</summary>
        public static string TypeRef(TypeSymbol type)
        {
            // 6e 跨库里程碑：基元内建 → `@` 权威记法（@i32/@string/@bool…，Rust/LLVM 式位宽名）。
            // 引用相等键（单例稳定），先于 NamedTypeSymbol 分支命中，避免输出 C# 短名 int/string。
            if (GenericTypeInstantiator.TryGetPrimitiveName(type, out var primitiveName))
            {
                return primitiveName;
            }

            // 6e-G7 S1：开放类型参数 → 限定权威键 `!属主全名.参数名`（方法级无属主回落裸名）。
            // 实例化类型 → Encode v3 完整 mangle（backtick 元数 + # + $ 分隔递归实参）。
            if (type is TypeParameterSymbol openParameter)
            {
                return openParameter.OwningClass != null
                    ? "!" + openParameter.OwningClass.FullName + "." + openParameter.Name
                    : "!" + openParameter.Name;
            }

            if (type is InstantiatedTypeSymbol instantiated)
            {
                return EncodeInstantiatedTypeRef(instantiated);
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return LibraryQualify(enumType);
            }

            if (type is NamedTypeSymbol classType)
            {
                return LibraryQualify(classType);
            }

            // 6e-M22/M0-1b：函数类型 `fnty{参数,;返回}`（递归 TypeRef；参数逗号分隔、分号接返回、{} 嵌套）。
            // .coa 词法仅以空白与 () 切分，故嵌套用 {} 避开结构括号。
            if (type is FunctionTypeSymbol functionType)
            {
                var builder = new StringBuilder();
                builder.Append("fnty{");
                for (var i = 0; i < functionType.ParameterTypes.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(TypeRef(functionType.ParameterTypes[i]));
                }

                builder.Append(';');
                builder.Append(TypeRef(functionType.ReturnType));
                builder.Append('}');
                return builder.ToString();
            }

            if (type.ElementType != null)
            {
                // 数组：递归元素 TypeRef（元素为开放类型参数时限定为 !属主.名，如 K[] → !System.Collections.Generic.Dictionary.K[]）
                return TypeRef(type.ElementType) + "[]";
            }

            return type.Name;
        }

        /// <summary>6f-3：库限定全名（复合键写侧）——读入库的类型带来源库名，`库名!全名` 引用唯一化；
        /// 源码声明/系统内建（ContainingLibrary 空）回落裸全名。</summary>
        public static string LibraryQualify(NamedTypeSymbol type)
        {
            return string.IsNullOrEmpty(type.ContainingLibrary)
                ? type.FullName
                : type.ContainingLibrary + "!" + type.FullName;
        }

        /// <summary>
        /// 实例化类型的 .coa 编码（6e-G7 S1）：定义全名 + backtick 元数 + # + $ 分隔实参。
        /// 实参递归走 <see cref="TypeRef"/>——开放参数为限定键 !属主.名（区别于 mangle 缓存键的裸 !T），
        /// 保证跨定义无歧义且读侧可独立解析；基元/类用平名（不含 $、` 等，分隔安全）；嵌套实例化递归。
        /// </summary>
        public static string EncodeInstantiatedTypeRef(InstantiatedTypeSymbol instantiated)
        {
            var builder = new StringBuilder();
            builder.Append(instantiated.GenericDefinition.FullName);
            builder.Append('`');
            builder.Append(instantiated.TypeArguments.Length);
            builder.Append('#');

            for (var i = 0; i < instantiated.TypeArguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('$');
                }

                builder.Append(TypeRef(instantiated.TypeArguments[i]));
            }

            return builder.ToString();
        }

        /// <summary>6e-M26：泛型开放绑定体确定性排序键（GenericOpenBodies 为 ImmutableDictionary，枚举不稳定）。</summary>
        public static string GenericOpenSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }
    }
}
