using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 6e-M19 M2-c：System.Object 内建成员面（编译器内建虚方法符号，非 stdlib facade 源码）。
    /// 虚方法语义与 facade 静态降级互斥（override 需要动态分派），故直接向
    /// <see cref="NamedTypeSymbol.SystemObject"/> 单例注入 FunctionSymbol：
    /// 用户类成员查找沿 BaseType 链自然上溯命中；override 校验经 OverriddenMethod 回填；
    /// 三后端按 <see cref="FunctionSymbol.BuiltinKind"/> 分发（IL box+callvirt / Evaluator CLR / native M4 vtable）。
    /// 单例为 static readonly（跨编译共享），<see cref="Ensure"/> 幂等。
    /// </summary>
    public static class SystemObjectMembers
    {
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

        /// <summary>Type.IsClass 只读属性 getter。</summary>
        public static readonly FunctionSymbol TypeIsClass = CreateInstance(BuiltinKind.TypeIsClass);

        /// <summary>Type.IsValueType 只读属性 getter。</summary>
        public static readonly FunctionSymbol TypeIsValueType = CreateInstance(BuiltinKind.TypeIsValueType);

        /// <summary>Type.GetMethods(): string[]（实例/静态方法名字符串数组，反射面简化子集）。</summary>
        public static readonly FunctionSymbol TypeGetMethods = CreateInstance(BuiltinKind.TypeGetMethods);

        /// <summary>Type.GetMethod(name): string（按精确名返回规范方法名，未找到空串；宿主反射面简化子集）。</summary>
        public static readonly FunctionSymbol TypeGetMethod = CreateInstance(BuiltinKind.TypeGetMethod);

        /// <summary>Type.HasMethod(name): bool（精确名方法存在性检查，全可见性）。</summary>
        public static readonly FunctionSymbol TypeHasMethod = CreateInstance(BuiltinKind.TypeHasMethod);

        /// <summary>
        /// 向 SystemObject/SystemType 单例注入成员面（幂等）。须先于类成员绑定调用，
        /// 保证用户类 override 解析与成员沿链上溯可见。SystemType 继承 SystemObject（Type 值可用 ToString/GetType 等）。
        /// </summary>
        public static void Ensure()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            var obj = NamedTypeSymbol.SystemObject;
            obj.AddMethod(ToString);
            obj.AddMethod(GetHashCode);
            obj.AddMethod(Equals);
            obj.AddMethod(GetType);
            obj.AddMethod(StaticEquals);
            obj.AddMethod(ReferenceEquals);

            var type = NamedTypeSymbol.SystemType;
            type.BaseType = NamedTypeSymbol.SystemObject;
            var nameProperty = new PropertySymbol("Name", TypeSymbol.String, type, TypeName, setter: null, Visibility.Public, isStatic: false);
            var fullNameProperty = new PropertySymbol("FullName", TypeSymbol.String, type, TypeFullName, setter: null, Visibility.Public, isStatic: false);
            var isClassProperty = new PropertySymbol("IsClass", TypeSymbol.Boolean, type, TypeIsClass, setter: null, Visibility.Public, isStatic: false);
            var isValueTypeProperty = new PropertySymbol("IsValueType", TypeSymbol.Boolean, type, TypeIsValueType, setter: null, Visibility.Public, isStatic: false);
            type.AddProperty(nameProperty);
            type.AddProperty(fullNameProperty);
            type.AddProperty(isClassProperty);
            type.AddProperty(isValueTypeProperty);
            type.AddMethod(TypeName);
            type.AddMethod(TypeFullName);
            type.AddMethod(TypeIsClass);
            type.AddMethod(TypeIsValueType);
            type.AddMethod(TypeGetMethods);
            type.AddMethod(TypeGetMethod);
            type.AddMethod(TypeHasMethod);
        }

        /// <summary>是否为 Object/Type 内建单例（`.coa` 序列化跳过 cls 壳与方法回填的判据）。</summary>
        public static bool IsBuiltinSystemClass(NamedTypeSymbol classType)
            => classType == NamedTypeSymbol.SystemObject || classType == NamedTypeSymbol.SystemType;

        /// <summary>按 BuiltinKind 解析单例符号（`.coa` 读侧重建时复用，保证发射器识别内置）。</summary>
        public static FunctionSymbol? GetByKind(BuiltinKind kind) => kind switch
        {
            BuiltinKind.ObjectToString => ToString,
            BuiltinKind.ObjectGetHashCode => GetHashCode,
            BuiltinKind.ObjectEquals => Equals,
            BuiltinKind.ObjectGetType => GetType,
            BuiltinKind.ObjectStaticEquals => StaticEquals,
            BuiltinKind.ObjectReferenceEquals => ReferenceEquals,
            BuiltinKind.TypeName => TypeName,
            BuiltinKind.TypeFullName => TypeFullName,
            BuiltinKind.TypeIsClass => TypeIsClass,
            BuiltinKind.TypeIsValueType => TypeIsValueType,
            BuiltinKind.TypeGetMethods => TypeGetMethods,
            BuiltinKind.TypeGetMethod => TypeGetMethod,
            BuiltinKind.TypeHasMethod => TypeHasMethod,
            _ => null,
        };

        /// <summary>按枚举名解析种类（`.coa` v1 序列化用名称字符串，改名不依赖枚举顺序）——统一表 <see cref="BuiltinFunctions"/>。</summary>
        internal static BuiltinKind? GetByKindName(string name) => BuiltinFunctions.GetMemberByKindName(name);

        private static FunctionSymbol CreateInstance(BuiltinKind kind)
        {
            var function = Create(kind);
            // 虚方法仅 Object 四成员中的三个（GetType 非虚，C# 同构）；Type 全部成员非虚
            function.IsVirtual = kind != BuiltinKind.ObjectGetType &&
                                 kind != BuiltinKind.TypeName &&
                                 kind != BuiltinKind.TypeFullName &&
                                 kind != BuiltinKind.TypeIsClass &&
                                 kind != BuiltinKind.TypeIsValueType &&
                                 kind != BuiltinKind.TypeGetMethods &&
                                 kind != BuiltinKind.TypeGetMethod &&
                                 kind != BuiltinKind.TypeHasMethod;
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
            var spec = BuiltinFunctions.FindSpec(kind)!;
            var parameters = spec.Parameters.Select((p, i) => new ParameterSymbol(p.Name, p.Type, i)).ToImmutableArray();
            return new FunctionSymbol(spec.Name, parameters, spec.ReturnType, containingClass: NamedTypeSymbol.SystemObject, builtinKind: kind);
        }
    }
}
