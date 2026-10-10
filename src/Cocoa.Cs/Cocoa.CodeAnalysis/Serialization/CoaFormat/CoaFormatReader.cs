using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis.Bound;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>
    /// .coa 读端协调器（源 <c>CoaSerializer</c> 的读侧收敛为实例类）：
    /// 持有 token 读取器 + 读上下文 + 类型解析器（<see cref="CoaTextReader"/>/<see cref="CoaReadContext"/>/<see cref="CoaTypeResolver"/>），
    /// 所有 Read* 为实例方法，递归不再显式传上下文。
    /// </summary>
    internal sealed class CoaFormatReader
    {
        private readonly CoaTextReader _tokens;
        private readonly CoaReadContext _context;
        private readonly CoaTypeResolver _resolver;

        /// <summary>.coa 反序列化不携带语法节点（设计如此，见类头注释）；nullable 单点豁免。</summary>
        private static SyntaxNode NoSyntax => null!;

        private CoaFormatReader(string[] tokens, string moduleName, ImmutableArray<CoaProgram> external)
        {
            _tokens = new CoaTextReader(tokens);
            _context = new CoaReadContext(moduleName, external);
            _resolver = new CoaTypeResolver(_context);
        }

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
            var marker = "(checksum " + CoaText.ChecksumTag;
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
            var actual = CoaText.ComputeChecksum(payload);
            if (!string.Equals(provided, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($".coa checksum mismatch: library corrupted or modified (expected {actual}, got {provided})");
            }

            var tokens = Tokenize(payload).ToArray();
            var reader = new CoaFormatReader(tokens, moduleName, external);
            return reader.ReadProgram();
        }

        /// <summary>从 `.coa` 文件加载程序集。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
        public static CoaProgram Load(string path, ImmutableArray<CoaProgram>? external = null)
        {
            var moduleName = Path.GetFileNameWithoutExtension(path);
            return Read(File.ReadAllText(path), moduleName, external ?? ImmutableArray<CoaProgram>.Empty);
        }

        private CoaProgram ReadProgram()
        {
            _tokens.Expect("cod");

            var magic = _tokens.ExpectString();
            if (magic != CoaFormatWriter.Magic)
            {
                throw new InvalidDataException($"invalid .coa magic '{magic}'");
            }

            var version = _tokens.ExpectInt();
            if (version != CoaFormatWriter.Version)
            {
                throw new InvalidDataException($".coa version {version} is not supported (expected {CoaFormatWriter.Version}); rebuild the library");
            }

            var bodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var requires = CoaRequirement.Any;
            var platforms = ImmutableArray.CreateBuilder<string>();
            var dotnetRefs = ImmutableArray.CreateBuilder<string>();
            var codRefs = ImmutableArray.CreateBuilder<string>();
            var imports = ImmutableArray.CreateBuilder<string>();
            var namespaces = ImmutableArray.CreateBuilder<string>();
            var docs = ImmutableDictionary.CreateBuilder<string, string>();

            while (_tokens.TryExpect(out var child))
            {
                switch (child)
                {
                    case "symbols":
                        ReadSymbols();
                        ApplyPendingProperties();
                        ApplyPendingClosures();
                        ApplyPendingBaseTypes();
                        ApplyPendingFields();
                        break;
                    case "bodies":
                        ReadBodies(bodies);
                        break;
                    case "manifest":
                        while (_tokens.TryExpect(out var item))
                        {
                            switch (item)
                            {
                                case "requires":
                                    requires = CoaText.ParseRequirement(_tokens.ExpectString());
                                    break;
                                case "platform":
                                    platforms.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "refdll":
                                    dotnetRefs.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "refcod":
                                    codRefs.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "import":
                                    imports.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "ns":
                                    namespaces.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                            }

                            _tokens.End();
                        }

                        _tokens.End();
                        break;
                    case "docs":
                        while (_tokens.TryExpect(out var docItem))
                        {
                            switch (docItem)
                            {
                                case "doc":
                                    var docId = CoaText.Unescape(_tokens.ExpectString());
                                    var docText = CoaText.Unescape(_tokens.ExpectString());
                                    docs[docId] = docText;
                                    break;
                            }

                            _tokens.End();
                        }

                        _tokens.End();
                        break;
                }
            }

            // 6e 跨库里程碑：库名回填——优先取传入 moduleName；兼容入口（空）从本库 fn 键前缀恢复
            // （保证 read→write round-trip 稳定，重写时 RegisterFunction 跨库过滤不误伤本库函数）。
            var programName = _context.ModuleName.Length > 0
                ? _context.ModuleName
                : RecoverLibraryFromKeys(_context.LocalFunctionKeys.Keys);

            var program = new CoaProgram(
                _context.Functions.ToImmutable(),
                _context.Globals.ToImmutable(),
                _context.Enums.ToImmutable(),
                _context.Classes.ToImmutable(),
                bodies.ToImmutable(),
                requires,
                platforms.ToImmutable(),
                dotnetRefs.ToImmutable(),
                imports.ToImmutable(),
                codRefs.ToImmutable(),
                namespaces.ToImmutable(),
                _context.GenericDefinitions.ToImmutable(),
                functionKeys: _context.LocalFunctionKeys.ToImmutableDictionary(),
                typesByName: _context.LocalTypesByName.ToImmutableDictionary(),
                docs: docs.ToImmutable())
            {
                Name = programName,
            };

            ApplyDocumentation(program);

            return program;
        }

        /// <summary>
        /// 6e-M24：`.coa` 读侧文档回填——按 DocID 从 <see cref="CoaProgram.Docs"/> 查表挂到符号。
        /// 跨程序集符号（stdlib）的 <c>Declaration = null</c>，文档只能经序列化通道恢复（设计 §6.3）。
        /// </summary>
        private void ApplyDocumentation(CoaProgram program)
        {
            if (program.Docs.Count == 0)
            {
                return;
            }

            foreach (var symbol in EnumerateProgramSymbols(program))
            {
                if (symbol.DocumentationText != null)
                {
                    continue;
                }

                var docId = DocIdBuilder.GetDocId(symbol);
                if (docId != null && program.Docs.TryGetValue(docId, out var text))
                {
                    symbol.DocumentationText = text;
                }
            }
        }

        private static IEnumerable<Symbol> EnumerateProgramSymbols(CoaProgram program)
        {
            foreach (var fn in program.Functions)
            {
                yield return fn;
            }

            foreach (var type in program.Classes.Concat(program.GenericDefinitions))
            {
                yield return type;
                foreach (var method in type.Methods)
                {
                    yield return method;
                }

                foreach (var field in type.Fields)
                {
                    yield return field;
                }

                foreach (var property in type.Properties)
                {
                    yield return property;
                }

                foreach (var evt in type.Events)
                {
                    yield return evt;
                }
            }

            foreach (var global in program.Globals)
            {
                yield return global;
            }

            foreach (var en in program.Enums)
            {
                yield return en;
            }
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

        // ---------------------------------------------------------------- read: symbols

        private void ReadSymbols()
        {
            while (_tokens.TryExpect(out var kind))
            {
                switch (kind)
                {
                    case "enum":
                        ReadEnum();
                        break;
                    case "systype":
                        ReadSystemType();
                        break;
                    case "cls":
                        ReadClass();
                        break;
                    case "gcls":
                        ReadGenericClass();
                        break;
                    case "fn":
                        ReadFunction();
                        break;
                    case "glb":
                        ReadVariable(isGlobal: true);
                        break;
                    case "loc":
                        ReadVariable(isGlobal: false);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown symbol kind '{kind}'");
                }
            }

            _tokens.End();
        }

        /// <summary>6b：facade 类属性回填——访问器 fns（`get_X`/`set_X`，静态 + this 参）已读入类方法，据名挂接重建 PropertySymbol。</summary>
        private void ApplyPendingProperties()
        {
            foreach (var (classType, name, typeRef, hasGet, hasSet, visibility, isStatic) in _context.PendingProperties)
            {
                FunctionSymbol? getter = hasGet ? classType.GetDeclaredMethod("get_" + name) : null;
                FunctionSymbol? setter = hasSet ? classType.GetDeclaredMethod("set_" + name) : null;
                // 6e-M25：类型延后解析（可能指向文件中后声明的类）
                var type = _resolver.ResolveTypeRef(typeRef);
                // 6e 跨库里程碑：索引器属性（绑定侧统一命名 `Item`）重建时须带 isIndexer 位，
                // 否则实例化类型 GetIndexer() 命不中 → 元素访问回落数组判定报错。
                classType.AddProperty(new PropertySymbol(name, type, classType, getter, setter, visibility, isStatic, isIndexer: name == "Item"));
            }

            _context.PendingProperties.Clear();
        }

        /// <summary>6e-M25：类字段回填——字段类型可能指向文件中后声明的类，全类注册后统一 ResolveTypeRef。</summary>
        private void ApplyPendingFields()
        {
            foreach (var (classType, name, typeRef, visibility, isReadonly, isStatic) in _context.PendingFields)
            {
                var fieldType = _resolver.ResolveTypeRef(typeRef);
                classType.AddField(new FieldSymbol(name, fieldType, visibility, classType, isReadonly, isStatic));
            }

            _context.PendingFields.Clear();
        }

        /// <summary>6f-4：捕获闭包元数据回填——全符号读毕后按变量键解析捕获清单（host 播种 / lambda env 依赖）。</summary>
        private void ApplyPendingClosures()
        {
            foreach (var (function, isLambdaWithEnvironment, environmentClass, capturedKeys) in _context.PendingClosures)
            {
                if (capturedKeys.Count == 0)
                {
                    continue;
                }

                var captures = new List<VariableSymbol>(capturedKeys.Count);
                foreach (var key in capturedKeys)
                {
                    if (!_context.VariablesByKey.TryGetValue(key, out var variable))
                    {
                        throw new InvalidDataException($"Unknown captured variable '{key}' for closure function '{function.Name}'");
                    }

                    // 捕获标记回填：宿主/lambda 两侧读取统一走环境字段（发射器按 IsCaptured 分派）
                    variable.IsCaptured = true;
                    captures.Add(variable);
                }

                function.CapturedVariables = captures;
            }

            _context.PendingClosures.Clear();
        }

        /// <summary>6e-M33：基类回填——全部类注册后按引用解析（base 类声明可能晚于子类），解析失败明确报错。</summary>
        private void ApplyPendingBaseTypes()
        {
            foreach (var (classType, baseRef) in _context.PendingBaseTypes)
            {
                if (_resolver.ResolveTypeRef(baseRef) is NamedTypeSymbol baseClass)
                {
                    classType.BaseType = baseClass;
                }
                else
                {
                    throw new InvalidDataException($"Unknown base type '{baseRef}' for class '{classType.FullName}'");
                }
            }

            _context.PendingBaseTypes.Clear();
        }

        private void ReadEnum()
        {
            var fullName = _tokens.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            // facade 统一：`bclTarget:` 存在且非 `-` 即门面（枚举全名即 BCL 目标）；`-` 表非门面。
            var isFacade = false;
            if (_tokens.PeekRaw().StartsWith("bclTarget:", StringComparison.Ordinal))
            {
                isFacade = _tokens.ReadLabeledField("bclTarget:") != "-";
            }
            var count = _tokens.ReadCountField("members:");
            var members = new Dictionary<string, int>();
            for (var i = 0; i < count; i++)
            {
                var memberName = _tokens.ExpectKind();
                var value = _tokens.ExpectInt();
                members[CoaText.Unescape(memberName)] = value;
                _tokens.End();
            }

            var enumType = new NamedTypeSymbol(name, ns, Visibility.Public, declaration: null)
            {
                TypeKind = TypeKind.Enum,
                IsSealed = true,
                IsFacadeClass = isFacade,
            };
            enumType.ContainingLibrary = _context.ModuleName;
            enumType.SetEnumMembers(members);
            _context.Enums.Add(enumType);
            _context.AddNamedType(fullName, enumType);
            _tokens.End();
        }

        private void ReadSystemType()
        {
            // 6e-M19 M2-c：内建单例按全名映射（成员面已由 Ensure 内建注入）。
            var fullName = _tokens.ExpectString();
            var singleton = fullName switch
            {
                "System.Object" => NamedTypeSymbol.SystemObject,
                "System.Type" => NamedTypeSymbol.SystemType,
                _ => throw new InvalidDataException($"Unknown builtin system class '{fullName}'"),
            };
            _context.Classes.Add(singleton);
            _context.AddNamedType(fullName, singleton);
            _tokens.End();
        }

        private void ReadClass()
        {
            var fullName = _tokens.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var visibilityText = _tokens.ExpectString();
            if (!Enum.TryParse<Visibility>(visibilityText, ignoreCase: true, out var visibility))
            {
                throw new InvalidDataException($"Unknown visibility '{visibilityText}' on class '{fullName}'");
            }

            // 6e-Step D-c：delegate 标记（紧随 visibility；读侧重建 TypeKind.Delegate，Invoke 由 fn owner 挂到 Methods）
            var isDelegateKind = false;
            if (_tokens.PeekRaw().StartsWith("tk:", StringComparison.Ordinal))
            {
                isDelegateKind = _tokens.ReadLabeledField("tk:").Equals("delegate", StringComparison.Ordinal);
            }

            // 6e-G7/M0-1a：接口位 + 实现接口列表（向后兼容：旧版 .coa 无 iface 字段 → 默认非接口、无实现）
            var isInterface = false;
            var interfaceRefs = new string[0];
            if (_tokens.PeekRaw().StartsWith("iface:", StringComparison.Ordinal))
            {
                isInterface = _tokens.ParseBoolWord(_tokens.ReadLabeledField("iface:"));
                var ifaceCount = _tokens.ReadCountField("ifaces:");
                interfaceRefs = new string[ifaceCount];
                for (var i = 0; i < ifaceCount; i++)
                {
                    interfaceRefs[i] = _tokens.ExpectString();
                }
            }

            // N1：值类型位（旧版 .coa 无此字段 → 默认非 struct）
            var isStruct = false;
            if (_tokens.PeekRaw().StartsWith("struct:", StringComparison.Ordinal))
            {
                isStruct = _tokens.ParseBoolWord(_tokens.ReadLabeledField("struct:"));
            }

            // 6e-M32：`[Facade("X")]` 显式 BCL 目标（常写；`-` 表无）
            string? bclTargetName = null;
            if (_tokens.PeekRaw().StartsWith("bclTarget:", StringComparison.Ordinal))
            {
                var text = _tokens.ReadLabeledField("bclTarget:");
                if (text != "-")
                {
                    bclTargetName = text;
                }
            }

            // 6e-M33：显式基类（非 Object）引用——base 类声明可能晚于子类，延迟到全类注册后 pass2 解析；常写，`-` 表无
            string? baseTypeRef = null;
            if (_tokens.PeekRaw().StartsWith("base:", StringComparison.Ordinal))
            {
                var text = _tokens.ReadLabeledField("base:");
                if (text != "-")
                {
                    baseTypeRef = text;
                }
            }

            var methodCount = _tokens.ReadCountField("methods:");
            // 方法名仅供阅读，方法符号由各自 fn 条目的 owner 字段回填；
            // 接口方法无 fn 条目，须从这里的完整签名（Name[params]:Return）重建符号。
            var methodSignatureTexts = new string[methodCount];
            for (var i = 0; i < methodCount; i++)
            {
                methodSignatureTexts[i] = _tokens.ExpectString();
            }

            var classType = new NamedTypeSymbol(name, ns, visibility, declaration: null);
            classType.ContainingLibrary = _context.ModuleName;
            classType.FacadeBclTargetName = bclTargetName;
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
            else if (isStruct)
            {
                classType.TypeKind = TypeKind.Struct;
            }

            foreach (var interfaceRef in interfaceRefs)
            {
                classType.AddInterface((NamedTypeSymbol)_resolver.ResolveTypeRef(interfaceRef));
            }

            _context.Classes.Add(classType);
            _context.GenericDefinitions.Add(classType);
            _context.AddNamedType(fullName, classType);
            if (baseTypeRef != null)
            {
                _context.PendingBaseTypes.Add((classType, baseTypeRef));
            }

            // N1：接口成员符号重建——接口方法无 fn 条目（无方法体），从 methods: 完整签名恢复，
            // 否则库侧接口为空壳，消费方实现/成员解析（如 System.IDisposable.Dispose）失败。
            if (isInterface && methodSignatureTexts.Length > 0)
            {
                foreach (var signature in methodSignatureTexts)
                {
                    var method = ParseInterfaceMethodSignature(signature, classType);
                    if (method != null)
                    {
                        classType.AddMethod(method);
                    }
                }
            }

            // 6e-Step D-a：类字段（含闭包环境类 __Env_* 捕获实例成员）解析回填——与写侧 fields:/methods: 顺序一致
            if (_tokens.PeekRaw().StartsWith("fields:", StringComparison.Ordinal))
            {
                var classFieldCount = _tokens.ReadCountField("fields:");
                for (var i = 0; i < classFieldCount; i++)
                {
                    _tokens.Expect("fld");
                    var fieldName = CoaText.Unescape(_tokens.ExpectString());
                    var fieldTypeRef = _tokens.ExpectString();
                    var fieldVisibilityText = _tokens.ExpectString();
                    if (!Enum.TryParse<Visibility>(fieldVisibilityText, ignoreCase: true, out var fieldVisibility))
                    {
                        throw new InvalidDataException($"Unknown visibility '{fieldVisibilityText}' on field '{fullName}.{fieldName}'");
                    }

                    var isStatic = _tokens.ParseBoolWord(_tokens.ExpectString());
                    var isReadonly = _tokens.ParseBoolWord(_tokens.ExpectString());
                    _context.PendingFields.Add((classType, fieldName, fieldTypeRef, fieldVisibility, isReadonly, isStatic));
                    _tokens.End();
                }
            }

            // 6e-Step D-b：事件声明读回（handler 解析为 FunctionTypeSymbol）
            if (_tokens.PeekRaw().StartsWith("events:", StringComparison.Ordinal))
            {
                var eventCount = _tokens.ReadCountField("events:");
                for (var i = 0; i < eventCount; i++)
                {
                    _tokens.Expect("evt");
                    var eventName = CoaText.Unescape(_tokens.ExpectString());
                    var handlerType = (FunctionTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                    var eventVisibilityText = _tokens.ExpectString();
                    if (!Enum.TryParse<Visibility>(eventVisibilityText, ignoreCase: true, out var eventVisibility))
                    {
                        throw new InvalidDataException($"Unknown visibility '{eventVisibilityText}' on event '{fullName}.{eventName}'");
                    }

                    classType.AddEvent(new EventSymbol(eventName, handlerType, eventVisibility, classType));
                    _tokens.End();
                }
            }

            // 6b：facade 实例类属性声明读回（访问器 fns 读毕后回填挂接）
            if (_tokens.PeekRaw().StartsWith("props:", StringComparison.Ordinal))
            {
                var propertyCount = _tokens.ReadCountField("props:");
                for (var i = 0; i < propertyCount; i++)
                {
                    _tokens.Expect("prop");
                    var propertyName = CoaText.Unescape(_tokens.ExpectString());
                    var propertyTypeRef = _tokens.ExpectString();
                    var hasGet = _tokens.ParseBoolWord(_tokens.ExpectString());
                    var hasSet = _tokens.ParseBoolWord(_tokens.ExpectString());
                    if (!Enum.TryParse<Visibility>(_tokens.ExpectString(), ignoreCase: true, out var propertyVisibility))
                    {
                        propertyVisibility = Visibility.Public;
                    }

                    var isStatic = _tokens.ParseBoolWord(_tokens.ExpectString());
                    _context.PendingProperties.Add((classType, propertyName, propertyTypeRef, hasGet, hasSet, propertyVisibility, isStatic));
                    _tokens.End();
                }
            }

            _tokens.End();
        }

        /// <summary>
        /// 泛型定义类读取（6e-G7 S1）：重建 IsGenericDefinition 壳 + 类型参数（含约束，两趟——约束可引用兄弟参数）+
        /// 字段；静态方法签名仅作清单，符号由各自 fn 条目 owner 回填。
        /// 开放类型参数按限定键 `!属主全名.名` 注册进文件级表，后续 fn/bodies 的类型引用据此解析。
        /// </summary>
        private void ReadGenericClass()
        {
            var fullName = _tokens.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var visibilityText = _tokens.ExpectString();
            if (!Enum.TryParse<Visibility>(visibilityText, ignoreCase: true, out var visibility))
            {
                throw new InvalidDataException($"Unknown visibility '{visibilityText}' on generic class '{fullName}'");
            }

            // 6e-G7/M0-1a：接口位 + 实现接口列表（开放参数引用须待 tpar 注册后解析，见本方法尾部；旧版 .coa 缺字段则默认）
            var isInterface = false;
            var interfaceRefs = new string[0];
            if (_tokens.PeekRaw().StartsWith("iface:", StringComparison.Ordinal))
            {
                isInterface = _tokens.ParseBoolWord(_tokens.ReadLabeledField("iface:"));
                var ifaceCount = _tokens.ReadCountField("ifaces:");
                interfaceRefs = new string[ifaceCount];
                for (var i = 0; i < ifaceCount; i++)
                {
                    interfaceRefs[i] = _tokens.ExpectString();
                }
            }

            // N1：泛型 struct 值类型位（旧版 .coa 无此字段 → 默认非 struct）
            var isStruct = false;
            if (_tokens.PeekRaw().StartsWith("struct:", StringComparison.Ordinal))
            {
                isStruct = _tokens.ParseBoolWord(_tokens.ReadLabeledField("struct:"));
            }

            var typeParameterCount = _tokens.ReadCountField("tparams:");
            var classType = new NamedTypeSymbol(name, ns, visibility, declaration: null);
            classType.ContainingLibrary = _context.ModuleName;
            classType.BaseType = NamedTypeSymbol.SystemObject;

            var pendingConstraints = new (TypeParameterSymbol Parameter, string[] ConstraintRefs)[typeParameterCount];
            for (var i = 0; i < typeParameterCount; i++)
            {
                _tokens.Expect("tpar");
                var parameterName = CoaText.Unescape(_tokens.ExpectString());
                var ordinal = _tokens.ExpectInt();
                var flagsText = _tokens.ExpectString();
                var varianceText = "-";
                if (_tokens.PeekRaw().StartsWith("v:", StringComparison.Ordinal))
                {
                    varianceText = _tokens.ExpectString();
                }

                var constraintCount = _tokens.ReadCountField("c:");

                var parameter = new TypeParameterSymbol(parameterName, ordinal, classType);
                if (varianceText == "-")
                {
                    varianceText = "v:-";
                }

                ApplyVarianceFlag(parameter, varianceText);
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
                _context.OpenTypeParametersByKey["!" + fullName + "." + parameterName] = parameter;

                // 约束原文引用在 pass1 读入以推进到下一 tpar（多 tpar 须消费本 tpar 尾部）；
                // pass2 待兄弟参数注册后再 ResolveTypeRef 解析（!限定键可解析）。
                var constraintRefs = new string[constraintCount];
                for (var c = 0; c < constraintCount; c++)
                {
                    constraintRefs[c] = _tokens.ExpectString();
                }

                _tokens.End();
                pendingConstraints[i] = (parameter, constraintRefs);
            }

            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
            for (var i = 0; i < typeParameterCount; i++)
            {
                var (parameter, constraintRefs) = pendingConstraints[i];
                if (constraintRefs.Length > 0)
                {
                    parameter.ConstraintTypes = constraintRefs.Select(r => _resolver.ResolveTypeRef(r)).ToImmutableArray();
                }
            }

            var fieldCount = _tokens.ReadCountField("fields:");
            for (var i = 0; i < fieldCount; i++)
            {
                _tokens.Expect("fld");
                var fieldName = CoaText.Unescape(_tokens.ExpectString());
                var fieldTypeRef = _tokens.ExpectString();
                var fieldVisibilityText = _tokens.ExpectString();
                if (!Enum.TryParse<Visibility>(fieldVisibilityText, ignoreCase: true, out var fieldVisibility))
                {
                    throw new InvalidDataException($"Unknown visibility '{fieldVisibilityText}' on field '{fullName}.{fieldName}'");
                }

                var isStatic = _tokens.ParseBoolWord(_tokens.ExpectString());
                var isReadonly = _tokens.ParseBoolWord(_tokens.ExpectString());
                _context.PendingFields.Add((classType, fieldName, fieldTypeRef, fieldVisibility, isReadonly, isStatic));
                _tokens.End();
            }

            var methodCount = _tokens.ReadCountField("methods:");
            // 方法名仅供阅读，方法符号由各自 fn 条目的 owner 字段回填；
            // 接口方法无 fn 条目，须从完整签名（Name[params]:Return）重建符号。
            var methodSignatureTexts = new string[methodCount];
            for (var i = 0; i < methodCount; i++)
            {
                methodSignatureTexts[i] = _tokens.ExpectString();
            }

            // 6e-G7/M0-1a：接口位回填 + 实现接口列表回填（tpar 已注册，开放参数引用可解）
            if (isInterface)
            {
                classType.TypeKind = TypeKind.Interface;
            }
            else if (isStruct)
            {
                classType.TypeKind = TypeKind.Struct;
            }

            foreach (var interfaceRef in interfaceRefs)
            {
                classType.AddInterface((NamedTypeSymbol)_resolver.ResolveTypeRef(interfaceRef));
            }

            // N1：接口成员符号重建（泛型接口如 System.Collections.Generic.IEnumerable<T>）
            if (isInterface && methodSignatureTexts.Length > 0)
            {
                foreach (var signature in methodSignatureTexts)
                {
                    var method = ParseInterfaceMethodSignature(signature, classType);
                    if (method != null)
                    {
                        classType.AddMethod(method);
                    }
                }
            }

            // 6e 跨库里程碑：gcls 一律只入 GenericDefinitions，不入 Classes——否则 CoaLibraryCompiler 生成
            // Managed dll 时把开放类型参数类当普通类发射（IL Unexpected type K）。类型注入经 GenericDefinitions。
            _context.GenericDefinitions.Add(classType);
            _context.AddNamedType(fullName, classType);
            // N1：同名不同元数的泛型定义（ValueTuple<T1>..<T1..T7>）——补 backtick 元数键，
            // 避免同名键互相覆盖导致 `定义`元数` 实例化 mangle 无法反解
            _context.AddNamedType(fullName + "`" + classType.TypeParameters.Length, classType);

            // 6e-Step D-b：泛型定义类事件声明读回
            if (_tokens.PeekRaw().StartsWith("events:", StringComparison.Ordinal))
            {
                var eventCount = _tokens.ReadCountField("events:");
                for (var i = 0; i < eventCount; i++)
                {
                    _tokens.Expect("evt");
                    var eventName = CoaText.Unescape(_tokens.ExpectString());
                    var handlerType = (FunctionTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                    var eventVisibilityText = _tokens.ExpectString();
                    if (!Enum.TryParse<Visibility>(eventVisibilityText, ignoreCase: true, out var eventVisibility))
                    {
                        throw new InvalidDataException($"Unknown visibility '{eventVisibilityText}' on event '{fullName}.{eventName}'");
                    }

                    classType.AddEvent(new EventSymbol(eventName, handlerType, eventVisibility, classType));
                    _tokens.End();
                }
            }

            // 6e 跨库里程碑：泛型定义类属性声明解析（访问器 `get_X`/`set_X` 为独立 fn，读毕后回填挂接）。
            if (_tokens.PeekRaw().StartsWith("props:", StringComparison.Ordinal))
            {
                var propertyCount = _tokens.ReadCountField("props:");
                for (var i = 0; i < propertyCount; i++)
                {
                    _tokens.Expect("prop");
                    var propertyName = CoaText.Unescape(_tokens.ExpectString());
                    var propertyTypeRef = _tokens.ExpectString();
                    var hasGet = _tokens.ParseBoolWord(_tokens.ExpectString());
                    var hasSet = _tokens.ParseBoolWord(_tokens.ExpectString());
                    if (!Enum.TryParse<Visibility>(_tokens.ExpectString(), ignoreCase: true, out var propertyVisibility))
                    {
                        propertyVisibility = Visibility.Public;
                    }

                    var isStatic = _tokens.ParseBoolWord(_tokens.ExpectString());
                    _context.PendingProperties.Add((classType, propertyName, propertyTypeRef, hasGet, hasSet, propertyVisibility, isStatic));
                    _tokens.End();
                }
            }

            _tokens.End();
        }

        private void ReadFunction()
        {
            var key = _tokens.ExpectString();
            var name = _tokens.ReadLabeledField("name:");

            // 6e-G7 S1：方法级类型参数（顶层泛型函数，裸键 !名）——先注册再解析 ret/par 的类型引用
            var typeParameters = ImmutableArray<TypeParameterSymbol>.Empty;
            if (_tokens.PeekRaw().StartsWith("tps:", StringComparison.Ordinal))
            {
                var tpsHeader = _tokens.ExpectString();
                if (!int.TryParse(tpsHeader.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tpsCount))
                {
                    throw new InvalidDataException($"Malformed 'tps:' count '{tpsHeader}' on function '{name}'");
                }

                var builder = ImmutableArray.CreateBuilder<TypeParameterSymbol>(tpsCount);
                var deferred = new List<(TypeParameterSymbol Parameter, int ConstraintCount)>(tpsCount);
                for (var i = 0; i < tpsCount; i++)
                {
                    var (parameter, constraintCount) = ReadTypeParameter(ownerFullName: null);
                    builder.Add(parameter);
                    deferred.Add((parameter, constraintCount));
                }

                foreach (var (parameter, constraintCount) in deferred)
                {
                    ResolveDeferredConstraints(parameter, constraintCount);
                }

                typeParameters = builder.ToImmutable();
            }

            var returnType = _resolver.ResolveTypeRef(_tokens.ReadLabeledField("ret:"));
            var nsText = _tokens.ReadLabeledField("ns:");
            var ownerText = _tokens.ReadLabeledField("owner:");
            var isExtern = _tokens.ParseBoolWord(_tokens.ReadLabeledField("extern:"));
            var dllText = _tokens.ReadLabeledField("dll:");
            var ccText = _tokens.ReadLabeledField("cc:");
            var builtinText = _tokens.ReadLabeledField("builtin:");
            var entryText = _tokens.ReadLabeledField("entry:");
            var charSetText = _tokens.ReadLabeledField("charset:");

            // 6e-M32 Tier-2：函数级 attribute（库跨边界携带；逗号分隔属性类名，`-`/旧文件缺席 → 缺省空）
            var attributeNames = new List<string>();
            if (_tokens.PeekRaw().StartsWith("attrs:", StringComparison.Ordinal))
            {
                var attrsText = _tokens.ReadLabeledField("attrs:");
                if (attrsText != "-")
                {
                    attributeNames.AddRange(attrsText.Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            // 6e-G7 S2：属主方法的显式静态/构造/访问器位（旧文件无此字段，按默认：容器类全静态推断）
            bool? explicitIsStatic = null;
            var explicitIsConstructor = false;
            var explicitIsAccessor = false;
            var explicitIsVirtual = false;
            var explicitIsAbstract = false;
            var explicitIsOverride = false;
            var explicitIsSealed = false;
            if (_tokens.PeekRaw().StartsWith("static:", StringComparison.Ordinal))
            {
                explicitIsStatic = _tokens.ParseBoolWord(_tokens.ReadLabeledField("static:"));
                explicitIsConstructor = _tokens.ParseBoolWord(_tokens.ReadLabeledField("ctor:"));
                explicitIsAccessor = _tokens.ParseBoolWord(_tokens.ReadLabeledField("acc:"));

                // 6e-M25 阶段 5：虚/抽象/重写/密封位（旧文件无此字段 → 默认 false 兼容）
                if (_tokens.PeekRaw().StartsWith("virt:", StringComparison.Ordinal))
                {
                    explicitIsVirtual = _tokens.ParseBoolWord(_tokens.ReadLabeledField("virt:"));
                    explicitIsAbstract = _tokens.ParseBoolWord(_tokens.ReadLabeledField("abs:"));
                    explicitIsOverride = _tokens.ParseBoolWord(_tokens.ReadLabeledField("ovr:"));
                    explicitIsSealed = _tokens.ParseBoolWord(_tokens.ReadLabeledField("seal:"));
                }
            }

            // 6f-4：捕获闭包元数据（旧文件无此字段 → 缺省非 lambda/无 env/无捕获）
            var isLambdaWithEnvironment = false;
            var isLambda = false;
            NamedTypeSymbol? environmentClass = null;
            var capturedKeys = new List<string>();
            if (_tokens.PeekRaw().StartsWith("envn:", StringComparison.Ordinal))
            {
                isLambdaWithEnvironment = _tokens.ParseBoolWord(_tokens.ReadLabeledField("envn:"));
                isLambda = _tokens.ParseBoolWord(_tokens.ReadLabeledField("envl:"));
                var envcText = _tokens.ReadLabeledField("envc:");
                environmentClass = envcText == "-" ? null : (NamedTypeSymbol)_resolver.ResolveTypeRef(envcText);
                var envcapCount = _tokens.ReadCountField("envcap:");
                for (var i = 0; i < envcapCount; i++)
                {
                    capturedKeys.Add(_tokens.ExpectString());
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

            var containingClass = ownerText == "-" ? null : _resolver.ResolveOwnerClass(ownerText);

            var paramCount = _tokens.ReadCountField("params:");
            var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
            for (var i = 0; i < paramCount; i++)
            {
                _tokens.Expect("par");
                var pKey = _tokens.ExpectString();
                var pName = CoaText.Unescape(_tokens.ExpectString());
                var pType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                var ordinal = _tokens.ExpectInt();

                // 6e-M23 R8：第 5 个 token = out:/ref:/-（兼容旧文件裸 out/ref 与缺省 "-"）。
                var isOut = false;
                var isRef = false;
                var modifierText = _tokens.PeekRaw();
                if (modifierText is "out:" or "ref:" or "out" or "ref" or "-")
                {
                    _tokens.ExpectString();
                    isOut = modifierText is "out:" or "out";
                    isRef = modifierText is "ref:" or "ref";
                }

                var isThis = false;
                var thisText = _tokens.PeekRaw();
                if (thisText is "this" or "-")
                {
                    _tokens.ExpectString();
                    isThis = thisText == "this";
                }

                var parameter = new ParameterSymbol(pName, pType, ordinal, isOut, isRef, isThis);
                parameters.Add(parameter);
                _context.VariablesByKey[pKey] = parameter;
                _tokens.End();
            }

            // 6e-M19 M2-c：Object 内建方法复用单例（保持符号同一性，发射器按 BuiltinKind 分发）。
            if (containingClass != null && builtinKind != null && SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                var singleton = SystemObjectMembers.GetByKind(builtinKind.Value);
                if (singleton != null)
                {
                    _context.Functions.Add(singleton);
                    _context.FunctionsByKey[key] = singleton;
                    _context.LocalFunctionKeys[key] = singleton;
                    _tokens.End();
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
            function.ContainingLibrary = _resolver.ExtractLibraryFromKey(key);

            // 6e-M32 Tier-2：恢复函数级 attribute（轻量占位 AttributeSymbol：仅名，实参留空）
            if (attributeNames.Count > 0)
            {
                function.Attributes = attributeNames
                    .Select(n => new AttributeSymbol(new NamedTypeSymbol(n, "", Visibility.Public, declaration: null), ImmutableArray<(TypeSymbol, object)>.Empty))
                    .ToImmutableArray();
            }

            _context.Functions.Add(function);
            _context.FunctionsByKey[key] = function;
            _context.LocalFunctionKeys[key] = function;

            // 6e-G7 S1：方法级类型参数回填（顶层泛型函数）
            if (typeParameters.Length > 0)
            {
                function.TypeParameters = typeParameters;
            }

            // 6e-G7 S2 + 6b：属主方法按显式位还原（泛型定义/facade 实例类显式区分；容器类隐含全静态）
            if (containingClass != null && !SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                function.IsStatic = explicitIsStatic ?? true;
                if (explicitIsStatic.HasValue)
                {
                    function.IsConstructor = explicitIsConstructor;
                    function.IsPropertyAccessor = explicitIsAccessor;
                    function.IsVirtual = explicitIsVirtual;
                    function.IsAbstract = explicitIsAbstract;
                    function.IsOverride = explicitIsOverride;
                    function.IsSealed = explicitIsSealed;
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

                _context.PendingClosures.Add((function, isLambdaWithEnvironment, environmentClass, capturedKeys));
            }

            _tokens.End();
        }

        private void ReadVariable(bool isGlobal)
        {
            var key = _tokens.ExpectString();
            var isReadOnly = _tokens.ParseBoolWord(_tokens.ExpectString());
            var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
            BoundConstant? constant = null;

            if (_tokens.PeekRaw() == "(")
            {
                _tokens.Expect("const");
                var encoded = _tokens.ExpectString();
                var value = CoaText.DecodeValue(encoded);
                constant = new BoundConstant(value);
                _tokens.End();
            }

            var name = _resolver.KeyToName(key);
            VariableSymbol variable = isGlobal
                ? new GlobalVariableSymbol(name, isReadOnly, type, constant)
                : new LocalVariableSymbol(name, isReadOnly, type, constant);

            if (isGlobal)
            {
                _context.Globals.Add((GlobalVariableSymbol)variable);
            }

            _context.VariablesByKey[key] = variable;
            _tokens.End();
        }

        private void ReadBodies(ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder bodies)
        {
            while (_tokens.TryExpect(out var kind) && kind == "body")
            {
                var fnKey = _tokens.ExpectString();
                if (!_context.FunctionsByKey.TryGetValue(fnKey, out var function))
                {
                    throw new InvalidDataException($"Unknown function '{fnKey}' in bodies");
                }

                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                var body = (BoundBlockStatement)ReadStatement(labels);

                if (function.IsExtern)
                {
                    body = new BoundBlockStatement(NoSyntax, ImmutableArray<BoundStatement>.Empty);
                }

                bodies[function] = body;
                _tokens.End();
            }

            _tokens.End();
        }

        // ---------------------------------------------------------------- read: type parameters

        /// <summary>
        /// tpar/ftp 子节点读取（6e-G7 S1）：构造符号 + 应用标志 + 登记开放键（类级限定键 !属主.名；
        /// 方法级裸键 !名）+ 暂存约束数。返回 (参数, 约束数)，约束由第二趟解析。
        /// </summary>
        private (TypeParameterSymbol Parameter, int ConstraintCount) ReadTypeParameter(string? ownerFullName)
        {
            _tokens.Expect("tpar");
            var parameterName = CoaText.Unescape(_tokens.ExpectString());
            var ordinal = _tokens.ExpectInt();
            var flagsText = _tokens.ExpectString();
            var varianceText = "-";
            if (_tokens.PeekRaw().StartsWith("v:", StringComparison.Ordinal))
            {
                varianceText = _tokens.ExpectString();
            }

            var constraintCount = _tokens.ReadCountField("c:");

            var parameter = new TypeParameterSymbol(parameterName, ordinal, owningClass: null);
            ApplyTypeParameterFlags(parameter, flagsText);
            ApplyVarianceFlag(parameter, varianceText);

            var openKey = ownerFullName == null
                ? "!" + parameterName
                : "!" + ownerFullName + "." + parameterName;
            _context.OpenTypeParametersByKey[openKey] = parameter;

            return (parameter, constraintCount);
        }

        /// <summary>约束第二趟：兄弟参数已全部注册后解析显式约束类型。</summary>
        private void ResolveDeferredConstraints(TypeParameterSymbol parameter, int constraintCount)
        {
            if (constraintCount == 0)
            {
                _tokens.End();
                return;
            }

            var constraints = ImmutableArray.CreateBuilder<TypeSymbol>(constraintCount);
            for (var c = 0; c < constraintCount; c++)
            {
                constraints.Add(_resolver.ResolveTypeRef(_tokens.ExpectString()));
            }

            parameter.ConstraintTypes = constraints.ToImmutable();
            _tokens.End();
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

        /// <summary>型变注解位解析（6e-M22 委托真实类型化）：`v:in` / `v:out` / `v:-`；未知值报错。</summary>
        private static void ApplyVarianceFlag(TypeParameterSymbol parameter, string varianceText)
        {
            switch (varianceText)
            {
                case "v:-":
                case "-":
                    parameter.Variance = VarianceKind.Invariant;
                    break;
                case "v:in":
                case "in":
                    parameter.Variance = VarianceKind.In;
                    break;
                case "v:out":
                case "out":
                    parameter.Variance = VarianceKind.Out;
                    break;
                default:
                    throw new InvalidDataException($"Unknown variance flag '{varianceText}'");
            }
        }

        // ---------------------------------------------------------------- read: interface signatures

        /// <summary>解析接口方法完整签名 `Name[params]:Return` → FunctionSymbol（接口方法无 fn 条目承载时的成员重建）。</summary>
        private FunctionSymbol? ParseInterfaceMethodSignature(string signature, NamedTypeSymbol classType)
        {
            try
            {
                var openBracket = signature.IndexOf('[');
                string retText;
                string paramsText;
                string name;
                if (openBracket >= 0)
                {
                    var closeBracket = signature.LastIndexOf(']');
                    var sep = signature.IndexOf(':', closeBracket + 1);
                    if (sep < 0)
                    {
                        return null;
                    }

                    name = signature.Substring(0, openBracket);
                    paramsText = signature.Substring(openBracket + 1, closeBracket - openBracket - 1);
                    retText = signature.Substring(sep + 1);
                }
                else
                {
                    var sep = signature.IndexOf(':');
                    if (sep < 0)
                    {
                        return null;
                    }

                    name = signature.Substring(0, sep);
                    retText = signature.Substring(sep + 1);
                    paramsText = "";
                }

                var returnType = _resolver.ResolveTypeRef(retText);
                var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
                if (paramsText.Length > 0)
                {
                    var ordinal = 0;
                    foreach (var raw in SplitInterfaceMethodParams(paramsText))
                    {
                        var isOut = raw.StartsWith("out:", StringComparison.Ordinal);
                        var isRef = raw.StartsWith("ref:", StringComparison.Ordinal);
                        var isNone = raw.StartsWith("-", StringComparison.Ordinal);
                        var typeText = isOut ? raw.Substring(4) : isRef ? raw.Substring(4) : isNone ? raw.Substring(1) : raw;
                        parameters.Add(new ParameterSymbol("p" + ordinal, _resolver.ResolveTypeRef(typeText), ordinal, isOut, isRef, isThis: false));
                        ordinal++;
                    }
                }

                return new FunctionSymbol(name, parameters.ToImmutable(), returnType, containingClass: classType);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>签名参数列表按顶层逗号切分（忽略方括号/开放形参引用内的逗号）。</summary>
        private static IEnumerable<string> SplitInterfaceMethodParams(string paramsText)
        {
            var depth = 0;
            var current = new StringBuilder();
            foreach (var ch in paramsText)
            {
                switch (ch)
                {
                    case '[':
                    case ']':
                    case '!':
                        depth++;
                        current.Append(ch);
                        break;
                    case ',' when depth == 0:
                        yield return current.ToString();
                        current.Length = 0;
                        break;
                    default:
                        current.Append(ch);
                        break;
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        // ---------------------------------------------------------------- read: bodies

        private BoundStatement ReadStatement(Dictionary<string, BoundLabel> labels)
        {
            var kind = _tokens.ExpectKind();
            var statement = ReadStatementFromToken(kind, labels);
            _tokens.End();
            return statement;
        }

        private BoundStatement ReadStatementFromToken(string kind, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "block":
                    {
                        var count = _tokens.ExpectInt();
                        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                        for (var i = 0; i < count; i++)
                        {
                            statements.Add(ReadStatement(labels));
                        }

                        return new BoundBlockStatement(NoSyntax, statements.ToImmutable());
                    }
                case "nop":
                    return new BoundNopStatement(NoSyntax);
                case "vardecl":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var initializer = ReadExpression(labels);
                        return new BoundVariableDeclaration(NoSyntax, variable, initializer);
                    }
                case "if":
                    {
                        var condition = ReadExpression(labels);
                        var then = ReadStatement(labels);
                        var elseStatement = ReadNullableStatement(labels);
                        return new BoundIfStatement(NoSyntax, condition, then, elseStatement);
                    }
                case "while":
                    {
                        var condition = ReadExpression(labels);
                        var body = ReadStatement(labels);
                        var breakLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var continueLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        return new BoundWhileStatement(NoSyntax, condition, body, breakLabel, continueLabel);
                    }
                case "dowhile":
                    {
                        var body = ReadStatement(labels);
                        var condition = ReadExpression(labels);
                        var breakLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var continueLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        return new BoundDoWhileStatement(NoSyntax, body, condition, breakLabel, continueLabel);
                    }
                case "for":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var lowerBound = ReadExpression(labels);
                        var upperBound = ReadExpression(labels);
                        var step = ReadNullableExpression(labels);
                        var body = ReadStatement(labels);
                        var breakLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var continueLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        return new BoundForRangeStatement(NoSyntax, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
                    }
                case "label":
                    return new BoundLabelStatement(NoSyntax, GetLabel(labels, CoaText.Unescape(_tokens.ExpectString())));
                case "goto":
                    return new BoundGotoStatement(NoSyntax, GetLabel(labels, CoaText.Unescape(_tokens.ExpectString())));
                case "cgoto":
                    {
                        var label = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var condition = ReadExpression(labels);
                        var jumpIfTrue = _tokens.ParseBoolWord(_tokens.ExpectString());
                        return new BoundConditionalGotoStatement(NoSyntax, label, condition, jumpIfTrue);
                    }
                case "return":
                    {
                        var expression = ReadNullableExpression(labels);
                        return new BoundReturnStatement(NoSyntax, expression);
                    }
                case "exprstmt":
                    {
                        var expression = ReadExpression(labels);
                        return new BoundExpressionStatement(NoSyntax, expression);
                    }
                default:
                    throw new InvalidDataException($"Unknown statement kind '{kind}'");
            }
        }

        private BoundStatement? ReadNullableStatement(Dictionary<string, BoundLabel> labels)
        {
            if (_tokens.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var statement = ReadStatementFromToken(token, labels);
            _tokens.End();
            return statement;
        }

        private BoundExpression? ReadNullableExpression(Dictionary<string, BoundLabel> labels)
        {
            if (_tokens.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var expression = ReadExpressionFromToken(token, labels);
            _tokens.End();
            return expression;
        }

        private BoundExpression ReadExpression(Dictionary<string, BoundLabel> labels)
        {
            var token = _tokens.ExpectKind();
            var expression = ReadExpressionFromToken(token, labels);
            _tokens.End();
            return expression;
        }

        private BoundExpression ReadExpressionFromToken(string kind, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "lit":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var encoded = _tokens.ExpectString();
                        var value = CoaText.DecodeValue(encoded);
                        return new BoundLiteralExpression(NoSyntax, value, type);
                    }
                case "var":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        return new BoundVariableExpression(NoSyntax, variable);
                    }
                case "assign":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundAssignmentExpression(NoSyntax, variable, expression);
                    }
                case "cassign":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var op = ReadBinaryOperator();
                        var expression = ReadExpression(labels);
                        return new BoundCompoundAssignmentExpression(NoSyntax, variable, op, expression);
                    }
                case "unary":
                    {
                        var op = ReadUnaryOperator();
                        var operand = ReadExpression(labels);
                        return new BoundUnaryExpression(NoSyntax, op, operand);
                    }
                case "binary":
                    {
                        var op = ReadBinaryOperator();
                        var left = ReadExpression(labels);
                        var right = ReadExpression(labels);
                        return new BoundBinaryExpression(NoSyntax, left, op, right);
                    }
                case "cond":
                    {
                        var condition = ReadExpression(labels);
                        var whenTrue = ReadExpression(labels);
                        var whenFalse = ReadExpression(labels);
                        return new BoundConditionalExpression(NoSyntax, condition, whenTrue, whenFalse);
                    }
                case "call":
                    {
                        var function = _resolver.ResolveFunction(_tokens.ExpectString());
                        var count = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundCallExpression(NoSyntax, function, arguments.ToImmutable());
                    }
                case "byrefarg":
                    {
                        // 6e-M23 R8：out/ref 实参包装（内层为可赋值 lvalue）。
                        var modifier = _tokens.ExpectString();
                        var expression = ReadExpression(labels);
                        return new BoundByRefArgument(NoSyntax, expression, isRef: modifier is "ref" or "ref:");
                    }
                case "conv":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundConversionExpression(NoSyntax, type, expression);
                    }
                case "istype":
                    {
                        var targetType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundIsExpression(NoSyntax, expression, targetType);
                    }
                case "astype":
                    {
                        var targetType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundAsExpression(NoSyntax, expression, targetType);
                    }
                case "arrnew":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var length = ReadExpression(labels);
                        var count = _tokens.ExpectInt();
                        var initializers = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            initializers.Add(ReadExpression(labels));
                        }

                        return new BoundArrayCreationExpression(NoSyntax, type, length, initializers.ToImmutable());
                    }
                case "objnew":
                    {
                        var type = (NamedTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        var argCount = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < argCount; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundObjectCreationExpression(NoSyntax, type, arguments.ToImmutable());
                    }
                case "elem":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var target = ReadExpression(labels);
                        var index = ReadExpression(labels);
                        return new BoundElementAccessExpression(NoSyntax, type, target, index);
                    }
                case "elemassign":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var target = (BoundElementAccessExpression)ReadExpression(labels);
                        var expression = ReadExpression(labels);
                        return new BoundElementAssignmentExpression(NoSyntax, type, target, expression);
                    }
                case "memberacc":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var identifier = CoaText.Unescape(_tokens.ExpectString());

                        // 6e-G7 S2：owner 字段可选携带——回填 FieldSymbol（实例化类型的 Fields 经物化钩子可达）
                        FieldSymbol? field = null;
                        var hasOwner = _tokens.PeekRaw().StartsWith("owner:", StringComparison.Ordinal);
                        if (hasOwner)
                        {
                            var ownerFullName = _tokens.ReadLabeledField("owner:");
                            if (_resolver.ResolveNamedType(ownerFullName) is NamedTypeSymbol ownerClass)
                            {
                                field = ownerClass.Fields.FirstOrDefault(f => f.Name == identifier);
                            }
                        }

                        var target = ReadExpression(labels);
                        return new BoundMemberAccessExpression(NoSyntax, type, target, identifier, field);
                    }
                case "memberassign":
                    {
                        // 6e-G7 S2：字段赋值读回——Field 按 target 形态 + 名字解析
                        var target = ReadExpression(labels);
                        var fieldName = CoaText.Unescape(_tokens.ReadLabeledField("name:"));
                        _ = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        _ = _tokens.ParseBoolWord(_tokens.ExpectString());
                        var value = ReadExpression(labels);

                        FieldSymbol? field = target switch
                        {
                            // 6e-G7：隐式 this 赋值（`_value = v`）——字段在 this 的类上
                            BoundThisExpression thisExpression => ((NamedTypeSymbol)thisExpression.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            BoundMemberAccessExpression access => access.Field,
                            BoundStaticTypeExpression staticType => ((NamedTypeSymbol)staticType.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            // 6e-M25：局部/参数（如 `win.Field = v`）——字段在变量的静态类上
                            BoundVariableExpression variable when variable.Type is NamedTypeSymbol variableType => variableType.Fields.FirstOrDefault(f => f.Name == fieldName),
                            _ => null,
                        };

                        if (field == null)
                        {
                            throw new InvalidDataException($"Unknown field '{fieldName}' in memberassign");
                        }

                        return new BoundMemberAssignmentExpression(NoSyntax, target, field, value);
                    }
                case "membercall":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var identifier = CoaText.Unescape(_tokens.ExpectString());
                        var methodToken = _tokens.ExpectString();
                        var method = methodToken == "-" ? null : _resolver.ResolveFunction(methodToken);
                        var count = _tokens.ExpectInt();
                        var target = ReadExpression(labels);
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundMemberCallExpression(NoSyntax, target, identifier, arguments.ToImmutable(), type, method);
                    }
                case "statictype":
                    {
                        var type = (NamedTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        return new BoundStaticTypeExpression(NoSyntax, type);
                    }
                case "ctorchain":
                    {
                        var kindText = _tokens.ExpectString();
                        var constructorKey = _tokens.ExpectString();
                        FunctionSymbol? constructor = constructorKey == "-" ? null : _resolver.ResolveFunction(constructorKey);
                        var count = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        var initKind = kindText == "this" ? ConstructorInitializerKind.This : ConstructorInitializerKind.Base;
                        return new BoundConstructorChainExpression(NoSyntax, initKind, constructor, arguments.ToImmutable());
                    }
                case "this":
                    {
                        var type = (NamedTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        return new BoundThisExpression(NoSyntax, type);
                    }
                case "fnval":
                    {
                        // Step D-a：函数值/捕获闭包入口——类型 + FnKey + 可选接收者 + 可选闭包环境类
                        var fnType = (FunctionTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        var function = _resolver.ResolveFunction(_tokens.ExpectString());
                        var receiver = ReadNullableExpression(labels);
                        var envToken = _tokens.ExpectString();
                        var environmentClass = envToken == "-"
                            ? null
                            : _resolver.ResolveNamedType(envToken) as NamedTypeSymbol;

                        return new BoundFunctionValueExpression(NoSyntax, function, receiver, body: null, fnType, environmentClass);
                    }
                case "invoc":
                    {
                        var ivType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var count = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        var callee = ReadExpression(labels);
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundInvocationExpression(NoSyntax, callee, arguments.ToImmutable(), ivType);
                    }
                default:
                    throw new InvalidDataException($"Unknown expression kind '{kind}'");
            }
        }

        private BoundUnaryOperator ReadUnaryOperator()
        {
            _tokens.Expect("uop");
            var unaryKind = CoaText.ParseUnaryOpText(_tokens.ExpectString());
            var operandType = _resolver.ResolveTypeRef(_tokens.ExpectString());
            var op = BoundUnaryOperator.Bind(unaryKind, operandType);
            _tokens.End();
            return op ?? throw new InvalidDataException($"Cannot bind unary operator {unaryKind} on {operandType}");
        }

        private BoundBinaryOperator ReadBinaryOperator()
        {
            _tokens.Expect("bop");
            var binaryKind = CoaText.ParseBinaryOpText(_tokens.ExpectString());
            var leftType = _resolver.ResolveTypeRef(_tokens.ExpectString());
            var rightType = _resolver.ResolveTypeRef(_tokens.ExpectString());
            var op = BoundBinaryOperator.Bind(binaryKind, leftType, rightType);
            _tokens.End();
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

        // ---------------------------------------------------------------- read: tokenizer / helpers

        private static IEnumerable<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                // 6e-M35：反斜杠转义序列（`\(`/`\)`/`\s`/`\\` 等）整体入 token——
                // 否则字符串含 `(`/`)` 时（如 Handle.ToString "Handle("）构造定界符切碎，
                // Escape 已产出 `\(` 但 tokenizer 未识别 → System.Core.coa 加载失败。
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

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

        /// <summary>全名拆分为（命名空间, 名）；无点号时命名空间为空。</summary>
        private static (string Namespace, string Name) SplitFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            return lastDot < 0 ? ("", fullName) : (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1));
        }
    }
}
