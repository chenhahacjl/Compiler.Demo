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
    /// .coa 符号段读端（自 CoaFormatReader 拆出：enum/class/genericClass/function/variable 读取）。
    /// </summary>
    internal sealed partial class CoaFormatReader
    {
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
    }
}
