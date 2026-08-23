using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 6e-M19 M2-c：System.Object 内建成员面（编译器内建虚方法符号，非 stdlib facade 源码）。
    /// 虚方法语义与 facade 静态降级互斥（override 需要动态分派），故直接向
    /// <see cref="ClassTypeSymbol.SystemObject"/> 单例注入 FunctionSymbol：
    /// 用户类成员查找沿 BaseType 链自然上溯命中；override 校验经 OverriddenMethod 回填；
    /// 三后端按 <see cref="FunctionSymbol.BuiltinKind"/> 分发（IL box+callvirt / Evaluator CLR / native M4 vtable）。
    /// 单例为 static readonly（跨编译共享），<see cref="Ensure"/> 幂等。
    /// </summary>
    internal static class SystemObjectMembers
    {
        private static readonly BuiltinSpec[] _specs =
        {
            new BuiltinSpec(BuiltinKind.ObjectToString, "ToString", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.ObjectGetHashCode, "GetHashCode", TypeSymbol.Int32, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.ObjectEquals, "Equals", TypeSymbol.Boolean, new[] { ("other", TypeSymbol.Any) }),
            new BuiltinSpec(BuiltinKind.ObjectGetType, "GetType", ClassTypeSymbol.SystemType, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.ObjectStaticEquals, "Equals", TypeSymbol.Boolean, new[] { ("a", TypeSymbol.Any), ("b", TypeSymbol.Any) }),
            new BuiltinSpec(BuiltinKind.ObjectReferenceEquals, "ReferenceEquals", TypeSymbol.Boolean, new[] { ("a", TypeSymbol.Any), ("b", TypeSymbol.Any) }),
            new BuiltinSpec(BuiltinKind.TypeName, "get_Name", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.TypeFullName, "get_FullName", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
        };

        private static bool _initialized;

        /// <summary>实例虚 ToString(): string。</summary>
        public static readonly FunctionSymbol ToString = CreateInstance(BuiltinKind.ObjectToString);

        /// <summary>实例虚 GetHashCode(): int。</summary>
        public static readonly FunctionSymbol GetHashCode = CreateInstance(BuiltinKind.ObjectGetHashCode);

        /// <summary>实例虚 Equals(other): bool（C# Equals(object)；any 即语言顶类型）。</summary>
        public static readonly FunctionSymbol Equals = CreateInstance(BuiltinKind.ObjectEquals);

        /// <summary>实例非虚 GetType(): System.Type（C# 同款非虚，override 校验自然拒绝）。</summary>
        public static readonly FunctionSymbol GetType = CreateInstance(BuiltinKind.ObjectGetType);

        /// <summary>静态 Equals(a, b): bool。</summary>
        public static readonly FunctionSymbol StaticEquals = CreateStatic(BuiltinKind.ObjectStaticEquals);

        /// <summary>静态 ReferenceEquals(a, b): bool。</summary>
        public static readonly FunctionSymbol ReferenceEquals = CreateStatic(BuiltinKind.ObjectReferenceEquals);

        /// <summary>Type.Name 只读属性 getter（M3-b；IL get_Name / Evaluator CLR / native M4 vtable 名字）。</summary>
        public static readonly FunctionSymbol TypeName = CreateInstance(BuiltinKind.TypeName);

        /// <summary>Type.FullName 只读属性 getter。</summary>
        public static readonly FunctionSymbol TypeFullName = CreateInstance(BuiltinKind.TypeFullName);

        /// <summary>
        /// 向 SystemObject/SystemType 单例注入成员面（幂等）。须先于类成员绑定调用，
        /// 保证用户类 override 解析与成员沿链上溯可见。SystemType 继承 SystemObject（Type 值可用 ToString/GetType 等）。
        /// </summary>
        internal static void Ensure()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            var obj = ClassTypeSymbol.SystemObject;
            obj.AddMethod(ToString);
            obj.AddMethod(GetHashCode);
            obj.AddMethod(Equals);
            obj.AddMethod(GetType);
            obj.AddMethod(StaticEquals);
            obj.AddMethod(ReferenceEquals);

            var type = ClassTypeSymbol.SystemType;
            type.BaseType = ClassTypeSymbol.SystemObject;
            var nameProperty = new PropertySymbol("Name", TypeSymbol.String, type, TypeName, setter: null, Visibility.Public, isStatic: false);
            var fullNameProperty = new PropertySymbol("FullName", TypeSymbol.String, type, TypeFullName, setter: null, Visibility.Public, isStatic: false);
            type.AddProperty(nameProperty);
            type.AddProperty(fullNameProperty);
            type.AddMethod(TypeName);
            type.AddMethod(TypeFullName);
        }

        /// <summary>是否为 Object/Type 内建单例（`.cod` 序列化跳过 cls 壳与方法回填的判据）。</summary>
        internal static bool IsBuiltinSystemClass(ClassTypeSymbol classType)
            => classType == ClassTypeSymbol.SystemObject || classType == ClassTypeSymbol.SystemType;

        /// <summary>按 BuiltinKind 解析单例符号（`.cod` 读侧重建时复用，保证发射器识别内置）。</summary>
        internal static FunctionSymbol? GetByKind(BuiltinKind kind) => kind switch
        {
            BuiltinKind.ObjectToString => ToString,
            BuiltinKind.ObjectGetHashCode => GetHashCode,
            BuiltinKind.ObjectEquals => Equals,
            BuiltinKind.ObjectGetType => GetType,
            BuiltinKind.ObjectStaticEquals => StaticEquals,
            BuiltinKind.ObjectReferenceEquals => ReferenceEquals,
            BuiltinKind.TypeName => TypeName,
            BuiltinKind.TypeFullName => TypeFullName,
            _ => null,
        };

        /// <summary>按枚举名解析种类（`.cod` v1 序列化用名称字符串，改名不依赖枚举顺序）。</summary>
        internal static BuiltinKind? GetByKindName(string name)
        {
            foreach (var spec in _specs)
            {
                if (string.Equals(spec.Kind.ToString(), name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return spec.Kind;
                }
            }

            return null;
        }

        private static FunctionSymbol CreateInstance(BuiltinKind kind)
        {
            var function = Create(kind);
            // 虚方法仅 Object 四成员中的三个（GetType 非虚，C# 同构）；Type 属性 getter 非虚
            function.IsVirtual = kind != BuiltinKind.ObjectGetType &&
                                 kind != BuiltinKind.TypeName &&
                                 kind != BuiltinKind.TypeFullName;
            return function;
        }

        private static FunctionSymbol CreateStatic(BuiltinKind kind)
        {
            var function = Create(kind);
            function.IsStatic = true;
            return function;
        }

        private static FunctionSymbol Create(BuiltinKind kind)
        {
            var spec = _specs.First(s => s.Kind == kind);
            var parameters = spec.Parameters.Select((p, i) => new ParameterSymbol(p.Name, p.Type, i)).ToImmutableArray();
            return new FunctionSymbol(spec.Name, parameters, spec.ReturnType, containingClass: ClassTypeSymbol.SystemObject, builtinKind: kind);
        }
    }
}
