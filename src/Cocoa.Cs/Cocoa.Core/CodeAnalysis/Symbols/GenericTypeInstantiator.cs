using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 泛型实例化器（6e-M20 编译期单态化）：
    /// <list type="bullet">
    /// <item>按 (泛型定义, 实参元组) 全局去重缓存——同一 `List&lt;int&gt;` 全编译共享同一实例化类</item>
    /// <item>mangle 命名：`List_int` / `List_List_int`（实参名编码，`[]` 转下划线，与 IL EncodeTypeNameForMethodName 同约定）</item>
    /// <item>成员构造：字段/方法签名/属性/基类/接口经 <see cref="TypeSubstituter"/> 替换后填充</item>
    /// </list>
    /// 循环引用安全：缓存槽先于成员填充预留（`class Node&lt;T&gt; { _next: Node&lt;T&gt; }` 自引用不再递归）。
    /// </summary>
    public static class GenericTypeInstantiator
    {
        private static readonly ConcurrentDictionary<string, ClassTypeSymbol> _cache = new();

        /// <summary>
        /// 实例化（去重）：definition 须为泛型定义类，实参数须与类型参数数一致。
        /// 成员<b>惰性物化</b>——首次成员访问时从定义快照替换填充（定义可能尚未完成绑定，前向引用安全）。
        /// </summary>
        public static ClassTypeSymbol Instantiate(ClassTypeSymbol definition, ImmutableArray<TypeSymbol> arguments)
        {
            if (!definition.IsGenericDefinition)
            {
                throw new InvalidOperationException($"'{definition.FullName}' is not a generic definition.");
            }

            if (arguments.Length != definition.TypeParameters.Length)
            {
                throw new InvalidOperationException($"Generic type '{definition.Name}' takes {definition.TypeParameters.Length} type arguments but {arguments.Length} were supplied.");
            }

            var key = CacheKey(definition, arguments);

            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // 预留缓存槽（自引用字段类型替换时命中半成品实例，仅取身份不读成员）
            var instantiated = new InstantiatedTypeSymbol(MangledName(definition, arguments), definition.Namespace, definition.Visibility, definition, arguments);
            _cache[key] = instantiated;

            return instantiated;
        }

        /// <summary>填充成员（由 InstantiatedTypeSymbol.EnsureMembersMaterialized 惰性触发；幂等由调用方锁保证）。</summary>
        internal static void Populate(InstantiatedTypeSymbol instantiated)
        {
            var definition = instantiated.GenericDefinition;
            var map = TypeSubstituter.BuildMap(definition.TypeParameters, instantiated.TypeArguments);

            instantiated.IsInterface = definition.IsInterface;
            instantiated.IsAbstract = definition.IsAbstract;
            instantiated.IsSealed = definition.IsSealed;
            instantiated.TypeParameters = ImmutableArray<TypeParameterSymbol>.Empty;

            // 基类/基接口实参化：`List<T> : Collection<T>` → `List<int> : Collection<int>`
            if (definition.BaseType != null)
            {
                instantiated.BaseType = (ClassTypeSymbol)TypeSubstituter.Substitute(definition.BaseType, map);
            }
            else if (!definition.IsInterface)
            {
                instantiated.BaseType = ClassTypeSymbol.SystemObject;
            }

            foreach (var iface in definition.Interfaces)
            {
                instantiated.AddInterface((ClassTypeSymbol)TypeSubstituter.Substitute(iface, map));
            }

            foreach (var baseInterface in definition.BaseInterfaces)
            {
                instantiated.AddBaseInterface((ClassTypeSymbol)TypeSubstituter.Substitute(baseInterface, map));
            }

            foreach (var field in definition.Fields)
            {
                instantiated.AddField(new FieldSymbol(field.Name, TypeSubstituter.Substitute(field.Type, map), field.Visibility, instantiated, field.IsReadonly, field.IsStatic));
            }

            foreach (var method in definition.Methods)
            {
                // 实例构造器名须随实例化类名（`GetMethod(classType.Name)` 是全编译器的构造查找约定）；
                // 静态构造 `.cctor` 与普通方法名不变
                var nameOverride = method.IsConstructor && !method.IsStatic && method.Name == definition.Name
                    ? instantiated.Name
                    : null;

                instantiated.AddMethod(SubstituteMethod(method, instantiated, map, nameOverride));
            }

            foreach (var property in definition.Properties)
            {
                var getter = property.Getter == null ? null : SubstituteMethod(property.Getter, instantiated, map);
                var setter = property.Setter == null ? null : SubstituteMethod(property.Setter, instantiated, map);
                instantiated.AddProperty(new PropertySymbol(property.Name, TypeSubstituter.Substitute(property.Type, map), instantiated, getter, setter, property.Visibility, property.IsStatic));
            }
        }

        /// <summary>方法签名替换 + 关联容器改指实例化类（方法体在 G2 单态化阶段经语法重绑接管）。</summary>
        internal static FunctionSymbol SubstituteMethod(FunctionSymbol method, InstantiatedTypeSymbol containingClass, Dictionary<TypeParameterSymbol, TypeSymbol> map, string? nameOverride = null)
        {
            var parameters = method.Parameters
                .Select(p => new ParameterSymbol(p.Name, TypeSubstituter.Substitute(p.Type, map), p.Ordinal))
                .ToImmutableArray();
            var returnType = TypeSubstituter.Substitute(method.ReturnType, map);

            return new FunctionSymbol(
                nameOverride ?? method.Name,
                parameters,
                returnType,
                method.Declaration,
                method.IsExtern,
                method.DllName,
                method.CallingConvention,
                containingClass,
                method.Syntax,
                method.Visibility,
                method.BuiltinKind,
                method.Namespace,
                method.EntryPoint,
                method.CharSet)
            {
                IsVirtual = method.IsVirtual,
                IsOverride = method.IsOverride,
                IsAbstract = method.IsAbstract,
                IsSealed = method.IsSealed,
                IsStatic = method.IsStatic,
                IsConstructor = method.IsConstructor,
            };
        }
        /// <summary>mangle 名：`List_int` / `Dict_int_string` / `List_List_int` / `Box_int_Array`（简单名；跨命名空间唯一性由 FullName 承载）。</summary>
        public static string MangledName(ClassTypeSymbol definition, ImmutableArray<TypeSymbol> arguments)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(definition.Name);

            foreach (var argument in arguments)
            {
                builder.Append('_');
                builder.Append(Encode(argument));
            }

            return builder.ToString();
        }

        private static string Encode(TypeSymbol type)
        {
            if (type is TypeParameterSymbol parameter)
            {
                return parameter.Name;
            }

            // 数组：元素类型 + Array 后缀（int[] → int_Array）
            if (type.ElementType != null && type.Kind == SymbolKind.Type)
            {
                return Encode(type.ElementType) + "_Array";
            }

            if (type is InstantiatedTypeSymbol nested)
            {
                return MangledName(nested.GenericDefinition, nested.TypeArguments).Replace('.', '_');
            }

            if (type is ClassTypeSymbol classType)
            {
                return classType.FullName.Replace('.', '_');
            }

            return type.Name;
        }

        private static string CacheKey(ClassTypeSymbol definition, ImmutableArray<TypeSymbol> arguments)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(definition.GetHashCode());
            builder.Append('|');
            builder.Append(definition.FullName);

            foreach (var argument in arguments)
            {
                builder.Append('|');
                builder.Append(Encode(argument));
            }

            return builder.ToString();
        }
    }
}
