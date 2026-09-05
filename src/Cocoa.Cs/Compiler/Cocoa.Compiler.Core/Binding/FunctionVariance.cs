using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 委托/函数类型方差赋值兼容判定（6e-M22 委托真实类型化）：
    /// 具名 delegate（含泛型实例化 <c>D&lt;in T, out R&gt;</c>）与 fnty 之间按「参数逆变 + 返回协变」的
    /// 引用转换判定赋值兼容。方差仅限引用转换（identity 或类/接口引用上转），值类型/数值拓宽不参与（对齐 C#）。
    /// 恒等（同形状）是方差兼容的子集，故该判定可安全替换原有严格相等检查。
    /// </summary>
    public static class FunctionVariance
    {
        /// <summary>提取值/目标的函数签名：FunctionTypeSymbol 直返；delegate 类（含泛型实例化）取 Invoke 签名；其余 null。</summary>
        public static FunctionTypeSymbol? SignatureOf(TypeSymbol type)
        {
            if (type is FunctionTypeSymbol functionType)
            {
                return functionType;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateClass)
            {
                return delegateClass.DelegateSignature();
            }

            return null;
        }

        /// <summary>
        /// 来源值类型（source）可否赋给目标签名（targetSignature）：
        /// 参数逆变——目标参数位可赋给来源参数位（target.P → source.P 引用转换）；
        /// 返回协变——来源返回可赋给目标返回（source.R → target.R 引用转换）。
        /// 参数元数不一致或任一位不满足引用转换 → false。
        /// </summary>
        public static bool IsVarianceCompatible(TypeSymbol source, FunctionTypeSymbol? targetSignature)
        {
            var target = targetSignature;
            if (target == null)
            {
                return false;
            }

            var sourceSignature = SignatureOf(source);
            if (sourceSignature == null || sourceSignature.ParameterTypes.Length != target.ParameterTypes.Length)
            {
                return false;
            }

            for (var i = 0; i < sourceSignature.ParameterTypes.Length; i++)
            {
                if (!IsReferenceAssignable(target.ParameterTypes[i], sourceSignature.ParameterTypes[i]))
                {
                    return false;
                }
            }

            return IsReferenceAssignable(sourceSignature.ReturnType, target.ReturnType);
        }

        /// <summary>
        /// 引用转换判定：identity 或引用类型间引用上转。identity 与继承链均按**名**判定
        /// （FullName/mangle），与 FunctionTypeSymbol/ArrayOf 等按名跨编译去重缓存语义一致——
        /// 两次编译的同名普通类符号是不同实例，引用链比对会误失配（既有恒等 `==` 因共享缓存
        /// 同实例而掩盖）。值类型（数值/char/bool/枚举）拒绝参与方差——仅可恒等。
        /// </summary>
        public static bool IsReferenceAssignable(TypeSymbol from, TypeSymbol to)
        {
            if (NamesEqual(from, to))
            {
                return true;
            }

            if (IsReferenceTypeOrClassLike(from) && IsReferenceTypeOrClassLike(to))
            {
                if (from is NamedTypeSymbol fromNamed && to is NamedTypeSymbol toNamed)
                {
                    return IsNamedReferenceAssignable(fromNamed, toNamed);
                }
            }

            return false;
        }

        /// <summary>按名字比较两个类型的"身份"（类全名/基元名/数组名/fnty mangle）。</summary>
        private static bool NamesEqual(TypeSymbol a, TypeSymbol b)
        {
            if (a is NamedTypeSymbol an && b is NamedTypeSymbol bn)
            {
                return an.FullName == bn.FullName;
            }

            if (a is ArrayTypeSymbol aa && b is ArrayTypeSymbol ab)
            {
                return aa.Name == ab.Name;
            }

            return a.Name == b.Name;
        }

        private static bool IsNamedReferenceAssignable(NamedTypeSymbol from, NamedTypeSymbol target)
        {
            // 目标接口：来源实现（含继承链，按名）
            if (target.IsInterface)
            {
                return from.GetAllInterfaces().Any(i => i.FullName == target.FullName);
            }

            // 目标类：来源沿基类链上溯（按名）；不容许向派生方向的转换（方差仅 upcast）
            for (var current = from; current != null; current = current.BaseType)
            {
                if (current.FullName == target.FullName)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReferenceTypeOrClassLike(TypeSymbol type)
        {
            if (type.IsValueType)
            {
                return false;
            }

            // 类型参数（泛型上下文重绑）：保守仅恒等（上方 from==to 已兜底）。
            if (type is TypeParameterSymbol)
            {
                return false;
            }

            return type is FunctionTypeSymbol || type is NamedTypeSymbol || type == TypeSymbol.String || type.ElementType != null;
        }
    }
}