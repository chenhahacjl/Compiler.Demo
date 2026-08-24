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

            // mangle 名：`Swap$int` / `Max$int$long`（$ 非法标识符字符，与用户定义 Swap_int 无碰撞）
            var builder = new System.Text.StringBuilder(definition.Name);
            foreach (var argument in arguments)
            {
                builder.Append('$');
                builder.Append(GenericTypeInstantiator.Encode(argument));
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
