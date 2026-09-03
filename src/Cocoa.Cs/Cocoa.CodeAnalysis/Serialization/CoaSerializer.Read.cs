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
    /// <summary>
    /// `.coa` 璇箟灞傚簭鍒楀寲鍣細绗﹀彿琛?+ 闄嶇骇 BoundProgram锛堝嚱鏁颁綋锛夋枃鏈?round-trip銆?
    /// 鍙屽悗绔叡鐢紙native 鈫?MirToLir锛孖L 鈫?IlEmitter锛夛紱璇硶鑺傜偣锛圫yntax锛変笉搴忓垪鍖栵紙缃?null锛夈€?
    ///
    /// 鏂囨湰鏍煎紡锛堝彲璇讳紭鍏堬紝绫诲瀷/鍑芥暟/鍙橀噺涓€寰嬫寜鍚嶅瓧寮曠敤锛屼笉鐢ㄦ暟瀛?id锛夛細
    ///   (type)     鍐呭缓/鏁扮粍绫诲瀷鍐呰仈涓哄悕瀛楀紩鐢細int / int[] / int[][]锛涚被/鏋氫妇鐢ㄥ叏鍚?System.Console
    ///   (enum)     (enum MyLib.Color members:3 (Red 0) (Green 1) (Blue 2))
    ///   (systype)  (systype System.Object)鈥斺€斿唴寤哄崟渚嬫寜鍏ㄥ悕鏄犲皠
    ///   (cls)      (cls System.Console public methods:2 WriteLine[string] ReadKey)鈥斺€旀柟娉曞垪 Name[鍙傛暟绫诲瀷] 绛惧悕
    ///   (fn)       (fn MyLib.Add(i32,i32) name:Add ret:i32 ns:MyLib owner:- extern:false ...
    ///               params:2 (par MyLib.Add/a a i32 0) ...)
    ///              鍑芥暟閿?= [鍛藉悕绌洪棿鎴栧涓荤被.]鍑芥暟鍚?鍙傛暟绫诲瀷鍒楄〃)锛岄噸杞介潬鍙傛暟绫诲瀷鍖哄垎
    ///   (glb/loc)  (glb global:version true i32 (const i:1)) / (loc MyLib.Factorial/result false i32)
    ///              鍙橀噺閿細鍏ㄥ眬 global:鍚嶅瓧锛涘眬閮?鍙傛暟 鍑芥暟閿?鍚嶅瓧锛堝悓鍚嶅啿绐佸姞 #2銆?3 鍚庣紑锛?
    ///   杩愮畻绗?     鏂囨湰璁板彿 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   甯冨皵/鏋氫妇璇? true false锛沺ublic internal protected private锛泈inapi cdecl stdcall锛泆nicode ansi auto
    /// </summary>
    internal static partial class CoaSerializer
    {
        /// <summary>读 `.coa` 文本（兼容入口：无库名/无 external，跨库符号解析留空）。</summary>
        public static CoaProgram Read(string text)
        {
            return Read(text, moduleName: "", external: ImmutableArray<CoaProgram>.Empty);
        }

        /// <summary>
        /// 读 `.coa` 文本。`moduleName` 为库名（读入符号的 ContainingLibrary 回填，FnKey 库前缀来源）；
        /// `external` 为已加载的依赖库（System.Core 先行），供跨库符号合并解析（复用实例，非复制）。
        /// </summary>
        public static CoaProgram Read(string text, string moduleName, ImmutableArray<CoaProgram> external)
        {
            // 瀹屾暣鎬ф牎楠屽墠缃細缂哄け鎴栦笉鍖归厤鍗虫嫆杞斤紙闃茶鏀?鎹熷潖锛涜搫鎰忎吉閫犻渶绛惧悕鏈哄埗锛屼笉鍦?v1 鑼冨洿锛?
            var marker = "(checksum " + ChecksumTag;
            var markerIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                throw new InvalidDataException(".coa checksum missing (expected '(checksum sha256:<hex>)' as the last line); rebuild the library");
            }

            var payload = text.Substring(0, markerIndex);
            var provided = text.Substring(markerIndex + marker.Length).TrimEnd();
            if (!provided.EndsWith(")"))
            {
                throw new InvalidDataException(".coa checksum malformed (expected '(checksum sha256:<hex>)' as the last line)");
            }

            provided = provided.Substring(0, provided.Length - 1);
            var actual = ComputeChecksum(payload);
            if (!string.Equals(provided, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($".coa checksum mismatch: library corrupted or modified (expected {actual}, got {provided})");
            }

            var tokens = Tokenize(payload).ToArray();
            var reader = new Reader(tokens);
            reader.Expect("cod");

            var magic = reader.ExpectString();
            if (magic != Magic)
            {
                throw new InvalidDataException($"invalid .coa magic '{magic}'");
            }

            var version = reader.ExpectInt();
            if (version != Version)
            {
                throw new InvalidDataException($".coa version {version} is not supported (expected {Version}); rebuild the library");
            }

            var context = new ReadContext(moduleName, external);
            var bodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var requires = CoaRequirement.Any;
            var platforms = ImmutableArray.CreateBuilder<string>();
            var dotnetRefs = ImmutableArray.CreateBuilder<string>();
            var codRefs = ImmutableArray.CreateBuilder<string>();
            var imports = ImmutableArray.CreateBuilder<string>();
            var namespaces = ImmutableArray.CreateBuilder<string>();

            while (reader.TryExpect(out var child))
            {
                switch (child)
                {
                    case "symbols":
                        ReadSymbols(reader, context);
                        ApplyPendingProperties(context);
                        break;
                    case "bodies":
                        ReadBodies(reader, context, bodies);
                        break;
                    case "manifest":
                        while (reader.TryExpect(out var item))
                        {
                            switch (item)
                            {
                                case "requires":
                                    requires = ParseRequirement(reader.ExpectString());
                                    break;
                                case "platform":
                                    platforms.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "refdll":
                                    dotnetRefs.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "refcod":
                                    codRefs.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "import":
                                    imports.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "ns":
                                    namespaces.Add(Unescape(reader.ExpectString()));
                                    break;
                            }

                            reader.End();
                        }

                        reader.End();
                        break;
                }
            }

            // 6e 跨库里程碑：库名回填——优先取传入 moduleName；兼容入口（空）从本库 fn 键前缀恢复
            // （保证 read→write round-trip 稳定，重写时 RegisterFunction 跨库过滤不误伤本库函数）。
            var programName = moduleName.Length > 0
                ? moduleName
                : RecoverLibraryFromKeys(context.LocalFunctionKeys.Keys);

            var program = new CoaProgram(
                context.Functions.ToImmutable(),
                context.Globals.ToImmutable(),
                context.Enums.ToImmutable(),
                context.Classes.ToImmutable(),
                bodies.ToImmutable(),
                requires,
                platforms.ToImmutable(),
                dotnetRefs.ToImmutable(),
                imports.ToImmutable(),
                codRefs.ToImmutable(),
                namespaces.ToImmutable(),
                context.GenericDefinitions.ToImmutable(),
                functionKeys: context.LocalFunctionKeys.ToImmutableDictionary(),
                typesByName: context.LocalTypesByName.ToImmutableDictionary())
            {
                Name = programName,
            };

            return program;
        }

        /// <summary>6e 跨库里程碑：从本库 fn 键集合恢复库名（首键前缀 `库名!`；无则空）。</summary>
        private static string RecoverLibraryFromKeys(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                var bangIndex = key.IndexOf('!');
                if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
                {
                    return key.Substring(0, bangIndex);
                }
            }

            return "";
        }

        /// <summary>璇讳晶鍏变韩鐘舵€侊細鎸夊悕瀛?閿储寮曠殑绗﹀彿琛?+ 绋嬪簭闆嗙鍙锋竻鍗曘€?/summary>
        private sealed class ReadContext
        {
            /// <summary>当前库名（读入符号的 ContainingLibrary 回填；FnKey 库前缀）。6e 跨库里程碑。</summary>
            public string ModuleName { get; }

            /// <summary>外部队列：已加载的依赖库（System.Core 先行），符号经 FunctionKeys/TypesByName 合并复用实例。</summary>
            public ImmutableArray<CoaProgram> ExternalLibraries { get; }

            public ReadContext(string moduleName, ImmutableArray<CoaProgram> external)
            {
                ModuleName = moduleName;
                ExternalLibraries = external;

                // 6e 跨库里程碑：预播种 external 库的符号表（复用实例，非复制）——
                // FunctionsByKey（键含库前缀）/TypesByName（全名）。本地注册（indexer 赋值）优先。
                foreach (var library in external)
                {
                    foreach (var pair in library.FunctionKeys)
                    {
                        if (!FunctionsByKey.ContainsKey(pair.Key))
                        {
                            FunctionsByKey[pair.Key] = pair.Value;
                        }
                    }

                    foreach (var pair in library.TypesByName)
                    {
                        if (!TypesByName.ContainsKey(pair.Key))
                        {
                            TypesByName[pair.Key] = pair.Value;
                        }
                    }
                }
            }

            /// <summary>绫?鏋氫妇鍏ㄥ悕 鈫?绫诲瀷绗﹀彿锛堝唴寤虹被鍨嬩笉缁忔琛紝鐩存帴瑙ｆ瀽锛夈€?/summary>
            public Dictionary<string, TypeSymbol> TypesByName { get; } = new(StringComparer.Ordinal);

            /// <summary>6e 跨库里程碑：本库自持类型表（全名 → 符号）——CoaProgram.TypesByName 导出源，
            /// 供其他库读侧 external 合并。与 TypesByName 的区别：不含预播种的 external 符号。</summary>
            public Dictionary<string, TypeSymbol> LocalTypesByName { get; } = new(StringComparer.Ordinal);

            /// <summary>6e-G7 S1：开放类型参数限定键（!属主全名.参数名）→ 符号。文件级平铺——限定键天然无碰撞。</summary>
            public Dictionary<string, TypeParameterSymbol> OpenTypeParametersByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>鍑芥暟閿?鈫?鍑芥暟绗﹀彿銆?/summary>
            public Dictionary<string, FunctionSymbol> FunctionsByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>6e 跨库里程碑：本库自持函数键（含库前缀）→ 符号——CoaProgram.FunctionKeys 导出源。</summary>
            public Dictionary<string, FunctionSymbol> LocalFunctionKeys { get; } = new(StringComparer.Ordinal);

            /// <summary>鍙橀噺閿?鈫?鍙橀噺/鍙傛暟绗﹀彿銆?/summary>
            public Dictionary<string, VariableSymbol> VariablesByKey { get; } = new(StringComparer.Ordinal);

            public ImmutableArray<FunctionSymbol>.Builder Functions { get; } = ImmutableArray.CreateBuilder<FunctionSymbol>();

            public ImmutableArray<GlobalVariableSymbol>.Builder Globals { get; } = ImmutableArray.CreateBuilder<GlobalVariableSymbol>();

            public ImmutableArray<NamedTypeSymbol>.Builder Enums { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

            public ImmutableArray<NamedTypeSymbol>.Builder Classes { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

            /// <summary>6e-G7 S1：泛型定义类（gcls 读入）。</summary>
            public ImmutableArray<NamedTypeSymbol>.Builder GenericDefinitions { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

            /// <summary>6b：facade 类属性待挂接声明（访问器 fns 读毕后重建 PropertySymbol）。</summary>
            public List<(NamedTypeSymbol ClassType, string Name, TypeSymbol Type, bool HasGet, bool HasSet, Visibility Visibility, bool IsStatic)> PendingProperties { get; } = new();

            public void AddNamedType(string fullName, TypeSymbol type)
            {
                TypesByName[fullName] = type;
                LocalTypesByName[fullName] = type;
            }
        }

        private static void ReadSymbols(Reader reader, ReadContext context)
        {
            while (reader.TryExpect(out var kind))
            {
                switch (kind)
                {
                    case "enum":
                        ReadEnum(reader, context);
                        break;
                    case "systype":
                        ReadSystemType(reader, context);
                        break;
                    case "cls":
                        ReadClass(reader, context);
                        break;
                    case "gcls":
                        ReadGenericClass(reader, context);
                        break;
                    case "fn":
                        ReadFunction(reader, context);
                        break;
                    case "glb":
                        ReadVariable(reader, context, isGlobal: true);
                        break;
                    case "loc":
                        ReadVariable(reader, context, isGlobal: false);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown symbol kind '{kind}'");
                }
            }

            reader.End();
        }

        /// <summary>6b：facade 类属性回填——访问器 fns（`get_X`/`set_X`，静态 + this 参）已读入类方法，据名挂接重建 PropertySymbol。</summary>
        private static void ApplyPendingProperties(ReadContext context)
        {
            foreach (var (classType, name, type, hasGet, hasSet, visibility, isStatic) in context.PendingProperties)
            {
                FunctionSymbol? getter = hasGet ? classType.GetDeclaredMethod("get_" + name) : null;
                FunctionSymbol? setter = hasSet ? classType.GetDeclaredMethod("set_" + name) : null;
                // 6e 跨库里程碑：索引器属性（绑定侧统一命名 `Item`）重建时须带 isIndexer 位，
                // 否则实例化类型 GetIndexer() 命不中 → 元素访问回落数组判定报错。
                classType.AddProperty(new PropertySymbol(name, type, classType, getter, setter, visibility, isStatic, isIndexer: name == "Item"));
            }

            context.PendingProperties.Clear();
        }

        private static void ReadEnum(Reader reader, ReadContext context)
        {
            var fullName = reader.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var count = ReadCountField(reader, "members:");
            var members = new Dictionary<string, int>();
            for (var i = 0; i < count; i++)
            {
                var memberName = reader.ExpectKind();
                var value = reader.ExpectInt();
                members[Unescape(memberName)] = value;
                reader.End();
            }

            var enumType = new NamedTypeSymbol(name, ns, Visibility.Public, declaration: null)
            {
                TypeKind = TypeKind.Enum,
                IsSealed = true,
            };
            enumType.SetEnumMembers(members);
            context.Enums.Add(enumType);
            context.AddNamedType(fullName, enumType);
            reader.End();
        }

        private static void ReadSystemType(Reader reader, ReadContext context)
        {
            // 6e-M19 M2-c锛氬唴寤哄崟渚嬫寜鍏ㄥ悕鏄犲皠锛堟垚鍛橀潰宸茬敱 Ensure 鍐呭缓娉ㄥ叆锛?
            var fullName = reader.ExpectString();
            var singleton = fullName switch
            {
                "System.Object" => NamedTypeSymbol.SystemObject,
                "System.Type" => NamedTypeSymbol.SystemType,
                _ => throw new InvalidDataException($"Unknown builtin system class '{fullName}'"),
            };
            context.Classes.Add(singleton);
            context.AddNamedType(fullName, singleton);
            reader.End();
        }

        private static void ReadClass(Reader reader, ReadContext context)
        {
            var fullName = reader.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var visibilityText = reader.ExpectString();
            if (!Enum.TryParse<Visibility>(visibilityText, ignoreCase: true, out var visibility))
            {
                throw new InvalidDataException($"Unknown visibility '{visibilityText}' on class '{fullName}'");
            }

            // 6e-G7/M0-1a：接口位 + 实现接口列表（向后兼容：旧版 .coa 无 iface 字段 → 默认非接口、无实现）
            var isInterface = false;
            var interfaceRefs = new string[0];
            if (reader.PeekRaw().StartsWith("iface:", StringComparison.Ordinal))
            {
                isInterface = ParseBoolWord(ReadLabeledField(reader, "iface:"));
                var ifaceCount = ReadCountField(reader, "ifaces:");
                interfaceRefs = new string[ifaceCount];
                for (var i = 0; i < ifaceCount; i++)
                {
                    interfaceRefs[i] = reader.ExpectString();
                }
            }

            var methodCount = ReadCountField(reader, "methods:");
            // 鏂规硶鍚嶄粎渚涢槄璇伙紝鏂规硶绗﹀彿鐢卞悇 fn 鏉＄洰鐨?owner 瀛楁鍥炲～
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectString();
            }

            var classType = new NamedTypeSymbol(name, ns, visibility, declaration: null);
            // 6e-M19 M2-c锛?cod 绫婚粯璁ょ户鎵?System.Object锛堜笌婧愮爜缁戝畾涓€鑷达紱.coa v1 涓嶅簭鍒楀寲鎺ュ彛澹版槑锛?
            classType.BaseType = NamedTypeSymbol.SystemObject;
            // 6e-G7/M0-1a：接口位回填 + 实现接口列表回填
            if (isInterface)
            {
                classType.TypeKind = TypeKind.Interface;
            }

            foreach (var interfaceRef in interfaceRefs)
            {
                classType.AddInterface((NamedTypeSymbol)ResolveTypeRef(interfaceRef, context));
            }

            context.Classes.Add(classType);
            context.GenericDefinitions.Add(classType);
            context.AddNamedType(fullName, classType);

            // 6b：facade 类属性声明解析（访问器 `get_X`/`set_X` 为独立 fn，读毕后回填挂接）
            if (reader.PeekRaw().StartsWith("props:", StringComparison.Ordinal))
            {
                var propertyCount = ReadCountField(reader, "props:");
                for (var i = 0; i < propertyCount; i++)
                {
                    reader.Expect("prop");
                    var propertyName = Unescape(reader.ExpectString());
                    var propertyType = ResolveTypeRef(reader.ExpectString(), context);
                    var hasGet = ParseBoolWord(reader.ExpectString());
                    var hasSet = ParseBoolWord(reader.ExpectString());
                    if (!Enum.TryParse<Visibility>(reader.ExpectString(), ignoreCase: true, out var propertyVisibility))
                    {
                        propertyVisibility = Visibility.Public;
                    }

                    var isStatic = ParseBoolWord(reader.ExpectString());
                    context.PendingProperties.Add((classType, propertyName, propertyType, hasGet, hasSet, propertyVisibility, isStatic));
                    reader.End();
                }
            }

            reader.End();
        }

        /// <summary>
        /// 泛型定义类读取（6e-G7 S1）：重建 IsGenericDefinition 壳 + 类型参数（含约束，两趟——约束可引用兄弟参数）+
        /// 字段；静态方法签名仅作清单，符号由各自 fn 条目 owner 回填。
        /// 开放类型参数按限定键 `!属主全名.名` 注册进文件级表，后续 fn/bodies 的类型引用据此解析。
        /// </summary>
        private static void ReadGenericClass(Reader reader, ReadContext context)
        {
            var fullName = reader.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var visibilityText = reader.ExpectString();
            if (!Enum.TryParse<Visibility>(visibilityText, ignoreCase: true, out var visibility))
            {
                throw new InvalidDataException($"Unknown visibility '{visibilityText}' on generic class '{fullName}'");
            }

            // 6e-G7/M0-1a：接口位 + 实现接口列表（开放参数引用须待 tpar 注册后解析，见本方法尾部；旧版 .coa 缺字段则默认）
            var isInterface = false;
            var interfaceRefs = new string[0];
            if (reader.PeekRaw().StartsWith("iface:", StringComparison.Ordinal))
            {
                isInterface = ParseBoolWord(ReadLabeledField(reader, "iface:"));
                var ifaceCount = ReadCountField(reader, "ifaces:");
                interfaceRefs = new string[ifaceCount];
                for (var i = 0; i < ifaceCount; i++)
                {
                    interfaceRefs[i] = reader.ExpectString();
                }
            }

            var typeParameterCount = ReadCountField(reader, "tparams:");
            var classType = new NamedTypeSymbol(name, ns, visibility, declaration: null);
            classType.BaseType = NamedTypeSymbol.SystemObject;

            var pendingConstraints = new (TypeParameterSymbol Parameter, string[] ConstraintRefs)[typeParameterCount];
            for (var i = 0; i < typeParameterCount; i++)
            {
                reader.Expect("tpar");
                var parameterName = Unescape(reader.ExpectString());
                var ordinal = reader.ExpectInt();
                var flagsText = reader.ExpectString();
                var constraintCount = ReadCountField(reader, "c:");

                var parameter = new TypeParameterSymbol(parameterName, ordinal, classType);
                if (flagsText != "-")
                {
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
                                throw new InvalidDataException($"Unknown type parameter constraint flag '{flag}' on '{fullName}.{parameterName}'");
                        }
                    }
                }

                classType.TypeParameters = classType.TypeParameters.Add(parameter);
                context.OpenTypeParametersByKey["!" + fullName + "." + parameterName] = parameter;

                // 约束原文引用在 pass1 读入以推进到下一 tpar（多 tpar 须消费本 tpar 尾部）；
                // pass2 待兄弟参数注册后再 ResolveTypeRef 解析（!限定键可解析）。
                var constraintRefs = new string[constraintCount];
                for (var c = 0; c < constraintCount; c++)
                {
                    constraintRefs[c] = reader.ExpectString();
                }

                reader.End();
                pendingConstraints[i] = (parameter, constraintRefs);
            }

            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
            for (var i = 0; i < typeParameterCount; i++)
            {
                var (parameter, constraintRefs) = pendingConstraints[i];
                if (constraintRefs.Length > 0)
                {
                    parameter.ConstraintTypes = constraintRefs.Select(r => ResolveTypeRef(r, context)).ToImmutableArray();
                }
            }

            var fieldCount = ReadCountField(reader, "fields:");
            for (var i = 0; i < fieldCount; i++)
            {
                reader.Expect("fld");
                var fieldName = Unescape(reader.ExpectString());
                var fieldType = ResolveTypeRef(reader.ExpectString(), context);
                var fieldVisibilityText = reader.ExpectString();
                if (!Enum.TryParse<Visibility>(fieldVisibilityText, ignoreCase: true, out var fieldVisibility))
                {
                    throw new InvalidDataException($"Unknown visibility '{fieldVisibilityText}' on field '{fullName}.{fieldName}'");
                }

                var isStatic = ParseBoolWord(reader.ExpectString());
                var isReadonly = ParseBoolWord(reader.ExpectString());
                classType.AddField(new FieldSymbol(fieldName, fieldType, fieldVisibility, classType, isReadonly, isStatic));
                reader.End();
            }

            var methodCount = ReadCountField(reader, "methods:");
            // 方法名仅供阅读，方法符号由各自 fn 条目的 owner 字段回填
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectString();
            }

            // 6e-G7/M0-1a：接口位回填 + 实现接口列表回填（tpar 已注册，开放参数引用可解）
            if (isInterface)
            {
                classType.TypeKind = TypeKind.Interface;
            }

            foreach (var interfaceRef in interfaceRefs)
            {
                classType.AddInterface((NamedTypeSymbol)ResolveTypeRef(interfaceRef, context));
            }

            // 6e 跨库里程碑：gcls 一律只入 GenericDefinitions，不入 Classes——否则 CoaLibraryCompiler 生成
            // Managed dll 时把开放类型参数类当普通类发射（IL Unexpected type K）。类型注入经 GenericDefinitions。
            context.GenericDefinitions.Add(classType);
            context.AddNamedType(fullName, classType);

            // 6e 跨库里程碑：泛型定义类属性声明解析（访问器 `get_X`/`set_X` 为独立 fn，读毕后回填挂接）。
            if (reader.PeekRaw().StartsWith("props:", StringComparison.Ordinal))
            {
                var propertyCount = ReadCountField(reader, "props:");
                for (var i = 0; i < propertyCount; i++)
                {
                    reader.Expect("prop");
                    var propertyName = Unescape(reader.ExpectString());
                    var propertyType = ResolveTypeRef(reader.ExpectString(), context);
                    var hasGet = ParseBoolWord(reader.ExpectString());
                    var hasSet = ParseBoolWord(reader.ExpectString());
                    if (!Enum.TryParse<Visibility>(reader.ExpectString(), ignoreCase: true, out var propertyVisibility))
                    {
                        propertyVisibility = Visibility.Public;
                    }

                    var isStatic = ParseBoolWord(reader.ExpectString());
                    context.PendingProperties.Add((classType, propertyName, propertyType, hasGet, hasSet, propertyVisibility, isStatic));
                    reader.End();
                }
            }

            reader.End();
        }

        private static void ReadFunction(Reader reader, ReadContext context)
        {
            var key = reader.ExpectString();
            var name = ReadLabeledField(reader, "name:");

            // 6e-G7 S1：方法级类型参数（顶层泛型函数，裸键 !名）——先注册再解析 ret/par 的类型引用
            var typeParameters = ImmutableArray<TypeParameterSymbol>.Empty;
            if (reader.PeekRaw().StartsWith("tps:", StringComparison.Ordinal))
            {
                var tpsHeader = reader.ExpectString();
                if (!int.TryParse(tpsHeader.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tpsCount))
                {
                    throw new InvalidDataException($"Malformed 'tps:' count '{tpsHeader}' on function '{name}'");
                }

                var builder = ImmutableArray.CreateBuilder<TypeParameterSymbol>(tpsCount);
                var deferred = new List<(TypeParameterSymbol Parameter, int ConstraintCount)>(tpsCount);
                for (var i = 0; i < tpsCount; i++)
                {
                    var (parameter, constraintCount) = ReadTypeParameter(reader, context, ownerFullName: null);
                    builder.Add(parameter);
                    deferred.Add((parameter, constraintCount));
                }

                foreach (var (parameter, constraintCount) in deferred)
                {
                    ResolveDeferredConstraints(reader, parameter, constraintCount, context);
                }

                typeParameters = builder.ToImmutable();
            }

            var returnType = ResolveTypeRef(ReadLabeledField(reader, "ret:"), context);
            var nsText = ReadLabeledField(reader, "ns:");
            var ownerText = ReadLabeledField(reader, "owner:");
            var isExtern = ParseBoolWord(ReadLabeledField(reader, "extern:"));
            var dllText = ReadLabeledField(reader, "dll:");
            var ccText = ReadLabeledField(reader, "cc:");
            var builtinText = ReadLabeledField(reader, "builtin:");
            var entryText = ReadLabeledField(reader, "entry:");
            var charSetText = ReadLabeledField(reader, "charset:");

            // 6e-G7 S2：属主方法的显式静态/构造/访问器位（旧文件无此字段，按默认：容器类全静态推断）
            bool? explicitIsStatic = null;
            var explicitIsConstructor = false;
            var explicitIsAccessor = false;
            if (reader.PeekRaw().StartsWith("static:", StringComparison.Ordinal))
            {
                explicitIsStatic = ParseBoolWord(ReadLabeledField(reader, "static:"));
                explicitIsConstructor = ParseBoolWord(ReadLabeledField(reader, "ctor:"));
                explicitIsAccessor = ParseBoolWord(ReadLabeledField(reader, "acc:"));
            }


            var ns = nsText == "-" ? "" : nsText;
            var dllName = dllText == "-" ? null : dllText;
            var entryPoint = entryText == "-" ? null : entryText;
            var builtinKind = builtinText == "-" ? (BuiltinKind?)null : BuiltinFunctions.GetByKindName(builtinText) ?? SystemObjectMembers.GetByKindName(builtinText);
            if (builtinKind == null && builtinText != "-")
            {
                throw new InvalidDataException($"Unknown builtin kind '{builtinText}' on function '{key}'");
            }

            CharSet? charSet;
            if (charSetText == "-")
            {
                charSet = null;
            }
            else if (Enum.TryParse<CharSet>(charSetText, ignoreCase: true, out var parsedCharSet))
            {
                charSet = parsedCharSet;
            }
            else
            {
                throw new InvalidDataException($"Unknown charset '{charSetText}' on function '{key}'");
            }

            CallingConvention callingConvention;
            if (Enum.TryParse<CallingConvention>(ccText, ignoreCase: true, out var parsedCc))
            {
                callingConvention = parsedCc;
            }
            else
            {
                throw new InvalidDataException($"Unknown calling convention '{ccText}' on function '{key}'");
            }

            var containingClass = ownerText == "-" ? null : ResolveOwnerClass(ownerText, context);

            var paramCount = ReadCountField(reader, "params:");
            var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
            for (var i = 0; i < paramCount; i++)
            {
                reader.Expect("par");
                var pKey = reader.ExpectString();
                var pName = Unescape(reader.ExpectString());
                var pType = ResolveTypeRef(reader.ExpectString(), context);
                var ordinal = reader.ExpectInt();

                // 6e-M23 R8锛氱 5 涓?token = out/ref/-锛堟棫鏂囦欢鏃犳 token锛屾寜 "-" 鍏煎锛?
                var isOut = false;
                var isRef = false;
                var modifierText = reader.PeekRaw();
                if (modifierText is "out" or "ref" or "-")
                {
                    reader.ExpectString();
                    isOut = modifierText == "out";
                    isRef = modifierText == "ref";
                }

                var isThis = false;
                var thisText = reader.PeekRaw();
                if (thisText is "this" or "-")
                {
                    reader.ExpectString();
                    isThis = thisText == "this";
                }

                var parameter = new ParameterSymbol(pName, pType, ordinal, isOut, isRef, isThis);
                parameters.Add(parameter);
                context.VariablesByKey[pKey] = parameter;
                reader.End();
            }

            // 6e-M19 M2-c锛歄bject 鍐呭缓鏂规硶澶嶇敤鍗曚緥锛堜繚鎸佺鍙峰悓涓€鎬э紝鍙戝皠鍣ㄦ寜 BuiltinKind 鍒嗗彂锛?
            if (containingClass != null && builtinKind != null && SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                var singleton = SystemObjectMembers.GetByKind(builtinKind.Value);
                if (singleton != null)
                {
                    context.Functions.Add(singleton);
                    context.FunctionsByKey[key] = singleton;
                    context.LocalFunctionKeys[key] = singleton;
                    reader.End();
                    return;
                }
            }

            // 鍚被褰掑睘鎴栧唴缃绫伙細涓嶅鐢ㄥ叏灞€鍗曚緥锛堝唴缃崟渚嬫棤绫诲綊灞烇級锛岄噸寤哄甫涓婁笅鏂囩鍙?
            FunctionSymbol function;
            if (containingClass != null || builtinKind != null)
            {
                function = new FunctionSymbol(
                    name,
                    parameters.ToImmutable(),
                    returnType,
                    isExtern: isExtern,
                    dllName: dllName,
                    callingConvention: callingConvention,
                    containingClass: containingClass,
                    builtinKind: builtinKind,
                    @namespace: ns,
                    entryPoint: entryPoint,
                    charSet: charSet);
            }
            else
            {
                function = BuiltinFunctions.GetByName(name) ?? new FunctionSymbol(
                    name,
                    parameters.ToImmutable(),
                    returnType,
                    isExtern: isExtern,
                    dllName: dllName,
                    callingConvention: callingConvention,
                    @namespace: ns,
                    entryPoint: entryPoint,
                    charSet: charSet);
            }

            // 6e 跨库里程碑：库名回填（优先从 fn 键前缀提取——兼容入口无 moduleName 时仍能恢复库名，
            // 保证 round-trip 稳定；回退 context.ModuleName）。
            function.ContainingLibrary = ExtractLibraryFromKey(key, context);

            context.Functions.Add(function);
            context.FunctionsByKey[key] = function;
            context.LocalFunctionKeys[key] = function;

            // 6e-G7 S1：方法级类型参数回填（顶层泛型函数）
            if (typeParameters.Length > 0)
            {
                function.TypeParameters = typeParameters;
            }

            // 绫绘柟娉曞洖濉細鍚被褰掑睘鐨?fn 褰掑叆鍏剁被锛?e-M18锛氬鍣ㄧ被鍏ㄩ潤鎬佲€斺€攕yscall/extern 鍙婂甫浣撻潤鎬佹柟娉曪級銆?
            // 鍐呭缓鍗曚緥锛圫ystem.Object/System.Type锛孧2-c锛夋垚鍛樺凡鐢?Ensure 娉ㄥ叆锛岃烦杩囧洖濉槻閲嶅/闃茶鏍?static
            // 6e-G7 S2 + 6b：属主方法按显式位还原（泛型定义/facade 实例类显式区分；容器类隐含全静态）
            if (containingClass != null && !SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                function.IsStatic = explicitIsStatic ?? true;
                if (explicitIsStatic.HasValue)
                {
                    function.IsConstructor = explicitIsConstructor;
                    function.IsPropertyAccessor = explicitIsAccessor;
                }

                containingClass.AddMethod(function);
            }

            reader.End();
        }

        private static void ReadVariable(Reader reader, ReadContext context, bool isGlobal)
        {
            var key = reader.ExpectString();
            var isReadOnly = ParseBoolWord(reader.ExpectString());
            var type = ResolveTypeRef(reader.ExpectString(), context);
            BoundConstant? constant = null;

            if (reader.PeekRaw() == "(")
            {
                reader.Expect("const");
                var encoded = reader.ExpectString();
                var value = DecodeValue(encoded);
                constant = new BoundConstant(value);
                reader.End();
            }

            var name = KeyToName(key);
            VariableSymbol variable = isGlobal
                ? new GlobalVariableSymbol(name, isReadOnly, type, constant)
                : new LocalVariableSymbol(name, isReadOnly, type, constant);

            if (isGlobal)
            {
                context.Globals.Add((GlobalVariableSymbol)variable);
            }

            context.VariablesByKey[key] = variable;
            reader.End();
        }

        private static void ReadBodies(Reader reader, ReadContext context, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder bodies)
        {
            while (reader.TryExpect(out var kind) && kind == "body")
            {
                var fnKey = reader.ExpectString();
                if (!context.FunctionsByKey.TryGetValue(fnKey, out var function))
                {
                    throw new InvalidDataException($"Unknown function '{fnKey}' in bodies");
                }

                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                var body = (BoundBlockStatement)ReadStatement(reader, context, labels);

                // extern 鍑芥暟鏃犲疄鐜帮細绌?body锛堜笌 Binder.BindProgram 涓€鑷达級
                if (function.IsExtern)
                {
                    body = new BoundBlockStatement(null, ImmutableArray<BoundStatement>.Empty);
                }

                bodies[function] = body;
                reader.End();
            }

            reader.End();
        }

        // ---------------------------------------------------------------- read: resolution helpers

        /// <summary>娴?`.coa` 閺傚洣娆㈤崝鐘烘祰缁嬪绨梿鍡愨偓?/summary>
        /// <summary>Load `.coa` 文件。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
        public static CoaProgram Load(string path, ImmutableArray<CoaProgram>? external = null)
        {
            var moduleName = Path.GetFileNameWithoutExtension(path);
            return Read(File.ReadAllText(path), moduleName, external ?? ImmutableArray<CoaProgram>.Empty);
        }
    }
}
