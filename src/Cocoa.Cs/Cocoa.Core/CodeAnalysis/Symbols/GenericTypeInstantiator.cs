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

            // 属性访问器（getter/setter）同时登记在 definition.Methods 与 definition.Properties 中，
            // 若在此一并实例化会与其在下方属性循环里产生的访问器形成两个不同 FunctionSymbol 实例，
            // 导致同一实例化类上出现同名同签名的重复方法 def（非法元数据 → InvalidProgramException）。
            // 故此处跳过访问器，仅由属性循环负责。
            var accessors = new HashSet<FunctionSymbol>();
            foreach (var property in definition.Properties)
            {
                if (property.Getter != null) accessors.Add(property.Getter);
                if (property.Setter != null) accessors.Add(property.Setter);
            }

            foreach (var method in definition.Methods)
            {
                if (accessors.Contains(method)) continue;

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
                instantiated.AddProperty(new PropertySymbol(property.Name, TypeSubstituter.Substitute(property.Type, map), instantiated, getter, setter, property.Visibility, property.IsStatic, property.IsIndexer));
            }
        }

        /// <summary>方法签名替换 + 关联容器改指实例化类（方法体在 G2 单态化阶段经语法重绑接管）。</summary>
        internal static FunctionSymbol SubstituteMethod(FunctionSymbol method, InstantiatedTypeSymbol containingClass, Dictionary<TypeParameterSymbol, TypeSymbol> map, string? nameOverride = null)
        {
            var parameters = method.Parameters
                .Select(p => new ParameterSymbol(p.Name, TypeSubstituter.Substitute(p.Type, map), p.Ordinal, p.IsOut, p.IsRef))
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
                // 方法级类型参数原样保留（6e-M22 C1）：类实例化仅替换类参数，方法自己的 <U> 仍是模板，
                // 签名中的类参数已被替换、U 引用保持开放——调用期经 GenericMethodInstantiator 二次实例化
                TypeParameters = method.TypeParameters,
            };
        }
        /// <summary>
        /// mangle 名（6e-M20 Encode v3，CLR 风格）：`List`1#!System.Int32`。
        /// 结构 = 定义全限定名 + backtick 元数 + `#` + 实参（`$` 分隔）。
        /// `!` 前缀标记编译器权威身份（内建基元/开放类型参数）——均为用户不可声明实体
        /// （!/`/#/$ 均非标识符字符），与用户 FullName 结构性隔离，零名字禁令自足注入。
        /// </summary>
        public static string MangledName(ClassTypeSymbol definition, ImmutableArray<TypeSymbol> arguments)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(definition.FullName);
            builder.Append('`');
            builder.Append(arguments.Length);
            builder.Append('#');

            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('$');
                }

                builder.Append(Encode(arguments[i]));
            }

            return builder.ToString();
        }

        /// <summary>基元内建 → facade 全限定权威映射（`!` 前缀 = 编译器权威身份标记）。</summary>
        private static readonly Dictionary<TypeSymbol, string> PrimitiveEncodeNames = new Dictionary<TypeSymbol, string>
        {
            [TypeSymbol.Int8] = "!System.SByte",
            [TypeSymbol.Int16] = "!System.Int16",
            [TypeSymbol.Int32] = "!System.Int32",
            [TypeSymbol.Int64] = "!System.Int64",
            [TypeSymbol.UInt8] = "!System.Byte",
            [TypeSymbol.UInt16] = "!System.UInt16",
            [TypeSymbol.UInt32] = "!System.UInt32",
            [TypeSymbol.UInt64] = "!System.UInt64",
            [TypeSymbol.Float] = "!System.Single",
            [TypeSymbol.Double] = "!System.Double",
            [TypeSymbol.Boolean] = "!System.Boolean",
            [TypeSymbol.Char] = "!System.Char",
            [TypeSymbol.String] = "!System.String",
            [TypeSymbol.Any] = "!System.Object",
            [TypeSymbol.Void] = "!System.Void",
        };

        /// <summary>基元权威编码反解（6e-G7 S1：.cod 类型流读侧）。</summary>
        private static readonly Dictionary<string, TypeSymbol> PrimitiveDecodeNames =
            PrimitiveEncodeNames.ToDictionary(pair => pair.Value, pair => pair.Key);

        internal static bool TryDecodePrimitive(string encoded, out TypeSymbol type)
        {
            return PrimitiveDecodeNames.TryGetValue(encoded, out type!);
        }

        /// <summary>类型实参编码（mangle 与缓存键共用）：`!` 权威实体 / FullName 点保留 / 数组 `[]` 后缀 / 嵌套实例化递归。</summary>
        public static string Encode(TypeSymbol type)
        {
            // 开放类型参数（定义期壳）：! + 裸名
            if (type is TypeParameterSymbol parameter)
            {
                return "!" + parameter.Name;
            }

            // 数组：元素编码 + [] 后缀（[] 非标识符字符，注入安全）
            if (type.ElementType != null && type.Kind == SymbolKind.Type)
            {
                return Encode(type.ElementType) + "[]";
            }

            // 内建基元：! + facade 全限定
            if (PrimitiveEncodeNames.TryGetValue(type, out var primitiveName))
            {
                return primitiveName;
            }

            // 嵌套实例化：递归完整编码（含 backtick 元数与 #）
            if (type is InstantiatedTypeSymbol nested)
            {
                return MangledName(nested.GenericDefinition, nested.TypeArguments);
            }

            // 用户类/接口/枚举：FullName 点原样保留（点非标识符字符，一一映射）
            if (type is ClassTypeSymbol classType)
            {
                return classType.FullName;
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
