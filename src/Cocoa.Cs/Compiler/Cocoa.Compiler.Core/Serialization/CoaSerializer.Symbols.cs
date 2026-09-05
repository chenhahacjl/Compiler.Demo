using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization
{
    public static partial class CoaSerializer
    {
        private static void EmitEnumSymbol(Writer w, Registry registry, NamedTypeSymbol e)
        {
            w.Open("enum");
            w.Field(e.FullName);
            var members = e.MemberNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            w.Field("members:" + members.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var name in members)
            {
                e.TryGetMember(name, out var value);
                w.Open(name);
                w.Field(value);
                w.End();
            }
            w.End();
        }

        /// <summary>6e-M19 M2-c：内建单例（System.Object/System.Type）按全名序列化，读侧映射回单例。</summary>
        private static void EmitBuiltinSystemClass(Writer w, Registry registry, NamedTypeSymbol classType)
        {
            w.Open("systype");
            w.Field(classType.FullName);
            w.End();
        }

        private static void EmitClassSymbol(Writer w, Registry registry, NamedTypeSymbol classType)
        {
            w.Open("cls");
            w.Field(classType.FullName);
            w.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 6e-Step D-c：delegate 标记（Invoke 按 fn owner 携带，读侧据此重建 TypeKind.Delegate）
            if (classType.TypeKind == TypeKind.Delegate)
            {
                w.Field("tk:Delegate");
            }
            // 6e-G7/M0-1a：接口位 + 实现接口列表（供消费方 IsInterface 判定与接口成员沿 Interfaces 链解析）
            w.Field("iface:" + BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            w.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                w.Field(TypeRef(iface));
            }
            // 序列化全部静态方法签名（6e-M18：容器类允许带体静态方法，如 Console.WriteLine/Math.Max；syscall/extern 亦为静态）。
            // 方法本体由各自 fn 条目携带（owner 字段回填类归属），这里列 Name[参数类型] 供阅读（无参省略方括号）。
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(MethodSignature(method));
            }
            // 6e-Step D-a：类字段（含闭包环境类 __Env_* 捕获实例成员）随 fld 携带——供闭包读侧重建
            var classFields = classType.Fields.ToArray();
            w.Field("fields:" + classFields.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var field in classFields)
            {
                w.Open("fld");
                w.Field(Str(field.Name));
                w.Field(TypeRef(field.Type));
                w.Field(field.Visibility.ToString().ToLowerInvariant());
                w.Field(BoolWord(field.IsStatic));
                w.Field(BoolWord(field.IsReadonly));
                w.End();
            }
            // 6b：facade 实例类属性声明（getter/setter 访问器为独立 fn `get_X`/`set_X`，读侧 fns 回填后挂接）
            var properties = classType.Properties;
if (properties.Length > 0)
            {
                w.Field("props:" + properties.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var property in properties)
                {
                    w.Open("prop");
                    w.Field(Str(property.Name));
                    w.Field(TypeRef(property.Type));
                    w.Field(BoolWord(property.Getter != null));
                    w.Field(BoolWord(property.Setter != null));
                    w.Field(property.Visibility.ToString().ToLowerInvariant());
                    w.Field(BoolWord(property.IsStatic));
                    w.End();
                }
            }
            // 6e-Step D-b：事件声明（符号多播 + 后备字段 `_<e>` 已在 fields: 携带）——读侧回填 EventSymbol
            var events = classType.Events;
            w.Field("events:" + events.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var eventSymbol in events)
            {
                w.Open("evt");
                w.Field(Str(eventSymbol.Name));
                w.Field(TypeRef(eventSymbol.HandlerType));
                w.Field(eventSymbol.Visibility.ToString().ToLowerInvariant());
                w.End();
            }
            w.End();
        }

        /// <summary>方法签名短键：Name 或 Name[参数类型列表]（重载靠参数类型区分）。</summary>
        private static string MethodSignature(FunctionSymbol method)
        {
            // 6e-M23 R8：仅有 out/ref 的重载键须不同（修饰符入签名）。
            return method.Parameters.Length == 0
                ? method.Name
                : method.Name + "[" + string.Join(",", method.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type))) + "]";
        }

        /// <summary>
        /// 泛型定义类节点（6e-G7 S1）：类型参数（含约束）+ 字段 + 静态方法签名。
        /// 成员类型经 TypeRef 携带开放参数（!属主.名）与实例化 mangle；开放绑定体经 bodies 区按 FnKey 携带（S2）。
        /// </summary>
        private static void EmitGenericClassSymbol(Writer w, Registry registry, NamedTypeSymbol classType)
        {
            System.Console.Error.WriteLine("[G7] gcls " + classType.FullName + " methods=[" +
                string.Join(",", classType.Methods.Select(m => m.Name + (m.IsStatic ? "(s)" : m.IsConstructor ? "(ctor)" : "(i)"))) + "]" +
                " fns=" + string.Join(",", ((IEnumerable<object>)classType.Methods).Count()));
            w.Open("gcls");
            w.Field(classType.FullName);
            w.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 6e-G7/M0-1a：接口位 + 实现接口列表（开放参数经 TypeRef `!属主.名` 编码，如 `List<T>: IEnumerable<!List.T>`）。
            w.Field("iface:" + BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            w.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                w.Field(TypeRef(iface));
            }

            var typeParameters = classType.TypeParameters;
            w.Field("tparams:" + typeParameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var typeParameter in typeParameters)
            {
                WriteTypeParameter(w, typeParameter);
            }

            var fields = classType.Fields.ToArray();
            w.Field("fields:" + fields.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var field in fields)
            {
                w.Open("fld");
                w.Field(Str(field.Name));
                w.Field(TypeRef(field.Type));
                w.Field(field.Visibility.ToString().ToLowerInvariant());
                w.Field(BoolWord(field.IsStatic));
                w.Field(BoolWord(field.IsReadonly));
                w.End();
            }

            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(MethodSignature(method));
            }

            // 6e 跨库里程碑：泛型定义类属性声明（访问器 get_X/set_X 为独立 fn，读侧 fns 回填后挂接）。
            var properties = classType.Properties;
if (properties.Length > 0)
                {
                    w.Field("props:" + properties.Length.ToString(CultureInfo.InvariantCulture));
                    foreach (var property in properties)
                    {
                        w.Open("prop");
                        w.Field(Str(property.Name));
                        w.Field(TypeRef(property.Type));
                        w.Field(BoolWord(property.Getter != null));
                        w.Field(BoolWord(property.Setter != null));
                        w.Field(property.Visibility.ToString().ToLowerInvariant());
                        w.Field(BoolWord(property.IsStatic));
                        w.End();
                    }
                }
            // 6e-Step D-b：泛型定义类事件声明（handler 类型可含开放参数）
            var genericEvents = classType.Events;
            w.Field("events:" + genericEvents.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var eventSymbol in genericEvents)
            {
                w.Open("evt");
                w.Field(Str(eventSymbol.Name));
                w.Field(TypeRef(eventSymbol.HandlerType));
                w.Field(eventSymbol.Visibility.ToString().ToLowerInvariant());
                w.End();
            }

            w.End();
        }

        /// <summary>tpar/ftp 子节点共用写出（6e-G7 S1）：名 / 序号 / 约束标志 / 显式约束类型列表。</summary>
        private static void WriteTypeParameter(Writer w, TypeParameterSymbol typeParameter)
        {
            w.Open("tpar");
            w.Field(typeParameter.Name);
            w.Field(typeParameter.Ordinal);
            var flags = new List<string>();
            if (typeParameter.HasNewConstraint)
            {
                flags.Add("new");
            }

            if (typeParameter.HasReferenceTypeConstraint)
            {
                flags.Add("class");
            }

            if (typeParameter.HasValueTypeConstraint)
            {
                flags.Add("struct");
            }

            w.Field(flags.Count == 0 ? "-" : string.Join("+", flags));
            w.Field("c:" + typeParameter.ConstraintTypes.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                w.Field(TypeRef(constraint));
            }

            w.End();
        }

        /// <summary>约束标志解析（gcls.tpar 与 fn.tps 共用，6e-G7 S1）。</summary>
        private static void ApplyTypeParameterFlags(TypeParameterSymbol parameter, string flagsText)
        {
            if (flagsText == "-")
            {
                return;
            }

            foreach (var flag in flagsText.Split('+'))
            {
                switch (flag)
                {
                    case "new":
                        parameter.HasNewConstraint = true;
                        break;
                    case "class":
                        parameter.HasReferenceTypeConstraint = true;
                        break;
                    case "struct":
                        parameter.HasValueTypeConstraint = true;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown type parameter constraint flag '{flag}'");
                }
            }
        }

        /// <summary>
        /// tpar/ftp 子节点读取（6e-G7 S1）：构造符号 + 应用标志 + 登记开放键（类级限定键 !属主.名；
        /// 方法级裸键 !名）+ 暂存约束数。返回 (参数, 约束数)，约束由第二趟解析。
        /// </summary>
        private static (TypeParameterSymbol Parameter, int ConstraintCount) ReadTypeParameter(Reader reader, ReadContext context, string? ownerFullName)
        {
            reader.Expect("tpar");
            var parameterName = Unescape(reader.ExpectString());
            var ordinal = reader.ExpectInt();
            var flagsText = reader.ExpectString();
            var constraintCount = ReadCountField(reader, "c:");

            var parameter = new TypeParameterSymbol(parameterName, ordinal, owningClass: null);
            ApplyTypeParameterFlags(parameter, flagsText);

            var openKey = ownerFullName == null
                ? "!" + parameterName
                : "!" + ownerFullName + "." + parameterName;
            context.OpenTypeParametersByKey[openKey] = parameter;

            return (parameter, constraintCount);
        }

        /// <summary>约束第二趟：兄弟参数已全部注册后解析显式约束类型。</summary>
        private static void ResolveDeferredConstraints(Reader reader, TypeParameterSymbol parameter, int constraintCount, ReadContext context)
        {
            if (constraintCount == 0)
            {
                reader.End();
                return;
            }

            var constraints = ImmutableArray.CreateBuilder<TypeSymbol>(constraintCount);
            for (var c = 0; c < constraintCount; c++)
            {
                constraints.Add(ResolveTypeRef(reader.ExpectString(), context));
            }

            parameter.ConstraintTypes = constraints.ToImmutable();
            reader.End();
        }

        private static void EmitFunctionSymbol(Writer w, Registry registry, FunctionSymbol fn)
        {
            w.Open("fn");
            w.Field(registry.FnKey(fn));
            w.Field("name:" + Str(fn.Name));

            // 6e-G7 S1：方法级类型参数（顶层泛型函数）——裸键 !名（无属主类）。
            if (fn.TypeParameters.Length > 0)
            {
                w.Field("tps:" + fn.TypeParameters.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var typeParameter in fn.TypeParameters)
                {
                    WriteTypeParameter(w, typeParameter);
                }
            }

            w.Field("ret:" + TypeRef(fn.ReturnType));
            w.Field("ns:" + (fn.Namespace.Length > 0 ? Str(fn.Namespace) : "-"));
            w.Field("owner:" + (fn.ContainingClass != null ? fn.ContainingClass.FullName : "-"));
            w.Field("extern:" + BoolWord(fn.IsExtern));
            w.Field("dll:" + (fn.DllName != null ? Str(fn.DllName) : "-"));
            w.Field("cc:" + fn.CallingConvention.ToString().ToLowerInvariant());
            w.Field("builtin:" + (fn.BuiltinKind != null ? fn.BuiltinKind.Value.ToString() : "-"));
            w.Field("entry:" + (fn.EntryPoint != null ? Str(fn.EntryPoint) : "-"));
            w.Field("charset:" + (fn.CharSet != null ? fn.CharSet.Value.ToString().ToLowerInvariant() : "-"));

            // 6e-G7 S2：属主方法携带静态/构造/访问器位（泛型定义与 6b facade 实例类显式区分；容器类全静态显式 true）。
            if (fn.ContainingClass != null)
            {
                w.Field("static:" + BoolWord(fn.IsStatic));
                w.Field("ctor:" + BoolWord(fn.IsConstructor));
                w.Field("acc:" + BoolWord(fn.IsPropertyAccessor));
            }

            w.Field("params:" + fn.Parameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var p in fn.Parameters)
            {
                w.Open("par");
                w.Field(registry.VarKey(p));
                w.Field(Str(p.Name));
                w.Field(TypeRef(p.Type));
                w.Field(p.Ordinal);
                w.Field(p.IsOut ? "out" : p.IsRef ? "ref" : "-");
                w.Field(p.IsThisParameter ? "this" : "-");
                w.End();
            }
            w.End();
        }

        private static void EmitVariableSymbol(Writer w, Registry registry, VariableSymbol v)
        {
            w.Open(v is GlobalVariableSymbol ? "glb" : "loc");
            w.Field(registry.VarKey(v));
            w.Field(BoolWord(v.IsReadOnly));
            w.Field(TypeRef(v.Type));
            if (v.Constant != null)
            {
                w.Open("const");
                w.Field(EncodeValue(v.Constant.Value));
                w.End();
            }

            w.End();
        }

        // ---------------------------------------------------------------- write: naming

        /// <summary>类型的文本引用：内建/数组用短名（int / int[][]），类/枚举用全名。</summary>
        private static string TypeRef(TypeSymbol type)
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
                return enumType.FullName;
            }

            if (type is NamedTypeSymbol classType)
            {
                return classType.FullName;
            }

            // 6e-M22/M0-1b：函数类型 `fnty{参数,;返回}`（递归 TypeRef；参数逗号分隔、分号接返回、{} 嵌套）。
            // .coa 词法仅以空白与 () 切分，故嵌套用 {} 避开结构括号。
            if (type is FunctionTypeSymbol functionType)
            {
                var builder = new System.Text.StringBuilder();
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

        /// <summary>
        /// 实例化类型的 .coa 编码（6e-G7 S1）：定义全名 + backtick 元数 + # + $ 分隔实参。
        /// 实参递归走 <see cref="TypeRef"/>——开放参数为限定键 !属主.名（区别于 mangle 缓存键的裸 !T），
        /// 保证跨定义无歧义且读侧可独立解析；基元/类用平名（不含 $、` 等，分隔安全）；嵌套实例化递归。
        /// </summary>
        private static string EncodeInstantiatedTypeRef(InstantiatedTypeSymbol instantiated)
        {
            var builder = new System.Text.StringBuilder();
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

        /// <summary>6e-G7 S2：单个 body 条目（FnKey + 语句块）。</summary>
        /// <summary>6e-M26：泛型开放绑定体确定性排序键（GenericOpenBodies 为 ImmutableDictionary，枚举不稳定）。</summary>
        private static string GenericOpenSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }

        private static void WriteBodyEntry(Writer w, Registry registry, Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>> labelsByFunction, FunctionSymbol fn, BoundBlockStatement body)
        {
            registry.CurrentFunctionName = fn.Name + (fn.ContainingClass != null ? " (" + fn.ContainingClass.FullName + ")" : "");
            w.Open("body");
            w.Field(registry.FnKey(fn));
            WriteStatement(w, registry, labelsByFunction[fn], body);
            w.End();
            registry.CurrentFunctionName = null;
        }

    }
}
