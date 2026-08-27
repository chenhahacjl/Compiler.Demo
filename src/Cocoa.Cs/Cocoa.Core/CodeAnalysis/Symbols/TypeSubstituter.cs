using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类型替换器（6e-M20 单态化核心）：把类型中的类型参数按映射替换为实参。
    /// <list type="bullet">
    /// <item>TypeParameterSymbol → 映射实参（无映射则保留——仍处泛型上下文）</item>
    /// <item>数组类型 → 元素递归替换后 ArrayOf（去重复用）</item>
    /// <item>实例化类 → 实参递归替换后重新 Instantiate（去重缓存）</item>
    /// </list>
    /// </summary>
    internal static class TypeSubstituter
    {
        internal static Dictionary<TypeParameterSymbol, TypeSymbol> BuildMap(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TypeSymbol> arguments)
        {
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();

            for (var i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                map[parameters[i]] = arguments[i];
            }

            return map;
        }

        internal static TypeSymbol Substitute(TypeSymbol type, Dictionary<TypeParameterSymbol, TypeSymbol> map)
        {
            if (type is TypeParameterSymbol parameter)
            {
                return map.TryGetValue(parameter, out var substituted) ? substituted : type;
            }

            // 数组类型：基元符号类无独立数组子类，ElementType 非空即数组
            if (type.ElementType != null && type.Kind == SymbolKind.Type)
            {
                return TypeSymbol.ArrayOf(Substitute(type.ElementType, map));
            }

            if (type is InstantiatedTypeSymbol instantiated)
            {
                var arguments = instantiated.TypeArguments.Select(a => Substitute(a, map)).ToImmutableArray();
                return GenericTypeInstantiator.Instantiate(instantiated.GenericDefinition, arguments);
            }

            // 函数类型（6e-M22 C3）：递归替换参数类型与返回类型（泛型类实例化时，
            // 方法参数中的 (T, T) -> i32 内部 T 必须一并替换为实参）
            if (type is FunctionTypeSymbol functionType)
            {
                var parameterTypes = functionType.ParameterTypes.Select(p => Substitute(p, map)).ToImmutableArray();
                var returnType = Substitute(functionType.ReturnType, map);
                return FunctionTypeSymbol.Get(parameterTypes, returnType);
            }

            return type;
        }
    }
}
