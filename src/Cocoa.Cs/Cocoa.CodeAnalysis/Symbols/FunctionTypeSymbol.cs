using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 函数类型（6e-M22 C3）：结构化 `(A, B) -&gt; R`——参数类型元组 + 返回类型，**不变型**。
    /// 工厂缓存保证同形状同实例（对齐 TypeSymbol.ArrayOf），引用相等即结构相等；
    /// 转换语义天然成立：同形状 = 恒等转换，异形状无转换（Conversion.Classify 首位 from==to）。
    /// Name 为 Encode v3 mangle（`Func$!System.Int32__!System.Boolean` 形态，`$` 参数分隔、`__` 返回后缀）。
    /// </summary>
    public sealed class FunctionTypeSymbol : TypeSymbol
    {
        private static readonly ConcurrentDictionary<string, FunctionTypeSymbol> _cache = new();

        private FunctionTypeSymbol(ImmutableArray<TypeSymbol> parameterTypes, TypeSymbol returnType, string mangledName)
            : base(mangledName)
        {
            ParameterTypes = parameterTypes;
            ReturnType = returnType;
        }

        public ImmutableArray<TypeSymbol> ParameterTypes { get; }

        public TypeSymbol ReturnType { get; }

        /// <summary>工厂（去重）：参数/返回类型逐一经 Encode 编码组成缓存键。</summary>
        public static FunctionTypeSymbol Get(ImmutableArray<TypeSymbol> parameterTypes, TypeSymbol returnType)
        {
            var key = CacheKey(parameterTypes, returnType);

            return _cache.GetOrAdd(key, _ => new FunctionTypeSymbol(
                parameterTypes,
                returnType,
                MangledName(parameterTypes, returnType)));
        }

        /// <summary>mangle 名：`Func$&lt;参数编码 $ 分隔&gt;__&lt;返回编码&gt;`；无参为 `Func__&lt;返回编码&gt;`。`$`/`_` 序列非用户标识符，结构性隔离。</summary>
        public static string MangledName(ImmutableArray<TypeSymbol> parameterTypes, TypeSymbol returnType)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("Func");

            for (var i = 0; i < parameterTypes.Length; i++)
            {
                builder.Append('$');
                builder.Append(GenericTypeInstantiator.Encode(parameterTypes[i]));
            }

            builder.Append("__");
            builder.Append(GenericTypeInstantiator.Encode(returnType));

            return builder.ToString();
        }

        private static string CacheKey(ImmutableArray<TypeSymbol> parameterTypes, TypeSymbol returnType)
        {
            var builder = new System.Text.StringBuilder();

            foreach (var parameter in parameterTypes)
            {
                builder.Append('|');
                builder.Append(GenericTypeInstantiator.Encode(parameter));
            }

            builder.Append("__");
            builder.Append(GenericTypeInstantiator.Encode(returnType));

            return builder.ToString();
        }
    }
}
