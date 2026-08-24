using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 泛型方法实例化器（6e-M20）：`Swap&lt;int&gt;` → 具体方法 Swap_int（签名替换，TypeParameters 清空）。
    /// 与类实例化共享缓存模式；方法体由 Monomorphizer 经语法重绑填充。
    /// </summary>
    public static class GenericMethodInstantiator
    {
        private static readonly ConcurrentDictionary<string, FunctionSymbol> _cache = new();

        // 溯源表（6e-M22 C1）：实例化方法 → (定义, 实参)。Monomorphizer 走查绑定树时经此取重绑所需映射，
        // 免去语法层接收者复解析（局部变量类型在辅助 Binder 中不可见）
        private static readonly ConcurrentDictionary<FunctionSymbol, (FunctionSymbol Definition, ImmutableArray<TypeSymbol> Arguments)> _provenance = new();

        /// <summary>实例化方法是否可溯源；成功输出定义符号与类型实参。</summary>
        internal static bool TryGetProvenance(FunctionSymbol instantiated, out FunctionSymbol definition, out ImmutableArray<TypeSymbol> arguments)
        {
            if (_provenance.TryGetValue(instantiated, out var entry))
            {
                definition = entry.Definition;
                arguments = entry.Arguments;
                return true;
            }

            definition = null!;
            arguments = default;
            return false;
        }

        public static FunctionSymbol Instantiate(FunctionSymbol definition, ImmutableArray<TypeSymbol> arguments)
        {
            if (!definition.IsGenericMethod)
            {
                throw new InvalidOperationException($"'{definition.Name}' is not a generic method.");
            }

            if (arguments.Length != definition.TypeParameters.Length)
            {
                throw new InvalidOperationException($"Generic method '{definition.Name}' takes {definition.TypeParameters.Length} type arguments but {arguments.Length} were supplied.");
            }

            var key = CacheKey(definition, arguments);

            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var map = TypeSubstituter.BuildMap(definition.TypeParameters, arguments);
            var parameters = definition.Parameters
                .Select(p => new ParameterSymbol(p.Name, TypeSubstituter.Substitute(p.Type, map), p.Ordinal))
                .ToImmutableArray();
            var returnType = TypeSubstituter.Substitute(definition.ReturnType, map);

            // mangle 名（Encode v3 CLR 风格）：`Swap`1#!System.Int32`（定义全限定 + backtick 元数 + # 实参表）
            var builder = new System.Text.StringBuilder();
            builder.Append(definition.Namespace.Length == 0 ? definition.Name : definition.Namespace + "." + definition.Name);
            builder.Append('`');
            builder.Append(arguments.Length);
            builder.Append('#');

            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('$');
                }

                builder.Append(GenericTypeInstantiator.Encode(arguments[i]));
            }

            var instantiated = new FunctionSymbol(
                builder.ToString(),
                parameters,
                returnType,
                definition.Declaration,
                definition.IsExtern,
                definition.DllName,
                definition.CallingConvention,
                definition.ContainingClass,
                definition.Syntax,
                definition.Visibility,
                definition.BuiltinKind,
                definition.Namespace,
                definition.EntryPoint,
                definition.CharSet)
            {
                IsStatic = definition.IsStatic,
                IsVirtual = definition.IsVirtual,
                IsOverride = definition.IsOverride,
                IsAbstract = definition.IsAbstract,
                IsSealed = definition.IsSealed,
                IsConstructor = definition.IsConstructor,
            };

            _cache[key] = instantiated;
            _provenance[instantiated] = (definition, arguments);

            return instantiated;
        }

        private static string CacheKey(FunctionSymbol definition, ImmutableArray<TypeSymbol> arguments)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(definition.GetHashCode());
            builder.Append('|');
            builder.Append(definition.EmitName);

            foreach (var argument in arguments)
            {
                builder.Append('|');
                builder.Append(GenericTypeInstantiator.Encode(argument));
            }

            return builder.ToString();
        }
    }
}
