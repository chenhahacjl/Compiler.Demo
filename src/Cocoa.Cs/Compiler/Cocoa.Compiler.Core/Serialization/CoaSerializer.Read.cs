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
    /// `.coa` 语义层序列化器：符号表 + 降级 BoundProgram（函数体）文本 round-trip。
    /// 双后端共用（native → MirToLir，IL → IlEmitter）；语法节点（Syntax）不序列化（置 null）。
    ///
    /// 文本格式（可读优先，类型/函数/变量一律按名字引用，不用数字 id）：
    ///   (type)     内建/数组类型内联为名字引用：int / int[] / int[][]；类/枚举用全名 System.Console
    ///   (enum)     (enum MyLib.Color members:3 (Red 0) (Green 1) (Blue 2))
    ///   (systype)  (systype System.Object)——内建单例按全名映射
    ///   (cls)      (cls System.Console public methods:2 WriteLine[string] ReadKey)——方法列 Name[参数类型] 签名
    ///   (fn)       (fn MyLib.Add(i32,i32) name:Add ret:i32 ns:MyLib owner:- extern:false ...
    ///               params:2 (par MyLib.Add/a a i32 0) ...)
    ///              函数键 = [命名空间或宿主类.]函数名(参数类型列表)，重载靠参数类型区分
    ///   (glb/loc)  (glb global:version true i32 (const i:1)) / (loc MyLib.Factorial/result false i32)
    ///              变量键：全局 global:名字；局部（参数）函数键/名字（同名冲突加 #2、#3 后缀）。
    ///   运算符     文本记号 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   布尔/枚举词： true false；public internal protected private；winapi cdecl stdcall；unicode ansi auto
    /// </summary>
    public static partial class CoaSerializer
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
            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
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
                        ApplyPendingClosures(context);
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

        /// <summary>读侧共享状态：按名字/键索引的符号表 + 程序集符号清单。</summary>
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

            /// <summary>类/枚举全名 → 类型符号（内建类型不经此表，直接解析）。</summary>
            public Dictionary<string, TypeSymbol> TypesByName { get; } = new(StringComparer.Ordinal);

            /// <summary>6e 跨库里程碑：本库自持类型表（全名 → 符号）——CoaProgram.TypesByName 导出源，
            /// 供其他库读侧 external 合并。与 TypesByName 的区别：不含预播种的 external 符号。</summary>
            public Dictionary<string, TypeSymbol> LocalTypesByName { get; } = new(StringComparer.Ordinal);

            /// <summary>6e-G7 S1：开放类型参数限定键（!属主全名.参数名）→ 符号。文件级平铺——限定键天然无碰撞。</summary>
            public Dictionary<string, TypeParameterSymbol> OpenTypeParametersByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>函数键 → 函数符号。</summary>
            public Dictionary<string, FunctionSymbol> FunctionsByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>6e 跨库里程碑：本库自持函数键（含库前缀）→ 符号——CoaProgram.FunctionKeys 导出源。</summary>
            public Dictionary<string, FunctionSymbol> LocalFunctionKeys { get; } = new(StringComparer.Ordinal);

            /// <summary>变量键 → 变量/参数符号。</summary>
            public Dictionary<string, VariableSymbol> VariablesByKey { get; } = new(StringComparer.Ordinal);

            public ImmutableArray<FunctionSymbol>.Builder Functions { get; } = ImmutableArray.CreateBuilder<FunctionSymbol>();

            public ImmutableArray<GlobalVariableSymbol>.Builder Globals { get; } = ImmutableArray.CreateBuilder<GlobalVariableSymbol>();

            public ImmutableArray<NamedTypeSymbol>.Builder Enums { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

            public ImmutableArray<NamedTypeSymbol>.Builder Classes { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

            /// <summary>6e-G7 S1：泛型定义类（gcls 读入）。</summary>
            public ImmutableArray<NamedTypeSymbol>.Builder GenericDefinitions { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

            /// <summary>6b：facade 类属性待挂接声明（访问器 fns 读毕后重建 PropertySymbol）。</summary>
            public List<(NamedTypeSymbol ClassType, string Name, TypeSymbol Type, bool HasGet, bool HasSet, Visibility Visibility, bool IsStatic)> PendingProperties { get; } = new();

            /// <summary>6f-4：捕获闭包元数据待回填（捕获变量 loc 晚于 fn 记录——全符号读毕后再解析）。</summary>
            public List<(FunctionSymbol Function, bool IsLambdaWithEnvironment, NamedTypeSymbol? EnvironmentClass, List<string> CapturedKeys)> PendingClosures { get; } = new();

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

        /// <summary>6f-4：捕获闭包元数据回填——全符号读毕后按变量键解析捕获清单（host 播种 / lambda env 依赖）。</summary>
        private static void ApplyPendingClosures(ReadContext context)
        {
            foreach (var (function, isLambdaWithEnvironment, environmentClass, capturedKeys) in context.PendingClosures)
            {
                if (capturedKeys.Count == 0)
                {
                    continue;
                }

                var captures = new List<VariableSymbol>(capturedKeys.Count);
                foreach (var key in capturedKeys)
                {
                    if (!context.VariablesByKey.TryGetValue(key, out var variable))
                    {
                        throw new InvalidDataException($"Unknown captured variable '{key}' for closure function '{function.Name}'");
                    }

                    // 捕获标记回填：宿主/lambda 两侧读取统一走环境字段（发射器按 IsCaptured 分派）
                    variable.IsCaptured = true;
                    captures.Add(variable);
                }

                function.CapturedVariables = captures;
            }

            context.PendingClosures.Clear();
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
            enumType.ContainingLibrary = context.ModuleName;
            enumType.SetEnumMembers(members);
            context.Enums.Add(enumType);
            context.AddNamedType(fullName, enumType);
            reader.End();
        }

        private static void ReadSystemType(Reader reader, ReadContext context)
        {
            // 6e-M19 M2-c：内建单例按全名映射（成员面已由 Ensure 内建注入）。
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

            // 6e-Step D-c：delegate 标记（紧随 visibility；读侧重建 TypeKind.Delegate，Invoke 由 fn owner 挂到 Methods）
            var isDelegateKind = false;
            if (reader.PeekRaw().StartsWith("tk:", StringComparison.Ordinal))
            {
                isDelegateKind = ReadLabeledField(reader, "tk:").Equals("delegate", StringComparison.Ordinal);
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
            // 方法名仅供阅读，方法符号由各自 fn 条目的 owner 字段回填
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectString();
            }

            var classType = new NamedTypeSymbol(name, ns, visibility, declaration: null);
            classType.ContainingLibrary = context.ModuleName;
            // 6e-M19 M2-c：cod 类默认继承 System.Object（与源码绑定一致；.coa v1 不序列化接口声明）。
            classType.BaseType = NamedTypeSymbol.SystemObject;
            // 6e-G7/M0-1a：接口位回填 + 实现接口列表回填
            if (isInterface)
            {
                classType.TypeKind = TypeKind.Interface;
            }
            else if (isDelegateKind)
            {
                classType.TypeKind = TypeKind.Delegate;
            }

            foreach (var interfaceRef in interfaceRefs)
            {
                classType.AddInterface((NamedTypeSymbol)ResolveTypeRef(interfaceRef, context));
            }

            context.Classes.Add(classType);
            context.GenericDefinitions.Add(classType);
            context.AddNamedType(fullName, classType);

            // 6e-Step D-a：类字段（含闭包环境类 __Env_* 捕获实例成员）解析回填——与写侧 fields:/methods: 顺序一致
            if (reader.PeekRaw().StartsWith("fields:", StringComparison.Ordinal))
            {
                var classFieldCount = ReadCountField(reader, "fields:");
                for (var i = 0; i < classFieldCount; i++)
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
            }

            // 6e-Step D-b：事件声明读回（handler 解析为 FunctionTypeSymbol）
            if (reader.PeekRaw().StartsWith("events:", StringComparison.Ordinal))
            {
                var eventCount = ReadCountField(reader, "events:");
                for (var i = 0; i < eventCount; i++)
                {
                    reader.Expect("evt");
                    var eventName = Unescape(reader.ExpectString());
                    var handlerType = (FunctionTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                    var eventVisibilityText = reader.ExpectString();
                    if (!Enum.TryParse<Visibility>(eventVisibilityText, ignoreCase: true, out var eventVisibility))
                    {
                        throw new InvalidDataException($"Unknown visibility '{eventVisibilityText}' on event '{fullName}.{eventName}'");
                    }

                    classType.AddEvent(new EventSymbol(eventName, handlerType, eventVisibility, classType));
                    reader.End();
                }
            }

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
            classType.ContainingLibrary = context.ModuleName;
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

            // 6e-Step D-b：泛型定义类事件声明读回
            if (reader.PeekRaw().StartsWith("events:", StringComparison.Ordinal))
            {
                var eventCount = ReadCountField(reader, "events:");
                for (var i = 0; i < eventCount; i++)
                {
                    reader.Expect("evt");
                    var eventName = Unescape(reader.ExpectString());
                    var handlerType = (FunctionTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                    var eventVisibilityText = reader.ExpectString();
                    if (!Enum.TryParse<Visibility>(eventVisibilityText, ignoreCase: true, out var eventVisibility))
                    {
                        throw new InvalidDataException($"Unknown visibility '{eventVisibilityText}' on event '{fullName}.{eventName}'");
                    }

                    classType.AddEvent(new EventSymbol(eventName, handlerType, eventVisibility, classType));
                    reader.End();
                }
            }

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

            // 6f-4：捕获闭包元数据（旧文件无此字段 → 缺省非 lambda/无 env/无捕获）
            var isLambdaWithEnvironment = false;
            var isLambda = false;
            NamedTypeSymbol? environmentClass = null;
            var capturedKeys = new List<string>();
            if (reader.PeekRaw().StartsWith("envn:", StringComparison.Ordinal))
            {
                isLambdaWithEnvironment = ParseBoolWord(ReadLabeledField(reader, "envn:"));
                isLambda = ParseBoolWord(ReadLabeledField(reader, "envl:"));
                var envcText = ReadLabeledField(reader, "envc:");
                environmentClass = envcText == "-" ? null : (NamedTypeSymbol)ResolveTypeRef(envcText, context);
                var envcapCount = ReadCountField(reader, "envcap:");
                for (var i = 0; i < envcapCount; i++)
                {
                    capturedKeys.Add(reader.ExpectString());
                }
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

                // 6e-M23 R8：第 5 个 token = out/ref/-（旧文件无此 token，按 "-" 兼容）。
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

            // 6e-M19 M2-c：Object 内建方法复用单例（保持符号同一性，发射器按 BuiltinKind 分发）。
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

            // 含类归属或内置种类：不复用全局单例（内置单例无类归属），重建带上下文符号。
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

            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
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

            // 6f-4：捕获闭包元数据回填——IsLambdaWithEnvironment/IsLambda/EnvironmentClass 即时；
            // 捕获变量（param/loc 引用）待全符号读毕（loc 晚于 fn 记录）统一解析。
            if (isLambdaWithEnvironment || environmentClass != null || capturedKeys.Count > 0)
            {
                function.IsLambda = isLambda;
                function.IsLambdaWithEnvironment = isLambdaWithEnvironment;
                if (environmentClass != null)
                {
                    function.EnvironmentClass = environmentClass;
                }

                context.PendingClosures.Add((function, isLambdaWithEnvironment, environmentClass, capturedKeys));
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

            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
                if (function.IsExtern)
                {
                    body = new BoundBlockStatement(NoSyntax, ImmutableArray<BoundStatement>.Empty);
                }

                bodies[function] = body;
                reader.End();
            }

            reader.End();
        }

        // ---------------------------------------------------------------- read: resolution helpers

        /// <summary>从 `.coa` 文件加载程序集。</summary>
        /// <summary>Load `.coa` 文件。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
        public static CoaProgram Load(string path, ImmutableArray<CoaProgram>? external = null)
        {
            var moduleName = Path.GetFileNameWithoutExtension(path);
            return Read(File.ReadAllText(path), moduleName, external ?? ImmutableArray<CoaProgram>.Empty);
        }
    }
}
