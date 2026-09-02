using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Coa
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

        private static TypeSymbol ResolveTypeRef(string reference, ReadContext context)
        {
            // 6e-M22/M0-1b：函数类型 `fnty{...}`（递归解析，{} 内参数/返回可能再含 fnty）
            if (reference.StartsWith("fnty{", StringComparison.Ordinal))
            {
                return ParseFunctionTypeRef(reference, context);
            }

            var baseName = reference;
            var dims = 0;
            while (baseName.EndsWith("[]", StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - 2);
                dims++;
            }

            var core = ResolveNamedType(baseName, context);
            for (var i = 0; i < dims; i++)
            {
                core = TypeSymbol.ArrayOf(core);
            }

            return core;
        }

        /// <summary>6e-M22/M0-1b：解析 `fnty{参数,;返回}`（参数/返回递归 ResolveTypeRef，{} 深度感知）。</summary>
        private static TypeSymbol ParseFunctionTypeRef(string reference, ReadContext context)
        {
            var position = "fnty{".Length;
            var parameterTypes = ImmutableArray.CreateBuilder<TypeSymbol>();

            while (true)
            {
                var (part, next) = ReadUntilTopLevel(reference, position, ',', ';');
                parameterTypes.Add(ResolveTypeRef(part, context));
                position = next;

                if (position >= reference.Length || (reference[position] != ',' && reference[position] != ';'))
                {
                    throw new InvalidDataException($"Malformed function type ref '{reference}'");
                }

                if (reference[position] == ';')
                {
                    position++;
                    break;
                }

                position++; // 跳过 ','
            }

            var (returnPart, end) = ReadUntilTopLevel(reference, position, '}', '}');
            if (end >= reference.Length || reference[end] != '}')
            {
                throw new InvalidDataException($"Malformed function type ref '{reference}'");
            }

            var returnType = ResolveTypeRef(returnPart, context);
            return FunctionTypeSymbol.Get(parameterTypes.ToImmutable(), returnType);
        }

        /// <summary>从 position 读到深度 0 处 stop1/stop2 之一（或外层 `}`），返回 (子串, 停止位置)。</summary>
        private static (string Part, int Next) ReadUntilTopLevel(string text, int position, char stop1, char stop2)
        {
            var start = position;
            var depth = 0;

            while (position < text.Length)
            {
                var c = text[position];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }
                else if (depth == 0 && (c == stop1 || c == stop2))
                {
                    break;
                }

                position++;
            }

            return (text.Substring(start, position - start), position);
        }

        private static TypeSymbol ResolveNamedType(string name, ReadContext context)
        {
            if (context.TypesByName.TryGetValue(name, out var known))
            {
                return known;
            }

            // 6e-G7 S1：开放类型参数限定键（!属主.名）或基元权威编码（!System.Int32 等，实例化实参位置出现）
            if (name.StartsWith("!", StringComparison.Ordinal))
            {
                if (context.OpenTypeParametersByKey.TryGetValue(name, out var openParameter))
                {
                    return openParameter;
                }

                if (GenericTypeInstantiator.TryDecodePrimitive(name, out var primitive))
                {
                    return primitive;
                }

                throw new InvalidDataException($"Unknown open type parameter '{name}'");
            }

            // 6e 跨库里程碑：基元 `@` 权威记法（@i32/@string/@bool…，Rust/LLVM 式位宽名）。
            if (name.StartsWith("@", StringComparison.Ordinal))
            {
                if (GenericTypeInstantiator.TryDecodePrimitive(name, out var primitive))
                {
                    return primitive;
                }

                throw new InvalidDataException($"Unknown primitive type '{name}'");
            }

            // 6e-G7 S1：实例化类型 mangle（backtick 元数 + # + $ 分隔递归实参）
            if (name.Contains('`') && name.Contains('#'))
            {
                return ParseInstantiatedTypeRef(name, context);
            }

            return name switch
            {
                "any" => TypeSymbol.Any,
                "null" => TypeSymbol.Null, // 6e-M19 M5-a
                "bool" => TypeSymbol.Boolean,
                "byte" => TypeSymbol.UInt8,
                "sbyte" => TypeSymbol.Int8,
                "short" => TypeSymbol.Int16,
                "ushort" => TypeSymbol.UInt16,
                "int" => TypeSymbol.Int32,
                "uint" => TypeSymbol.UInt32,
                "long" => TypeSymbol.Int64,
                "ulong" => TypeSymbol.UInt64,
                "float" => TypeSymbol.Float,
                "double" => TypeSymbol.Double,
                "char" => TypeSymbol.Char,
                "string" => TypeSymbol.String,
                "void" => TypeSymbol.Void,
                "i128" => TypeSymbol.Int128,
                "u128" => TypeSymbol.UInt128,
                "f128" => TypeSymbol.Float128,
                "?" => TypeSymbol.Error,
                _ => throw new InvalidDataException($"Unknown type '{name}'"),
            };
        }

        /// <summary>
        /// 实例化类型 mangle 递归解析（6e-G7 S1）：`定义全名\`N#实参1$...$实参N`，
        /// 按 arity 递归消费（嵌套实例化的内层 $ 归属内层分组）；叶子经
        /// !开放参数/!基元反解或既有名字解析；`[]` 后缀按数组还原。
        /// </summary>
        private static TypeSymbol ParseInstantiatedTypeRef(string text, ReadContext context)
        {
            var position = 0;
            var type = ParseEncodedType(text, ref position, context);
            if (position != text.Length)
            {
                throw new InvalidDataException($"Trailing characters in instantiated type '{text}'");
            }

            return type;
        }

        private static TypeSymbol ParseEncodedType(string text, ref int position, ReadContext context)
        {
            // ! 前缀：开放类型参数限定键
            if (position < text.Length && text[position] == '!')
            {
                var start = position;
                position++;
                while (position < text.Length && IsEncodedNameChar(text[position]))
                {
                    position++;
                }

                var key = text.Substring(start, position - start);
                if (context.OpenTypeParametersByKey.TryGetValue(key, out var openParameter))
                {
                    return ConsumeArraySuffixes(key, openParameter, text, ref position);
                }

                if (GenericTypeInstantiator.TryDecodePrimitive(key, out var primitive))
                {
                    return ConsumeArraySuffixes(key, primitive, text, ref position);
                }

                throw new InvalidDataException($"Unknown encoded type '{key}' in '{text}'");
            }

            // 6e 跨库里程碑：@ 前缀 —— 基元权威记法（@i32/@string…，mangle 实参）。
            if (position < text.Length && text[position] == '@')
            {
                var start = position;
                position++;
                while (position < text.Length && (char.IsLetterOrDigit(text[position])))
                {
                    position++;
                }

                var key = text.Substring(start, position - start);
                if (GenericTypeInstantiator.TryDecodePrimitive(key, out var primitive))
                {
                    return ConsumeArraySuffixes(key, primitive, text, ref position);
                }

                throw new InvalidDataException($"Unknown primitive '{key}' in '{text}'");
            }

            // 名字段：字母数字._ （实例化头在此处截断于 backtick）
            var nameStart = position;
            while (position < text.Length && IsEncodedNameChar(text[position]))
            {
                position++;
            }

            var fullName = text.Substring(nameStart, position - nameStart);

            // 实例化：backtick 元数 + # + N 个递归实参（$ 分隔）
            if (position < text.Length && text[position] == '`')
            {
                position++;
                var arityStart = position;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                {
                    position++;
                }

                if (!int.TryParse(text.Substring(arityStart, position - arityStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var arity) ||
                    posAt(text, position) != '#')
                {
                    throw new InvalidDataException($"Malformed instantiation arity in '{text}'");
                }

                position++; // skip '#'
                if (!context.TypesByName.TryGetValue(fullName, out var definitionObject) ||
                    definitionObject is not NamedTypeSymbol definition ||
                    !definition.IsGenericDefinition ||
                    definition.TypeParameters.Length != arity)
                {
                    throw new InvalidDataException($"Unknown generic definition or arity mismatch '{fullName}`{arity}' in '{text}'");
                }

                var arguments = ImmutableArray.CreateBuilder<TypeSymbol>(arity);
                for (var i = 0; i < arity; i++)
                {
                    if (i > 0)
                    {
                        if (posAt(text, position) != '$')
                        {
                            throw new InvalidDataException($"Expected '$' separator in '{text}'");
                        }

                        position++;
                    }

                    arguments.Add(ParseEncodedType(text, ref position, context));
                }

                var instantiated = GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
                return ConsumeArraySuffixes(fullName + "`" + arity, instantiated, text, ref position);
            }

            // 平名：类/枚举全名或别名，走既有解析
            var resolved = ResolveNamedType(fullName, context);
            return ConsumeArraySuffixes(fullName, resolved, text, ref position);
        }

        private static TypeSymbol ConsumeArraySuffixes(string debugName, TypeSymbol type, string text, ref int position)
        {
            while (position + 1 < text.Length && text[position] == '[' && text[position + 1] == ']')
            {
                position += 2;
                type = TypeSymbol.ArrayOf(type);
            }

            return type;
        }

        private static char posAt(string text, int index) => index < text.Length ? text[index] : '\0';

        private static bool IsEncodedNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '.' || c == '_';
        }

        private static NamedTypeSymbol ResolveOwnerClass(string fullName, ReadContext context)
        {
            // 6e-M19 M2-c：内建系统类（System.Object/System.Type）按单例解析（不从 cod 类型表读）。
            if (fullName == "System.Object")
            {
                return NamedTypeSymbol.SystemObject;
            }

            if (fullName == "System.Type")
            {
                return NamedTypeSymbol.SystemType;
            }

            if (!context.TypesByName.TryGetValue(fullName, out var type) || type is not NamedTypeSymbol classType)
            {
                throw new InvalidDataException($"Unknown owner class '{fullName}'");
            }

            return classType;
        }

        /// <summary>
        /// 6e 跨库里程碑：从成员键的属主段解析定义类。属主段可为：
        /// 普通全名（`System.Collections.Generic.List`）或实例化副本 mangle 双缀
        /// （`Lib!...System.Collections.Generic.List`1#...T`，InstanceTypeSymbol.FullName = ns + mangle）。
        /// 从 TypesByName（含 external 预播种）按「最长匹配 key + backtick」反解泛型定义类。
        /// </summary>
        private static NamedTypeSymbol? ResolveOwnerClassFromHead(string ownerText, ReadContext context)
        {
            if (ownerText == "System.Object")
            {
                return NamedTypeSymbol.SystemObject;
            }

            if (ownerText == "System.Type")
            {
                return NamedTypeSymbol.SystemType;
            }

            if (context.TypesByName.TryGetValue(ownerText, out var direct))
            {
                return direct as NamedTypeSymbol;
            }

            if (ownerText.Contains('`'))
            {
                // 实例化副本 mangle 双缀：找 TypesByName 中「最长 key + backtick」为 ownerText 子串
                // （InstanceTypeSymbol.FullName = ns + mangle，定义全名出现在 mangle 头部）。
                string? bestKey = null;
                foreach (var key in context.TypesByName.Keys)
                {
                    if (key.IndexOf('`') >= 0)
                    {
                        continue;
                    }

                    if (ownerText.Contains(key + "`", StringComparison.Ordinal) &&
                        (bestKey == null || key.Length > bestKey.Length))
                    {
                        bestKey = key;
                    }
                }

                if (bestKey != null && context.TypesByName.TryGetValue(bestKey, out var best) && best is NamedTypeSymbol bestClass)
                {
                    return bestClass;
                }
            }

            return null;
        }

        private static VariableSymbol ResolveVariable(string key, ReadContext context)
        {
            if (!context.VariablesByKey.TryGetValue(key, out var variable))
            {
                throw new InvalidDataException($"Unknown variable '{key}'");
            }

            return variable;
        }

        private static FunctionSymbol ResolveFunction(string key, ReadContext context)
        {
            if (context.FunctionsByKey.TryGetValue(key, out var function))
            {
                return function;
            }

            // 6e 跨库里程碑：键带库前缀（`库名!head[...]`）——先剥离前缀，再按方法名+元数在
            // 本库 + external 库函数集中归一（消费方替换期再映射回实例化副本）。
            var searchKey = key;
            var bangIndex = key.IndexOf('!');
            if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
            {
                searchKey = key.Substring(bangIndex + 1);
            }

            var bracketIndex = searchKey.LastIndexOf('[');
            if (bracketIndex > 0)
            {
                var head = searchKey.Substring(0, bracketIndex);
                var dotIndex = head.LastIndexOf('.');
                if (dotIndex > 0)
                {
                    var methodName = head.Substring(dotIndex + 1);
                    var parameterCountText = searchKey.Substring(bracketIndex + 1, searchKey.Length - bracketIndex - 2);
                    var parameterCount = parameterCountText.Length == 0
                        ? 0
                        : parameterCountText.Split(',').Length;

                    // 6e 跨库里程碑：优先按属主类精确解析——head 的 backtick 前段即泛型定义全名
                    // （实例化副本键 `Lib!...List`1#...T.get_Count[]`），在 TypesByName（含 external 预播种）
                    // 定义类内按名+元数匹配。避免全集搜索歧义（多个类同签名方法时非唯一）。
                    var ownerText = head.Substring(0, dotIndex);
                    var ownerClass = ResolveOwnerClassFromHead(ownerText, context);

                    if (ownerClass != null)
                    {
                        var ownerCandidates = ownerClass.Methods.Where(m =>
                            m.Name == methodName &&
                            m.Parameters.Length == parameterCount).ToList();

                        if (ownerCandidates.Count == 1)
                        {
                            return ownerCandidates[0];
                        }

                        if (ownerCandidates.Count > 1)
                        {
                            throw new InvalidDataException($"Ambiguous owner-class method '{key}'");
                        }
                    }

                    var candidates = context.Functions.Where(f =>
                        f.Name == methodName &&
                        f.Parameters.Length == parameterCount).ToList();

                    if (candidates.Count == 0)
                    {
                        foreach (var library in context.ExternalLibraries)
                        {
                            candidates.AddRange(library.Functions.Where(f =>
                                f.Name == methodName &&
                                f.Parameters.Length == parameterCount));
                        }
                    }

                    if (candidates.Count == 1)
                    {
                        return candidates[0];
                    }
                }
            }

            throw new InvalidDataException($"Unknown function '{key}'");
        }

        private static bool ParseBoolWord(string text)
        {
            return text switch
            {
                "true" => true,
                "false" => false,
                _ => throw new InvalidDataException($"Expected 'true'/'false' but found '{text}'"),
            };
        }

        /// <summary>璇诲彇 label:value 褰㈠紡鐨勫瓧娈靛苟鏍￠獙鏍囩銆?/summary>
        private static string ReadLabeledField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return Unescape(token.Substring(label.Length));
        }

        /// <summary>璇诲彇 count:N 褰㈠紡鐨勮鏁板瓧娈点€?/summary>
        private static int ReadCountField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return int.Parse(token.Substring(label.Length), CultureInfo.InvariantCulture);
        }

        /// <summary>鍏ㄥ悕鎷嗗垎涓猴紙鍛藉悕绌洪棿, 鍚嶏級锛涙棤鐐瑰彿鏃跺懡鍚嶇┖闂翠负绌恒€?/summary>
        private static (string Namespace, string Name) SplitFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            return lastDot < 0 ? ("", fullName) : (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1));
        }

        /// <summary>6e 跨库里程碑：从 fn 键提取库名（键格式 `库名!head[...]`；`!` 界符在 `[` 之前）。
        /// 兼容旧格式（无 `!`）与兼容入口（moduleName 为空）：回退 moduleName。</summary>
        private static string ExtractLibraryFromKey(string key, ReadContext context)
        {
            var bangIndex = key.IndexOf('!');
            if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
            {
                return key.Substring(0, bangIndex);
            }

            return context.ModuleName;
        }

        /// <summary>鍙橀噺閿繕鍘熺湡瀹炵鍙峰悕锛氬幓鎺?global:/鍑芥暟閿墠缂€涓?#N 鍐茬獊鍚庣紑銆?/summary>
        private static string KeyToName(string key)
        {
            var name = key;
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            var hash = name.LastIndexOf('#');
            if (hash >= 0 && int.TryParse(name.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                name = name.Substring(0, hash);
            }

            return Unescape(name);
        }

        // ---------------------------------------------------------------- read: statements

        private static BoundStatement ReadStatement(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            var kind = reader.ExpectKind();
            var statement = ReadStatementFromToken(reader, kind, context, labels);
            reader.End();
            return statement;
        }

        private static BoundStatement ReadStatementFromToken(Reader reader, string kind, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "block":
                    {
                        var count = reader.ExpectInt();
                        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                        for (var i = 0; i < count; i++)
                        {
                            statements.Add(ReadStatement(reader, context, labels));
                        }

                        return new BoundBlockStatement(null, statements.ToImmutable());
                    }
                case "nop":
                    return new BoundNopStatement(null);
                case "vardecl":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var initializer = ReadExpression(reader, context, labels);
                        return new BoundVariableDeclaration(null, variable, initializer);
                    }
                case "if":
                    {
                        var condition = ReadExpression(reader, context, labels);
                        var then = ReadStatement(reader, context, labels);
                        var elseStatement = ReadNullableStatement(reader, context, labels);
                        return new BoundIfStatement(null, condition, then, elseStatement);
                    }
                case "while":
                    {
                        var condition = ReadExpression(reader, context, labels);
                        var body = ReadStatement(reader, context, labels);
                        var breakLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        var continueLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        return new BoundWhileStatement(null, condition, body, breakLabel, continueLabel);
                    }
                case "dowhile":
                    {
                        var body = ReadStatement(reader, context, labels);
                        var condition = ReadExpression(reader, context, labels);
                        var breakLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        var continueLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        return new BoundDoWhileStatement(null, body, condition, breakLabel, continueLabel);
                    }
                case "for":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var lowerBound = ReadExpression(reader, context, labels);
                        var upperBound = ReadExpression(reader, context, labels);
                        var step = ReadNullableExpression(reader, context, labels);
                        var body = ReadStatement(reader, context, labels);
                        var breakLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        var continueLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        return new BoundForRangeStatement(null, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
                    }
                case "label":
                    return new BoundLabelStatement(null, GetLabel(labels, Unescape(reader.ExpectString())));
                case "goto":
                    return new BoundGotoStatement(null, GetLabel(labels, Unescape(reader.ExpectString())));
                case "cgoto":
                    {
                        var label = GetLabel(labels, Unescape(reader.ExpectString()));
                        var condition = ReadExpression(reader, context, labels);
                        var jumpIfTrue = ParseBoolWord(reader.ExpectString());
                        return new BoundConditionalGotoStatement(null, label, condition, jumpIfTrue);
                    }
                case "return":
                    {
                        var expression = ReadNullableExpression(reader, context, labels);
                        return new BoundReturnStatement(null, expression);
                    }
                case "exprstmt":
                    {
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundExpressionStatement(null, expression);
                    }
                default:
                    throw new InvalidDataException($"Unknown statement kind '{kind}'");
            }
        }

        private static BoundStatement? ReadNullableStatement(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            if (reader.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var statement = ReadStatementFromToken(reader, token, context, labels);
            reader.End();
            return statement;
        }

        private static BoundExpression? ReadNullableExpression(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            if (reader.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var expression = ReadExpressionFromToken(reader, token, context, labels);
            reader.End();
            return expression;
        }

        private static BoundExpression ReadExpression(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            var token = reader.ExpectKind();
            var expression = ReadExpressionFromToken(reader, token, context, labels);
            reader.End();
            return expression;
        }

        private static BoundExpression ReadExpressionFromToken(Reader reader, string kind, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "lit":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var encoded = reader.ExpectString();
                        var value = DecodeValue(encoded);
                        return new BoundLiteralExpression(null, value, type);
                    }
                case "var":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        return new BoundVariableExpression(null, variable);
                    }
                case "assign":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundAssignmentExpression(null, variable, expression);
                    }
                case "cassign":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var op = ReadBinaryOperator(reader, context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundCompoundAssignmentExpression(null, variable, op, expression);
                    }
                case "unary":
                    {
                        var op = ReadUnaryOperator(reader, context);
                        var operand = ReadExpression(reader, context, labels);
                        return new BoundUnaryExpression(null, op, operand);
                    }
                case "binary":
                    {
                        var op = ReadBinaryOperator(reader, context);
                        var left = ReadExpression(reader, context, labels);
                        var right = ReadExpression(reader, context, labels);
                        return new BoundBinaryExpression(null, left, op, right);
                    }
                case "cond":
                    {
                        var condition = ReadExpression(reader, context, labels);
                        var whenTrue = ReadExpression(reader, context, labels);
                        var whenFalse = ReadExpression(reader, context, labels);
                        return new BoundConditionalExpression(null, condition, whenTrue, whenFalse);
                    }
                case "call":
                    {
                        var function = ResolveFunction(reader.ExpectString(), context);
                        var count = reader.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundCallExpression(null, function, arguments.ToImmutable());
                    }
                case "byrefarg":
                    {
                        // 6e-M23 R8锛歰ut/ref 瀹炲弬鍖呰锛堝唴灞備负鍙祴鍊?lvalue锛?
                        var modifier = reader.ExpectString();
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundByRefArgument(null, expression, isRef: modifier == "ref");
                    }
                case "conv":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundConversionExpression(null, type, expression);
                    }
                case "arrnew":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var length = ReadExpression(reader, context, labels);
                        var count = reader.ExpectInt();
                        var initializers = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            initializers.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundArrayCreationExpression(null, type, length, initializers.ToImmutable());
                    }
                case "objnew":
                    {
                        // M0-1c：对象创建 `new Foo(args)`——构造器由类型+元数重解析
                        var type = (NamedTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        var argCount = reader.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < argCount; i++)
                        {
                            arguments.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundObjectCreationExpression(null, type, arguments.ToImmutable());
                    }
                case "elem":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var target = ReadExpression(reader, context, labels);
                        var index = ReadExpression(reader, context, labels);
                        return new BoundElementAccessExpression(null, type, target, index);
                    }
                case "elemassign":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var target = (BoundElementAccessExpression)ReadExpression(reader, context, labels);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundElementAssignmentExpression(null, type, target, expression);
                    }
                case "memberacc":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var identifier = Unescape(reader.ExpectString());

                        // 6e-G7 S2：owner 字段可选携带 → 回填 FieldSymbol（实例化类型的 Fields 经物化钩子可达）
                        FieldSymbol? field = null;
                        var hasOwner = reader.PeekRaw().StartsWith("owner:", StringComparison.Ordinal);
                        if (hasOwner)
                        {
                            var ownerFullName = ReadLabeledField(reader, "owner:");
                            if (ResolveNamedType(ownerFullName, context) is NamedTypeSymbol ownerClass)
                            {
                                field = ownerClass.Fields.FirstOrDefault(f => f.Name == identifier);
                            }
                        }

                        var target = ReadExpression(reader, context, labels);
                        return new BoundMemberAccessExpression(null, type, target, identifier, field);
                    }
                case "memberassign":
                    {
                        // 6e-G7 S2：字段赋值读回——Field 由 target 形态 + 名字解析
                        var target = ReadExpression(reader, context, labels);
                        var fieldName = Unescape(ReadLabeledField(reader, "name:"));
                        _ = ResolveTypeRef(reader.ExpectString(), context);
                        _ = ParseBoolWord(reader.ExpectString());
                        var value = ReadExpression(reader, context, labels);

                        FieldSymbol? field = target switch
                        {
                            // 6e-G7：隐式 this 赋值（`_value = v`）——字段在 this 的类上
                            BoundThisExpression thisExpression => ((NamedTypeSymbol)thisExpression.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            BoundMemberAccessExpression access => access.Field,
                            BoundStaticTypeExpression staticType => ((NamedTypeSymbol)staticType.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            _ => null,
                        };

                        if (field == null)
                        {
                            throw new InvalidDataException($"Unknown field '{fieldName}' in memberassign");
                        }

                        return new BoundMemberAssignmentExpression(null, target, field, value);
                    }
                case "membercall":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var identifier = Unescape(reader.ExpectString());
                        var methodToken = reader.ExpectString();
                        var method = methodToken == "-" ? null : ResolveFunction(methodToken, context);
                        var count = reader.ExpectInt();
                        var target = ReadExpression(reader, context, labels);
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundMemberCallExpression(null, target, identifier, arguments.ToImmutable(), type, method);
                    }
                case "statictype":
                    {
                        var type = (NamedTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        return new BoundStaticTypeExpression(null, type);
                    }
                case "this":
                    {
                        var type = (NamedTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        return new BoundThisExpression(null, type);
                    }
                case "istype":
                    {
                        var targetType = ResolveTypeRef(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundIsExpression(null, expression, targetType);
                    }
                case "astype":
                    {
                        var targetType = ResolveTypeRef(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundAsExpression(null, expression, targetType);
                    }
                default:
                    throw new InvalidDataException($"Unknown expression kind '{kind}'");
            }
        }

        private static BoundUnaryOperator ReadUnaryOperator(Reader reader, ReadContext context)
        {
            reader.Expect("uop");
            var unaryKind = ParseUnaryOpText(reader.ExpectString());
            var operandType = ResolveTypeRef(reader.ExpectString(), context);
            var op = BoundUnaryOperator.Bind(unaryKind, operandType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind unary operator {unaryKind} on {operandType}");
        }

        private static BoundBinaryOperator ReadBinaryOperator(Reader reader, ReadContext context)
        {
            reader.Expect("bop");
            var binaryKind = ParseBinaryOpText(reader.ExpectString());
            var leftType = ResolveTypeRef(reader.ExpectString(), context);
            var rightType = ResolveTypeRef(reader.ExpectString(), context);
            var op = BoundBinaryOperator.Bind(binaryKind, leftType, rightType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind binary operator {binaryKind} on {leftType} and {rightType}");
        }

        private static BoundLabel GetLabel(Dictionary<string, BoundLabel> labels, string name)
        {
            if (!labels.TryGetValue(name, out var label))
            {
                label = new BoundLabel(name);
                labels[name] = label;
            }

            return label;
        }

        // ---------------------------------------------------------------- read: tokenizer / reader

        private static IEnumerable<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '(' || c == ')')
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }

                    tokens.Add(c.ToString());
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }

                    continue;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        private sealed class Reader
        {
            private readonly string[] _tokens;
            private int _pos;

            public Reader(string[] tokens)
            {
                _tokens = tokens;
            }

            public string Expect(string kind)
            {
                var token = Next();
                if (token != kind)
                {
                    throw new InvalidDataException($"Expected '{kind}' but found '{token}'");
                }

                return token;
            }

            public string ExpectKind()
            {
                var token = Next();
                if (token == "(" || token == ")")
                {
                    throw new InvalidDataException($"Expected kind token but found '{token}'");
                }

                return token;
            }

            public string ExpectString()
            {
                var token = Next();
                if (token == "(" || token == ")")
                {
                    throw new InvalidDataException($"Expected atom but found '{token}'");
                }

                return token;
            }

            public int ExpectInt()
            {
                var token = ExpectString();
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    throw new InvalidDataException($"Expected integer but found '{token}'");
                }

                return value;
            }

            /// <summary>绐ユ帰褰撳墠鍘熷 token锛堜笉璺宠繃 `(`锛夆€斺€旂敤浜庡垽鏂瓙鑺傜偣鏄惁鍑虹幇銆?/summary>
            public string PeekRaw()
            {
                return _pos < _tokens.Length ? _tokens[_pos] : "";
            }

            public bool TryExpect(out string token)
            {
                // 璺宠繃鑺傜偣寮€鎷彿 `(`
                while (_pos < _tokens.Length && _tokens[_pos] == "(")
                {
                    _pos++;
                }

                if (_pos >= _tokens.Length)
                {
                    token = null!;
                    return false;
                }

                // `)` 涓嶆秷璐癸紙鐣欑粰 End()锛夛紝杩斿洖 false 缁堟褰撳墠鍒楄〃
                if (_tokens[_pos] == ")")
                {
                    token = ")";
                    return false;
                }

                token = _tokens[_pos++];
                return true;
            }

            public void End()
            {
                // 褰撳墠 token 搴斾负鑺傜偣闂嫭鍙?`)`锛堢洿鎺ユ秷璐癸紝涓嶈烦杩?`(`锛?
                if (_pos >= _tokens.Length)
                {
                    throw new InvalidDataException($"unexpected end of .coa file at pos {_pos}; context: {Context()}");
                }

                var token = _tokens[_pos++];
                if (token != ")")
                {
                    throw new InvalidDataException($"Expected ')' but found '{token}' at pos {_pos - 1}; context: {Context()}");
                }
            }

            private string Context()
            {
                var start = Math.Max(0, _pos - 12);
                var count = Math.Min(_tokens.Length - start, 24);
                return string.Join(" ", _tokens, start, count);
            }

            private string Next()
            {
                // 璺宠繃鑺傜偣寮€鎷彿 `(`锛涜繑鍥炲師瀛愭垨 `)`锛堝垪琛ㄧ粓姝級
                while (true)
                {
                    if (_pos >= _tokens.Length)
                    {
                        throw new InvalidDataException("unexpected end of .coa file");
                    }

                    var token = _tokens[_pos++];
                    if (token != "(")
                    {
                        return token;
                    }
                }
            }
        }
    }
}
