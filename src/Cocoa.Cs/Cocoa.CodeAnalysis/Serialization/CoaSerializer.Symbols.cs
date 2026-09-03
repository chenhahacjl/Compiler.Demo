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
    internal static partial class CoaSerializer
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

        /// <summary>6e-M19 M2-c锛氬唴寤哄崟渚嬶紙System.Object/System.Type锛夋寜鍏ㄥ悕搴忓垪鍖栵紝璇讳晶鏄犲皠鍥炲崟渚嬨€?/summary>
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
            // 6e-G7/M0-1a锛氭帴鍙ｄ綅 + 瀹炵幇鎺ュ彛鍒楄〃锛堜緵娑堣垂鏂?IsInterface 鍒ゅ畾涓庢帴鍙ｆ垚鍛樻部 Interfaces 閾捐В鏋愶級
            w.Field("iface:" + BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            w.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                w.Field(TypeRef(iface));
            }
            // 鎼村繐鍨崠鏍у弿闁劑娼ら幀浣规煙濞夋洜顒烽崥宥忕礄6e-M18閿涙艾顔愰崳銊ц閸忎浇顔忕敮锔跨秼闂堟瑦鈧焦鏌熷▔鏇礉婵?Console.WriteLine/Math.Max閿涙硞yscall/extern 娴滐缚璐熼棃娆愨偓渚婄礆閵?
            // 閺傝纭堕張顑跨秼閻㈠崬鎮囬懛?fn 閺夛紕娲伴幖鍝勭敨閿涘潵wner 鐎涙顔岄崶鐐诧綖缁缍婄仦鐑囩礆閿涘矁绻栭柌灞藉灙 Name[閸欏倹鏆熺猾璇茬€穄 娓氭盯妲勭拠浼欑礄閺冪姴寮惇浣烘殣閺傝瀚崣鍑ょ礆閵?
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(MethodSignature(method));
            }
            // 6b锛歠acade 瀹炰緥绫诲睘鎬у０鏄庯紙getter/setter 璁块棶鍣ㄤ负鐙珛 fn `get_X`/`set_X`锛岃渚?fns 鍥炲～鍚庢寕鎺ワ級
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
            w.End();
        }

        /// <summary>鏂规硶绛惧悕鐭敭锛歂ame 鎀Name[鍙傛暟绫诲瀷鍒楄〃]锛堥噸杞介潬鍙傛暟绫诲瀷鍖哄垎锛夈₀/summary>
        private static string MethodSignature(FunctionSymbol method)
        {
            // 6e-M23 R8閿涙矮绮庡?out/ref 閻ㄥ嫰鍣告潪浠嬫暛妞よ绗夐崥宀嬬礄娣囶噣銈扮粭锕€鍙嗙粵鎯ф倳閿?
            return method.Parameters.Length == 0
                ? method.Name
                : method.Name + "[" + string.Join(",", method.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type))) + "]";
        }

        /// <summary>
        /// 娉涘瀷瀹氫箟绫昏妭鐐癸紙6e-G7 S1锛夛細绫诲瀷鍙傛暟锛堝惈绾︽潫锛? 瀛楁 + 闈欐€佹柟娉曠鍚嶃€?
        /// 鎴愬憳绫诲瀷缁?TypeRef 鎼哄甫寮€鏀惧弬鏁帮紙!灞炰富.鍚嶏級涓庡疄渚嬪寲 mangle锛涘紑鏀剧粦瀹氫綋鐢?bodies 鍖烘寜 FnKey 鎼哄甫锛圫2锛夈€?
        /// </summary>
        private static void EmitGenericClassSymbol(Writer w, Registry registry, NamedTypeSymbol classType)
        {
            System.Console.Error.WriteLine("[G7] gcls " + classType.FullName + " methods=[" +
                string.Join(",", classType.Methods.Select(m => m.Name + (m.IsStatic ? "(s)" : m.IsConstructor ? "(ctor)" : "(i)"))) + "]" +
                " fns=" + string.Join(",", ((IEnumerable<object>)classType.Methods).Count()));
            w.Open("gcls");
            w.Field(classType.FullName);
            w.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 6e-G7/M0-1a锛氭帴鍙ｄ綅 + 瀹炵幇鎺ュ彛鍒楄〃锛堝紑鏀惧弬鏁扮粡 TypeRef `!灞炰富.鍚峘 缂栫爜锛屽 `List<T>: IEnumerable<!List.T>`锛?
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

            w.End();
        }

        /// <summary>tpar/ftp 瀛愯妭鐐瑰叡鐢ㄥ啓鍑猴紙6e-G7 S1锛夛細鍚?/ 搴忓彿 / 绾︽潫鏍囧織 / 鏄惧紡绾︽潫绫诲瀷鍒楄〃銆?/summary>
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

        /// <summary>绾︽潫鏍囧織瑙ｆ瀽锛坓cls.tpar 涓?fn.tps 鍏辩敤锛?e-G7 S1锛夈€?/summary>
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
        /// tpar/ftp 瀛愯妭鐐硅鍙栵紙6e-G7 S1锛夛細鏋勯€犵鍙?+ 搴旂敤鏍囧織 + 鐧昏寮€鏀鹃敭锛堢被绾?闄愬畾閿?!灞炰富.鍚嶏紱
        /// 鏂规硶绾?瑁搁敭 !鍚嶏級+ 鏆傚瓨绾︽潫鏁般€傝繑鍥?(鍙傛暟, 绾︽潫鏁?锛岀害鏉熺敱绗簩瓒熻В鏋愩€?
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

        /// <summary>绾︽潫绗簩瓒燂細鍏勫紵鍙傛暟宸插叏閮ㄦ敞鍐屽悗瑙ｆ瀽鏄惧紡绾︽潫绫诲瀷銆?/summary>
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

            // 6e-G7 S1：方法级类型参数（顶层泛型函数）——裸销!名（无属主类＀
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

            // 6e-G7 S2锛氬睘涓绘柟娉曟惡甯﹂潤鎬?鏋勯€?璁块棶鍣ㄤ綅锛堟硾鍨嬪畾涔変笌 6b facade 瀹炰緥绫绘樉寮忓尯鍒嗭紱瀹瑰櫒绫诲叏闈欐€?鏄惧紡 true锛?
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

        /// <summary>缁鐎烽惃鍕瀮閺堫剙绱╅悽顭掔窗閸愬懎缂?閺佹壆绮嶉悽銊х叚閸氬稄绱檌nt / int[][]閿涘绱濈猾?閺嬫矮濡囬悽銊ュ弿閸氬秲鈧?/summary>
        private static string TypeRef(TypeSymbol type)
        {
            // 6e 跨库里程碑：基元内建 → `@` 权威记法（@i32/@string/@bool…，Rust/LLVM 式位宽名）。
            // 引用相等键（单例稳定），先于 NamedTypeSymbol 分支命中，避免输出 C# 短名 int/string。
            if (GenericTypeInstantiator.TryGetPrimitiveName(type, out var primitiveName))
            {
                return primitiveName;
            }
            // 6e-G7 S1锛氬紑鏀剧被鍨嬪弬鏁?鈫?闄愬畾鏉冨▉閿?`!灞炰富鍏ㄥ悕.鍙傛暟鍚峘锛堟柟娉曠骇鏃犲睘涓诲洖钀借８鍚嶏級锛?
            // 瀹炰緥鍖栫被鍨?鈫?Encode v3 瀹屾暣 mangle锛坆acktick 鍏冩暟 + # + $ 鍒嗛殧閫掑綊瀹炲弬锛?
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

            // 6e-M22/M0-1b：函数类垀`fnty{参数,;返回}`（递归 TypeRef；参数逗号分隔、分号接返回、{} 嵌套— 
            // .coa 璇嶆硶浠呬互绌虹櫧涓?() 鍒囧垎锛屾晠宓屽鐢?{} 閬垮紑缁撴瀯鎷彿锛?
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
        /// 瀹炰緥鍖栫被鍨嬬殑 .coa 缂栫爜锛?e-G7 S1锛夛細瀹氫箟鍏ㄥ悕 + backtick 鍏冩暟 + # + $ 鍒嗛殧瀹炲弬銆?
        /// 瀹炲弬閫掑綊璧?<see cref="TypeRef"/>鈥斺€斿紑鏀惧弬鏁颁负闄愬畾閿?!灞炰富.鍚嶏紙鍖哄埆浜?mangle 缂撳瓨閿殑瑁?!T锛夛紝
        /// 淇濊瘉璺ㄥ畾涔夋棤姝т箟涓旇渚у彲鐙珛瑙ｆ瀽锛涘熀鍏?绫荤敤骞冲悕锛堜笉鍚?$銆乣銆?锛屽垎闅斿畨鍏級锛涘祵濂楀疄渚嬪寲閫掑綊銆?
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

        /// <summary>6e-G7 S2锛氬崟鏉?body 鏉＄洰锛團nKey + 璇彞鍧楋級銆?/summary>
        /// <summary>6e-M26锛氭硾鍨嬪紑鏀剧粦瀹氫綋纭畾鎬ф帓搴忛敭锛圙enericOpenBodies 涓?ImmutableDictionary锛屾灇涓句笉绋冲畾锛夈€?/summary>
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
