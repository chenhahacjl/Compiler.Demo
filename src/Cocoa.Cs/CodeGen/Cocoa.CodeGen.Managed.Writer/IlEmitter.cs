using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeGen.Managed.Structure;
 using Cocoa.CodeGen.Managed.Reader;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

using Cocoa.CodeAnalysis;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Managed.Writer
{
    /// <summary>
    /// IL 路径发射器：绑定树 → 自研 IL 组件（IlAssembler/MetadataBuilder/ManagedPEWriter）。
    /// 发射语义与原 Mono.Cecil 实现一致（表达式/语句 → IL 指令序列）。
    /// </summary>
    internal sealed partial class IlEmitter
    {
        private readonly MetadataBuilder _metadata;
        private readonly IlFramework _framework;
        private readonly string _moduleName;

        // 动态链接（阶段 A3）：cod 来源符号 → 库程序集名；据此合成 AssemblyRef/TypeRef/MemberRef 指向各库 dll
        private readonly ImmutableDictionary<object, string> _codAssemblies;
        private readonly Dictionary<string, IlAssemblyRef> _codAssemblyRefs = new Dictionary<string, IlAssemblyRef>();

        /// <summary>cod 顶层函数重载组（同库内同 (ns,name) 的函数 >1）：方法名追加参数类型后缀，与库侧发射规则一致。</summary>
        private readonly HashSet<(string asm, string ns, string name)> _codOverloadedGroups = new();
        private readonly Dictionary<FunctionSymbol, IlMethodDef> _methods = new Dictionary<FunctionSymbol, IlMethodDef>();
        private readonly Dictionary<VariableSymbol, int> _locals = new Dictionary<VariableSymbol, int>();
        private readonly Dictionary<BoundExpression, int> _temporaryLocalIndices = new Dictionary<BoundExpression, int>();
        // N1：非 BoundExpression 键的合成临时槽（builtin 发射内部用，如 CopyRange 的 count/dst），键需调用点唯一
        private readonly Dictionary<object, int> _syntheticTemporaryLocalIndices = new Dictionary<object, int>();
        private List<IlType>? _currentFunctionLocals;

        /// <summary>保护区内 return 的出口块（leave 目标）——CLR 要求离开保护区域须 leave，且 finally 须先执行。</summary>
        private IlInstruction? _returnExitTarget;
        private bool _returnExitHasValue;
        private int _returnExitTemp = -1;
        private int _protectedBlockDepth;
        private readonly Dictionary<BoundLabel, IlInstruction> _labelTargets = new Dictionary<BoundLabel, IlInstruction>();

        private FunctionSymbol? _entryFunction;
        private bool _entryVoidMain;

        private readonly IlTypeDef _typeDefinition;
        /// <summary>库产物（emitLibrary）：类型/方法统一按 public 发布（分发面即公共契约）。</summary>
        private bool _publishPublicSurface;

        private readonly Dictionary<NamedTypeSymbol, IlTypeDef> _classTypeDefs = new Dictionary<NamedTypeSymbol, IlTypeDef>();
        private readonly Dictionary<FieldSymbol, IlFieldDef> _fieldDefs = new Dictionary<FieldSymbol, IlFieldDef>();
    private readonly DelegateShapeCache _delegateShapes;
        private HashSet<(string Namespace, string Name)>? _overloadedGroups;
        private bool _currentMethodIsInstance;

        /// <summary>6e-M22 C5-c：当前方法的环境对象局部槽索引与布局类（无捕获 = null）。</summary>
        private int? _closureEnvLocalIndex;
        private NamedTypeSymbol? _closureClass;
        private Dictionary<string, IlFieldDef>? _closureFieldDefs;
        private readonly Dictionary<NamedTypeSymbol, IlMethodDef> environmentCtorDefs = new();

        /// <summary>6e-M22 委托真实类型化：delegate 类合成 `.ctor(object, IntPtr)` MethodDef（newobj 目标）。</summary>
        private readonly Dictionary<NamedTypeSymbol, IlMethodDef> _delegateCtors = new();
        /// <summary>6e-M22 委托真实类型化：delegate 类合成 `Invoke` MethodDef（callvirt 目标）。</summary>
        private readonly Dictionary<NamedTypeSymbol, IlMethodDef> _delegateInvokes = new();

        /// <summary>闭包环境类判定：Binder 合成的 `__Env_<fn>` 命名约定。</summary>
        private static bool IsClosureEnvironmentClass(NamedTypeSymbol classType)
            => classType.Name.StartsWith("__Env_", StringComparison.Ordinal);

        private IlEmitter(string moduleName, string[] references, ImmutableDictionary<object, string>? codAssemblies = null)
        {
            _moduleName = moduleName;
            _codAssemblies = codAssemblies ?? ImmutableDictionary<object, string>.Empty;
            BuildCodOverloadGroups();
            _metadata = new MetadataBuilder(moduleName, moduleName);
            _framework = new IlFramework(_metadata, references);
            _delegateShapes = new DelegateShapeCache(_metadata, _framework);

            // 顶层函数容器 TypeDef。名字用尖括号（非法标识符）杜绝与用户类同名冲突
            // （否则用户定义 `class Program` 时与默认 "Program" TypeDef 撞名 → BadImageFormatException）。
            _typeDefinition = new IlTypeDef("<CocoaTopLevel>", "", _framework.ObjectType);
            _metadata.AddTypeDef(_typeDefinition);
        }

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath)
            => Emit(program, moduleName, references, outputPath, IlTarget.Default, emitLibrary: false);

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath, IlTarget target)
            => Emit(program, moduleName, references, outputPath, target, emitLibrary: false);

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary)
        {
            return Emit(program, moduleName, references, outputPath, target, emitLibrary, program.CodAssemblies);
        }

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary, ImmutableDictionary<object, string>? codAssemblies, bool publishPublicSurface = false)
        {
            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            var emitter = new IlEmitter(moduleName, references, codAssemblies);

            return emitter.Emit(program, outputPath, target, emitLibrary, publishPublicSurface);
        }

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath)
            => Emit(program, outputPath, IlTarget.Default);

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target)
            => Emit(program, outputPath, target, emitLibrary: false);

        /// <summary>BCL 重名类的本体发射判定：有实例字段或实例方法（真实 Cocoa body，如 MemoryStream/StreamWriter）
        /// → 必须发射 TypeDef + 方法体；纯静态容器（Console/Environment/Convert）→ 调用点 BCL 直链。</summary>
        private static bool MustEmitBody(NamedTypeSymbol c) =>
            c.Fields.Any(f => !f.IsStatic) || c.Methods.Any(m => !m.IsStatic);

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target, bool emitLibrary, bool publishPublicSurface = false)
        {
        // 库产物的分发面即其公共契约：internal 门面类/方法在 dll 形态下发布为 public。
        // 仅 `.coa` 动态链接库启用（CoaLibraryCompiler）——消费方跨程序集访问需要；
        // `-f library`（C# 互操作）保持符号原可见性：internal 隐藏是既定访问控制语义
        _publishPublicSurface = publishPublicSurface;
        _entryFunction = emitLibrary ? null : program.MainFunction;

            // 6e-M26：FunctionSymbol 走默认引用 GetHashCode（进程随机）→ program.Functions（ImmutableDictionary）
            // 枚举顺序跨运行不稳定，导致方法体/MemberRef/#US 注册顺序变化、构建不可复现。
            // 统一按确定性键排序后再迭代（FunctionSortKey：Ordinal 组合键），保证发射顺序可复现。
            var orderedFunctions = program.Functions.Keys
                .OrderBy(FunctionSortKey, StringComparer.Ordinal)
                .ToList();

            // 1. 收集 class（基类在前）→ 建 IlTypeDef + 字段
            // 6e-M18：补入函数引用的注入容器类（System.Core.coa 的 Console/Math 等，不在 program.Classes 的源码声明集内）
            // 非 facade 类出 TypeDef，除非是「与 BCL 重名且无实例状态」的静态容器类（Console/Environment 等：
            // 调用点按 BCL 同名直链 TypeRef，避免本地 TypeDef 与 BCL TypeRef 冲撞）。
            // 有实例字段/实例方法的 BCL 重名类（MemoryStream/StreamWriter 等真实 Cocoa body）必须发射本体。
            var classes = program.Classes.Where(c => (!c.IsFacadeClass || IsNativeIntCarrier(c))
                && (c.ContainingLibrary == null || !_framework.TypeExistsInReferences(c.FullName) || MustEmitBody(c))).ToList();
            foreach (var f in orderedFunctions)
            {
                if (f.ContainingClass != null && (!f.ContainingClass.IsFacadeClass || IsNativeIntCarrier(f.ContainingClass))
                    && (f.ContainingClass.ContainingLibrary == null || !_framework.TypeExistsInReferences(f.ContainingClass.FullName) || MustEmitBody(f.ContainingClass))
                    && !classes.Contains(f.ContainingClass))
                {
                    classes.Add(f.ContainingClass);
                }
            }

            var emitted = new HashSet<NamedTypeSymbol>();

            // 1a：先注册全部 TypeDef 壳（6e-M20：泛型实例化类的字段可前向引用兄弟实例化类，
            // 依赖序无法仅按基类链排序——壳先行入表，Extends/字段随后填充）
            foreach (var classType in classes)
            {
                var typeDef = new IlTypeDef(classType.Name, classType.Namespace, null, isPublic: _publishPublicSurface || classType.Visibility == Cocoa.CodeAnalysis.Symbols.Visibility.Public || IsClosureEnvironmentClass(classType), baseTypeDef: null)
                {
                    IsAbstract = classType.IsAbstract,
                    IsSealed = classType.IsSealed,
                    IsInterface = classType.IsInterface,
                    IsValueType = classType.IsValueType,
                };
                _classTypeDefs.Add(classType, typeDef);
                _metadata.AddTypeDef(typeDef);
                emitted.Add(classType);
            }

            // 注册内建 Delegate/MulticastDelegate TypeRef（供 delegate 子类 Extends 引用）
            var multicastDelegateRef = _framework.RequireType("System.MulticastDelegate");

            // 1b：填充 Extends + 字段（全部 TypeDef 已在表内，任意顺序安全）
            foreach (var classType in classes)
            {
                EmitClassDeclaration(classType, multicastDelegateRef);
            }

            // 6e-M22 C5-c：合成环境类的默认 .ctor（ldarg.0 → Object::.ctor → ret），
            // 直接挂 TypeDef；方法体在本函数尾部统一手工组装
            var environmentCtorBodies = new List<(NamedTypeSymbol ClassType, IlMethodDef Ctor)>();
            foreach (var classType in classes)
            {
                if (IsClosureEnvironmentClass(classType))
                {
                    var ctorDef = new IlMethodDef(".ctor", IlType.Void, Array.Empty<IlType>(), null, isStatic: false) { Visibility = IlVisibility.Public };
                    _metadata.AddMethodDef(_classTypeDefs[classType], ctorDef);
                    environmentCtorDefs[classType] = ctorDef;
                    
                    environmentCtorBodies.Add((classType, ctorDef));
                }
            }

            // 1.5 InterfaceImpl：所有 TypeDef 就绪后，把类实现/继承的接口（含基类链与接口继承）写入各自 TypeDef
            foreach (var classType in classes)
            {
                if (!_classTypeDefs.TryGetValue(classType, out var typeDef)) continue;
                foreach (var iface in classType.GetAllInterfaces())
                {
                    if (iface.IsExternal)
                    {
                        typeDef.Interfaces.Add(new IlInterfaceImpl(null, ResolveExternalTypeRef(iface)));
                    }
                    else if (iface.ContainingLibrary != null && iface.TypeKind == TypeKind.Interface)
                    {
                        // cod 库接口（BCL 同名接口）：无 TypeDef，按全名直联。
                        // 仅当 BCL 对应物确实为接口时才重定向（System.IO.Stream 在 Cocoa 是接口、BCL 是类 → 跳过，
                        // 否则 TypeDef 会"把类当接口实现"被 CLR 拒绝）。
                        if (_framework.IsInterfaceInReferences(iface.FullName))
                        {
                            typeDef.Interfaces.Add(new IlInterfaceImpl(null, _framework.RequireType(iface.FullName)));
                        }
                    }
                    else
                    {
                        // 泛型标记接口（6e-M20 IEnumerable$T 等）不进发射清单：仅作编译期能力标记
                        if (!_classTypeDefs.TryGetValue(iface, out var ifaceDef))
                        {
                            continue;
                        }

                        typeDef.Interfaces.Add(new IlInterfaceImpl(ifaceDef, null));
                    }
                }
            }

            // 2. 方法声明（顺序 = 顶层 + 各 class 方法，与 typeDefs 分组一致）
            // 先计算重载组（同 (ns, name) 顶层函数 >1）：IL 方法名追加参数类型后缀保证元数据唯一
            _overloadedGroups = new HashSet<(string, string)>();
            var topLevelNameCounts = new Dictionary<(string, string), int>();
            foreach (var f in orderedFunctions)
            {
                if (f.ContainingClass == null && !f.IsConstructor)
                {
                    var key = (f.Namespace, f.Name);
                    topLevelNameCounts[key] = topLevelNameCounts.GetValueOrDefault(key) + 1;
                }
            }

            foreach (var kv in topLevelNameCounts)
            {
                if (kv.Value > 1)
                {
                    _overloadedGroups.Add(kv.Key);
                }
            }

            foreach (var function in orderedFunctions)
            {
                if (function.ContainingClass?.IsFacadeClass == true && !IsNativeIntCarrier(function.ContainingClass)) continue;
                if (function.ContainingClass != null && !classes.Contains(function.ContainingClass)) continue; // 类型未入 TypeDef 表
                if (function.ContainingClass is { TypeKind: TypeKind.Delegate }) continue;
                if (function.BuiltinKind != null)
                {
                    // syscall 内部原语：无方法体、调用点按 BuiltinKind 分发，不声明为 IL 方法
                    continue;
                }

                EmitFunctionDeclaration(function);
            }

            // 2.5 属性定义（getter/setter 方法已发射）
            foreach (var classType in classes)
            {
                if (!_classTypeDefs.TryGetValue(classType, out var typeDef)) continue;
                foreach (var property in classType.Properties)
                {
                    IlMethodDef? getterMethod = null;
                    IlMethodDef? setterMethod = null;
                    if (property.Getter != null && _methods.TryGetValue(property.Getter, out var gm))
                    {
                        getterMethod = gm;
                    }
                    if (property.Setter != null && _methods.TryGetValue(property.Setter, out var sm))
                    {
                        setterMethod = sm;
                    }

                    if (getterMethod != null)
                    {
                        typeDef.Properties.Add(new IlPropertyDef(property.Name, ToIlType(property.Type), getterMethod, setterMethod));
                    }
                }
            }

            var bodies = new List<ManagedPEWriter.MethodBodyBlob>();
            var methods = new List<IlMethodDef>();

            foreach (var function in orderedFunctions)
            {
                if (function.ContainingClass?.IsFacadeClass == true && !IsNativeIntCarrier(function.ContainingClass)) continue;
                if (function.ContainingClass != null && !classes.Contains(function.ContainingClass)) continue; // 类型未入 TypeDef 表
                if (function.ContainingClass is { TypeKind: TypeKind.Delegate }) continue;
                if (function.IsExtern || function.IsAbstract || function.BuiltinKind != null)
                {
                    continue;
                }

                var method = _methods[function];
                methods.Add(method);
                _entryVoidMain = _entryFunction == function && function.ReturnType == TypeSymbol.Void;
                var (code, localSigToken, maxStack, exceptionTable) = EmitFunctionBody(method, function, program.Functions[function]);
                bodies.Add(new ManagedPEWriter.MethodBodyBlob(code, localSigToken, (ushort)maxStack, exceptionTable));
            }

            // 6e-M22 C5-c：环境类 .ctor 方法体（ldarg.0 → Object::.ctor → ret）
            foreach (var (classType, ctorDef) in environmentCtorBodies)
            {
                var ctorAssembler = new IlAssembler();
                ctorAssembler.Emit(IlOpCodeTable.Get("Ldarg_0"));
                ctorAssembler.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectCtor);
                ctorAssembler.Emit(IlOpCodeTable.Get("Ret"));

                var ctorCode = ctorAssembler.Assemble();
                ctorAssembler.PatchTokens(ctorCode, _metadata.BuildTokenMap());

                methods.Add(ctorDef);
                bodies.Add(new ManagedPEWriter.MethodBodyBlob(ctorCode, 0, 1));
            }

            _metadata.AddCustomAttribute(new IlCustomAttribute(_framework.DebuggableAttributeCtor, MetadataBuilder.EncodeDebuggableAttributeBlob()));

            var entryPointToken = program.MainFunction == null ? 0 : _metadata.BuildTokenMap()[_methods[program.MainFunction]];
            var pe = ManagedPEWriter.Build(_moduleName, methods, bodies, _metadata, entryPointToken, target);

            File.WriteAllBytes(outputPath, pe);
            // 库（dll）不直接运行，不写 runtimeconfig；netcore exe 写。
            if (!emitLibrary && target.Runtime == IlRuntime.NetCore)
            {
                WriteRuntimeConfig(outputPath, target);
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        /// <summary>framework-dependent 运行所需的 runtimeconfig.json。</summary>
        private static void WriteRuntimeConfig(string outputPath, IlTarget target)
        {
            var runtimeConfigPath = Path.ChangeExtension(outputPath, ".runtimeconfig.json");
            File.WriteAllText(runtimeConfigPath, target.GetRuntimeConfigJson());
        }

        private void EmitFunctionDeclaration(FunctionSymbol function)
        {
            // 入口统一为 static int Main()：语言 void main（默认返回 0）→ IL 返回 int，尾部补 0
            var returnType = _entryFunction == function && function.ReturnType == TypeSymbol.Void
                ? ToIlType(TypeSymbol.Int32)
                : ToIlType(function.ReturnType);
            var isInstance = function.ContainingClass != null && !function.IsStatic;
            var parameterTypes = new List<IlType>();
            foreach (var parameter in function.Parameters)
            {
                // 6e-M23 R6：byref 形参编码为 T&
                parameterTypes.Add(parameter.IsByRef ? IlType.ByRefOf(ToIlType(parameter.Type)) : ToIlType(parameter.Type));
            }

            var callingConvention = function.CallingConvention switch
            {
                CallingConvention.Cdecl => IlCallingConvention.Cdecl,
                CallingConvention.StdCall => IlCallingConvention.StdCall,
                _ => IlCallingConvention.Winapi,
            };

            // 顶层函数：命名空间限定名（EmitName）；重载组内追加参数类型后缀保证同一 TypeDef 内元数据方法名唯一
            var name = function.IsConstructor ? (function.IsStatic ? ".cctor" : ".ctor") : function.EmitName;
            if (function.ContainingClass == null && !function.IsConstructor &&
                _overloadedGroups!.Contains((function.Namespace, function.Name)))
            {
                // 6e-M23 R6：仅差 out/ref 的重载也须名字唯一（修饰符前缀入 mangle）
                name += "$" + string.Join("$", function.Parameters.Select(p =>
                    (p.IsOut ? "out$" : p.IsRef ? "ref$" : "") + EncodeTypeNameForMethodName(p.Type)));
            }

            var implementsInterfaceMember = isInstance &&
                function.ContainingClass!.GetAllInterfaces().Any(i =>
                    i.GetDeclaredMethod(function.Name) != null ||
                    i.Properties.Any(p => p.Getter?.Name == function.Name || p.Setter?.Name == function.Name));

            var method = new IlMethodDef(name, returnType, parameterTypes, null, function.IsExtern ? function.DllName : null, function.EntryPoint, callingConvention, isStatic: !isInstance, charSet: function.CharSet == null ? IlCharSet.Unicode : ToIlCharSet(function.CharSet.Value))
            {
                Visibility = _publishPublicSurface || (function.IsLambda && function.EnvironmentClass != null) ? IlVisibility.Public : ToIlVisibility(function.Visibility),
                IsVirtual = function.IsVirtual || function.IsOverride || implementsInterfaceMember,
                IsAbstract = function.IsAbstract,
                IsSealed = function.IsSealed,
                IsExplicitThis = false,
            };
            _methods.Add(function, method);

            // 6e-M22 C5-c：捕获 lambda 声明为环境类的实例方法（this = 环境对象，经委托 target 传入）
            if (function.EnvironmentClass != null && function.IsLambda)
            {
                method.IsStatic = false;
                _metadata.AddMethodDef(_classTypeDefs[function.EnvironmentClass], method);
                return;
            }

            var declaringType = function.ContainingClass != null ? _classTypeDefs[function.ContainingClass] : _typeDefinition;
            _metadata.AddMethodDef(declaringType, method);
        }

        private void EmitClassDeclaration(NamedTypeSymbol classType, IlTypeRef multicastDelegateRef)
        {
            var typeDef = _classTypeDefs[classType];
            var hasUserBase = classType.BaseType != null && !classType.BaseType.IsSystemObjectRoot;
            IlTypeDef? baseTypeDef = null;
            IlTypeRef? baseTypeRef = null;

            if (hasUserBase)
            {
                if (classType.BaseType == NamedTypeSymbol.SystemMulticastDelegate)
                {
                    // delegate 子类 extends System.MulticastDelegate → 框架 TypeRef
                    baseTypeRef = multicastDelegateRef;
                }
                else if (IsFacadeRedirect(classType.BaseType!))
                {
                    // 6e-M25：facade 基类（MyError extends Exception，Exception → System.Exception）
                    // TypeDef 基类指向框架 TypeRef（facade 类无 TypeDef）。
                    baseTypeRef = ToIlType(classType.BaseType!).Reference;
                }
                else if (_classTypeDefs.TryGetValue(classType.BaseType!, out var bt))
                {
                    baseTypeDef = bt;
                }
            }

            // Extends 决策：接口无基类；无显式基类走 Object；用户基类走 TypeDef；MulticastDelegate 走 TypeRef
            if (classType.IsInterface)
            {
                typeDef.SetBase(null, null);
            }
            else if (baseTypeRef != null)
            {
                typeDef.SetBase(baseTypeRef, null);
            }
            else if (baseTypeDef != null)
            {
                typeDef.SetBase(null, baseTypeDef);
            }
            else if (classType.IsValueType)
            {
                typeDef.SetBase(_framework.ValueType, null);
            }
            else
            {
                typeDef.SetBase(_framework.ObjectType, null);
            }

            foreach (var field in classType.Fields)
            {
                var fieldDef = new IlFieldDef(field.Name, ToIlType(field.Type), ToIlVisibility(field.Visibility), isStatic: field.IsStatic);
                typeDef.Fields.Add(fieldDef);
                _fieldDefs.Add(field, fieldDef);
            }

            // 6e-M22 委托真实类型化：delegate 类合成 `.ctor(object, IntPtr)` + `Invoke(签名)` MethodDef。
            // csc 同构（见 DlgCS 反射基线）：委托类所有方法均为 **Runtime 实现**（impl=0x0003、RVA=0），
            // 由 CLR 在委托实例化时填充——.ctor(object,IntPtr) 设置 target/method，Invoke 提供委托分派。
            if (classType.TypeKind == TypeKind.Delegate)
            {
                var ctorDef = new IlMethodDef(".ctor", IlType.Void, new[] { IlType.Object, IlType.NativeInt }, null, isStatic: false)
                {
                    Visibility = IlVisibility.Public,
                    IsRuntimeImplementation = true,
                };
                _metadata.AddMethodDef(typeDef, ctorDef);
                _delegateCtors[classType] = ctorDef;

                var signature = classType.DelegateSignature();
                if (signature != null)
                {
                    var parameterTypes = signature.ParameterTypes.Select(ToIlType).ToList();
                    var invokeDef = new IlMethodDef("Invoke", ToIlType(signature.ReturnType), parameterTypes, null, isStatic: false)
                    {
                        Visibility = IlVisibility.Public,
                        IsVirtual = true,
                        IsNewSlot = true,
                        IsRuntimeImplementation = true,
                    };
                    _metadata.AddMethodDef(typeDef, invokeDef);
                    _delegateInvokes[classType] = invokeDef;
                }
            }
        }

        private (byte[] Code, uint LocalSigToken, int MaxStack, byte[]? ExceptionTable) EmitFunctionBody(IlMethodDef method, FunctionSymbol function, BoundBlockStatement body)
        {
            _locals.Clear();
            _labelTargets.Clear();
            _temporaryLocalIndices.Clear();
            _syntheticTemporaryLocalIndices.Clear();
            _currentMethodIsInstance = !method.IsStatic;
            _returnExitTarget = null;
            _returnExitHasValue = false;
            _returnExitTemp = -1;
            _protectedBlockDepth = 0;

            var assembler = new IlAssembler();

            // 预收集局部变量（按声明顺序分配索引）
            var localTypes = new List<IlType>();
            _currentFunctionLocals = localTypes;

            // 6e-M22 C5-c：环境对象局部槽预留 + 前奏 IL
            _closureClass = function.EnvironmentClass;
            _closureEnvLocalIndex = null;

            if (_closureClass != null)
            {
                var envIlType = ToIlType(_closureClass);
                localTypes.Add(envIlType);
                _closureEnvLocalIndex = localTypes.Count - 1;
                _closureFieldDefs = _closureClass.Fields.ToDictionary(f => f.Name, f => _fieldDefs[f]);

                if (function.IsLambdaWithEnvironment)
                {
                    // lambda：this（ldarg.0）即环境对象 → 存入局部
                    assembler.Emit(IlOpCodeTable.Get("Ldarg_0"));
                    assembler.Emit(IlOpCodeTable.Get("Stloc"), (ushort)_closureEnvLocalIndex.Value);
                }
            }

            CollectLocals(body, localTypes);

            // 宿主函数：newobj 环境实例 + 捕获参数播种
            if (_closureClass != null && !function.IsLambdaWithEnvironment)
            {
                if (!environmentCtorDefs.TryGetValue(_closureClass, out var envCtorDef))
                {
                    throw new Exception($"环境类 {_closureClass.Name} 缺少 .ctor。");
                }

                assembler.Emit(IlOpCodeTable.Get("Newobj"), envCtorDef);
                assembler.Emit(IlOpCodeTable.Get("Stloc"), (ushort)_closureEnvLocalIndex!.Value);

                if (function.CapturedVariables != null)
                {
                    foreach (var captured in function.CapturedVariables)
                    {
                        if (captured is ParameterSymbol parameter)
                        {
                            var field = _closureFieldDefs![captured.Name];
                            assembler.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                            assembler.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)(parameter.Ordinal + (_currentMethodIsInstance ? 1 : 0)));
                            assembler.Emit(IlOpCodeTable.Get("Stfld"), field);
                        }
                    }
                }
            }

            // 预收集 label 占位（前向引用需要目标指令对象）
            CollectLabels(body);

            foreach (var statement in body.Statements)
            {
                EmitStatement(assembler, statement);
            }

            // 6e-M22：方法体末尾补隐式 ret（若无显式 return 终结）
            // 1c/C4：判断"有效尾"须下探嵌套结构——最后一条语句是块（取其尾）或双分支 if
            // （两分支都以 return 收尾）时视为已有 return 收尾；旧实现只看最外层语句 Kind，
            // 嵌套块尾的 return 会被误判为缺收尾而多补一个 Ret（不可达冗余），
            // 且若 lowering 行为变化则可能反向漏补，产出 fall-through 的无效 IL。
            var lastStatement = body.Statements.Length == 0 ? null : body.Statements[^1];
            var needsImplicitRet = !TailEndsWithReturn(lastStatement);
            if (needsImplicitRet)
            {
                assembler.Emit(IlOpCodeTable.Get("Ret"));
            }

            // 保护区返回出口块：仅经 leave 可达，CLR 执行全部 finally 后落在出口 → ldloc+ret
            if (_returnExitTarget != null)
            {
                assembler.Emit(_returnExitTarget);
                if (_returnExitHasValue)
                {
                    assembler.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_returnExitTemp);
                }

                assembler.Emit(IlOpCodeTable.Get("Ret"));
            }

            var code = assembler.Assemble();
            var maxStack = assembler.ComputeMaxStack(assembler.Instructions);

            // 注册 #US 字符串（Ldstr fixup 回填前）
            foreach (var value in assembler.StringFixupValues)
            {
                _metadata.GetOrAddUserString(value);
            }

            // 先注册 StandAloneSig（局部变量签名），再构建 token 映射回填
            uint localSigToken = 0;
            var sigReference = localTypes.Count > 0
                ? _metadata.AddStandAloneSig(_metadata.EncodeLocalVarSignature(localTypes))
                : null;

            var tokenMap = _metadata.BuildTokenMap();
            assembler.PatchTokens(code, tokenMap);
            assembler.PatchStrings(code, _metadata.UserStringTokens);
            assembler.ValidatePatched(code); // 1c/C5：占位符回填自检，拦截坏 token 于发射现场

            if (sigReference != null)
            {
                localSigToken = tokenMap[sigReference];
            }

            byte[]? exceptionTable = null;
            if (assembler.ExceptionClauses.Count > 0)
            {
                exceptionTable = BuildExceptionTable(assembler.ExceptionClauses, tokenMap, _framework);
            }

            return (code, localSigToken, maxStack, exceptionTable);
        }

        /// <summary>由 SEH 子句生成异常表字节（fat 格式 24 字节/子句；子句偏移为方法体代码段相对偏移，即 IlInstruction.Offset）。</summary>
        private static byte[] BuildExceptionTable(List<ExceptionClause> clauses, IReadOnlyDictionary<object, uint> tokenMap, IlFramework framework)
        {
            var section = new MemoryStream();
            using var writer = new BinaryWriter(section);

            var totalSize = 4 + clauses.Count * 24;
            // 节头：低 8 位 = EH 表(0x01) | fat 格式(0x40)；高位 = 节总字节数
            writer.Write((uint)((totalSize << 8) | 0x41));

            foreach (var clause in clauses)
            {
                var tryStart = (uint)clause.TryStart.Offset;
                var tryEnd = (uint)clause.TryEnd.Offset;
                var handlerStart = (uint)clause.HandlerStart.Offset;
                var handlerEnd = (uint)clause.HandlerEnd.Offset;

                uint classToken = 0;
                if (clause.CatchType != null)
                {
                    object? key = clause.CatchType.TypeDef as object
                               ?? clause.CatchType.Reference as object
                               ?? (clause.CatchType.Kind == IlTypeKind.String ? framework.StringType : null)
                               ?? throw new InvalidOperationException("catch 类型既无 TypeDef 也无 Reference。");
                    classToken = tokenMap[key];
                }

                writer.Write((uint)clause.HandlerKind);
                writer.Write(tryStart);
                writer.Write(tryEnd - tryStart);
                writer.Write(handlerStart);
                writer.Write(handlerEnd - handlerStart);
                writer.Write(classToken);
            }

            return section.ToArray();
        }

        /// <summary>
        /// 1c/C4：判断语句是否"以 return 收尾"。下探块（取尾语句）与双分支 if
        /// （两分支各自收尾）；goto/throw 等按"未收尾"处理——多补的隐式 Ret
        /// 位于不可达位置，安全冗余，漏补的 fall-through 才是无效 IL。
        /// </summary>
        private static bool TailEndsWithReturn(BoundStatement? statement)
        {
            switch (statement)
            {
                case BoundReturnStatement:
                    return true;
                case BoundBlockStatement block:
                    return TailEndsWithReturn(block.Statements.Length == 0 ? null : block.Statements[^1]);
                case BoundIfStatement ifStatement:
                    return ifStatement.ElseStatement != null &&
                           TailEndsWithReturn(ifStatement.ThenStatement) &&
                           TailEndsWithReturn(ifStatement.ElseStatement);
                case BoundSequencePointStatement sequencePoint:
                    return TailEndsWithReturn(sequencePoint.Statement);
                default:
                    return false;
            }
        }

        private void CollectLabels(BoundStatement node)
        {
            switch (node)
            {
                case BoundBlockStatement block:
                    foreach (var statement in block.Statements)
                    {
                        CollectLabels(statement);
                    }

                    break;
                case BoundLabelStatement labelStatement:
                    _labelTargets[labelStatement.Label] = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
                    break;
                case BoundSequencePointStatement sequencePoint:
                    CollectLabels(sequencePoint.Statement);
                    break;
                case BoundTryStatement tryStatement:
                    // N1：try/finally/catch 体内同样可能含 label（lock 体中的循环 lowering 产物）
                    CollectLabels(tryStatement.TryBlock);
                    if (tryStatement.FinallyBlock != null)
                    {
                        CollectLabels(tryStatement.FinallyBlock);
                    }
                    foreach (var catchClause in tryStatement.Catches)
                    {
                        CollectLabels(catchClause.Body);
                    }
                    break;
            }
        }

        private void CollectLocals(BoundStatement node, List<IlType> localTypes)
        {
            switch (node)
            {
                case BoundBlockStatement block:
                    foreach (var statement in block.Statements)
                    {
                        CollectLocals(statement, localTypes);
                    }

                    break;
                case BoundVariableDeclaration variableDeclaration:
                    _locals.Add(variableDeclaration.Variable, localTypes.Count);
                    localTypes.Add(ToIlType(variableDeclaration.Variable.Type));
                    break;
                case BoundExpressionStatement expressionStatement:
                    // out var 内联声明变量（无显式声明语句）：从调用实参的 byref 变量收集局部
                    if (expressionStatement.Expression is BoundCallExpression call)
                    {
                        foreach (var argument in call.Arguments)
                        {
                            if (argument is BoundByRefArgument byRefArgument &&
                                byRefArgument.Expression is BoundVariableExpression byRefVariable &&
                                !_locals.ContainsKey(byRefVariable.Variable))
                            {
                                _locals.Add(byRefVariable.Variable, localTypes.Count);
                                localTypes.Add(ToIlType(byRefVariable.Variable.Type));
                            }
                        }
                    }

                    break;
                case BoundSequencePointStatement sequencePoint:
                    CollectLocals(sequencePoint.Statement, localTypes);
                    break;
                case BoundTryStatement tryStatement:
                    CollectLocals(tryStatement.TryBlock, localTypes);
                    foreach (var catchClause in tryStatement.Catches)
                    {
                        _locals.Add(catchClause.Variable, localTypes.Count);
                        localTypes.Add(ToIlType(catchClause.CatchType));
                        CollectLocals(catchClause.Body, localTypes);
                    }

                    if (tryStatement.FinallyBlock != null)
                    {
                        CollectLocals(tryStatement.FinallyBlock, localTypes);
                    }

                    break;
            }
        }

        private static IlCharSet ToIlCharSet(Cocoa.CodeAnalysis.Symbols.CharSet charSet)
    {
        return charSet switch
        {
            Cocoa.CodeAnalysis.Symbols.CharSet.Ansi => IlCharSet.Ansi,
            Cocoa.CodeAnalysis.Symbols.CharSet.Auto => IlCharSet.Auto,
            _ => IlCharSet.Unicode,
        };
    }

    private static IlVisibility ToIlVisibility(Cocoa.CodeAnalysis.Symbols.Visibility visibility)
    {
        return visibility switch
        {
            Cocoa.CodeAnalysis.Symbols.Visibility.Public => IlVisibility.Public,
            Cocoa.CodeAnalysis.Symbols.Visibility.Internal => IlVisibility.Internal,
            Cocoa.CodeAnalysis.Symbols.Visibility.Protected => IlVisibility.Protected,
            _ => IlVisibility.Private,
        };
    }

    private IlType ToIlType(TypeSymbol type)
        {
            if (type == TypeSymbol.Any)
            {
                return IlType.Object;
            }

            if (type == TypeSymbol.Boolean)
            {
                return IlType.Boolean;
            }

            if (type == TypeSymbol.Int32)
            {
                return IlType.Int32;
            }

            if (type == TypeSymbol.Int64)
            {
                return IlType.Int64;
            }

            if (type == TypeSymbol.Char)
            {
                return IlType.Char;
            }

            if (type == TypeSymbol.UInt8)
            {
                return IlType.Byte;
            }

            if (type == TypeSymbol.Int8)
            {
                return IlType.SByte;
            }

            if (type == TypeSymbol.Int16)
            {
                return IlType.Int16;
            }

            if (type == TypeSymbol.UInt16)
            {
                return IlType.UInt16;
            }

            if (type == TypeSymbol.UInt32)
            {
                return IlType.UInt32;
            }

            if (type == TypeSymbol.UInt64)
            {
                return IlType.UInt64;
            }

            if (type == TypeSymbol.Float)
            {
                return IlType.Float;
            }

            if (type == TypeSymbol.Double)
            {
                return IlType.Double;
            }

            if (type == TypeSymbol.NativeInt32)
            {
                return IlType.NativeInt;
            }

            if (type == TypeSymbol.NativeUInt32)
            {
                return IlType.NativeUInt;
            }

            if (type == TypeSymbol.String)
            {
                return IlType.String;
            }

            if (type == TypeSymbol.Void)
            {
                return IlType.Void;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                // facade enum：整型映射到 BCL 同名枚举（FileMode/FileAccess/FileShare 等，facade 直链签名匹配用）
                if (enumType.IsFacadeClass)
                {
                    return IlType.Class(_framework.RequireType(enumType.FullName), isValueType: true);
                }

                return IlType.Int32;
            }

            // 6e-M22 委托真实类型化：delegate 类即真实 TypeDef（进 classes 发射清单），不再映射 Func`N——
            // 旧 D-B 遗留"delegate 类→Func 等价类型"映射删除后，走下方 NamedTypeSymbol → TypeDef 分支。
            if (type is NamedTypeSymbol classType)
            {
                // facade 类：整类映射到 BCL（非泛型 → TypeRef；泛型实例化 → TypeSpec）。
                // struct facade 须发射为 valuetype，并使用 FacadeThisType 提供的 BCL 值类型全名。
                if (IsFacadeRedirect(classType))
                {
                    var isStructFacade = classType.TypeKind == TypeKind.Struct;

                    if (classType is InstantiatedTypeSymbol inst)
                    {
                        var def = inst.GenericDefinition!;
                        var openName = FacadeBclFullName(def) + "`" + def.TypeParameters.Length;
                        var genericDef = _framework.RequireType(openName);
                        return new IlType(IlTypeKind.GenericInst, genericDef, isValueType: isStructFacade, genericArguments: inst.TypeArguments.Select(ToIlType).ToArray());
                    }

                    var openNameDef = isStructFacade
                        ? FacadeBclFullName(classType)
                        : (classType.IsGenericDefinition
                            ? classType.FullName + "`" + classType.TypeParameters.Length
                            : classType.FullName);
                    return IlType.Class(_framework.RequireType(openNameDef), isValueType: isStructFacade);
                }

                // 动态链接：cod 容器类 → 指向其库 dll 的 TypeRef
                if (_codAssemblies.TryGetValue(classType, out var codAssembly))
                {
                    return IlType.Class(CodClassRef(classType, codAssembly));
                }

                // 6f-1 库类兜底：溯源表按 identity 索引，消费侧绑定可能经重建符号持有不同实例，
                // 依全名唯一性补一次匹配（与 6e-M23 泛型 owner 反解同系）。
                if (codAssembly == null)
                {
                    foreach (var entry in _codAssemblies)
                    {
                        if (entry.Key is NamedTypeSymbol named && named.FullName == classType.FullName)
                        {
                            codAssembly = entry.Value;
                            break;
                        }
                    }

                    if (codAssembly != null)
                    {
                        return IlType.Class(CodClassRef(classType, codAssembly));
                    }
                }

                if (classType.IsExternal)
                {
                    return IlType.Class(ResolveExternalTypeRef(classType));
                }

                // 6e-M19 M2-a：内建 Object/Type 单例不产生 TypeDef，映射框架 TypeRef
                if (classType.IsSystemObjectRoot)
                {
                    return IlType.Class(_framework.ObjectType);
                }

                if (classType == NamedTypeSymbol.SystemType)
                {
                    return IlType.Class(_framework.RequireType("System.Type"));
                }

                // cod 库接口（System.IDisposable / IEnumerable 等 BCL 同名接口）：无 TypeDef，按全名直联 BCL TypeRef。
                // （接口在 .coa 重建后 ContainingLibrary 已回填，正值系统库与消费方共享接口成员。）
                if (classType.ContainingLibrary != null && classType.TypeKind == TypeKind.Interface)
                {
                    return IlType.Class(_framework.RequireType(classType.FullName));
                }

                // cod 库类已在 BCL 引用中定义（MemoryStream 等）：无 TypeDef，按全名直联 BCL TypeRef。
                if (_classTypeDefs.TryGetValue(classType, out var typeDef))
                {
                    return IlType.Class(typeDef, isValueType: classType.IsValueType);
                }

                // 泛型实例化未入 TypeDef 表（ValueTuple`2 等合成值类型）：按泛型定义 + 实参构 GenericInst，
                // 不能用 FullName——InstantiatedTypeSymbol 的 FullName 是乱码（System.System.ValueTuple`2#@...）。
                if (classType is InstantiatedTypeSymbol instGen && instGen.GenericDefinition != null)
                {
                    var defRef = _framework.RequireType(FacadeBclFullName(instGen.GenericDefinition) + "`" + instGen.GenericDefinition.TypeParameters.Length);
                    return new IlType(IlTypeKind.GenericInst, defRef, isValueType: classType.IsValueType, genericArguments: instGen.TypeArguments.Select(ToIlType).ToArray());
                }

                return IlType.Class(_framework.RequireType(classType.FullName));
            }

            if (type.ElementType != null)
            {
                return IlType.SzArrayOf(ToIlType(type.ElementType));
            }

            // 函数类型（6e-M22 C4-b）：映射 System.Func`N / Action`N 泛型实例化
            if (type is FunctionTypeSymbol functionType)
            {
                return _delegateShapes.Resolve(functionType, ToIlType).Type;
            }

            throw new System.Exception($"Unexpected type {type}");
        }

        /// <summary>6e-M26：函数确定性排序键（ContainingClass.FullName + 命名空间 + 方法名 + 参数签名，Ordinal）。
        /// 保证 program.Functions（ImmutableDictionary，引用哈希进程随机）的发射顺序可复现。</summary>
        private static string FunctionSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }

        /// <summary>类型名编码进方法名后缀（`int[]` 的 `[]` 非法，转下划线）。</summary>
        private static string EncodeTypeNameForMethodName(TypeSymbol type)
        {
            return type.Name.Replace("[", "_").Replace("]", "_");
        }

        private readonly Dictionary<(string asm, string ns, string name), IlTypeRef> _codTypeRefs = new Dictionary<(string asm, string ns, string name), IlTypeRef>();

        /// <summary>复制库侧顶层函数重载分组规则（同 (asm, ns, name) 计数 >1 即成组）。</summary>
        private void BuildCodOverloadGroups()
        {
            if (_codAssemblies.IsEmpty)
            {
                return;
            }

            var counts = new Dictionary<(string asm, string ns, string name), int>();
            foreach (var pair in _codAssemblies)
            {
                if (pair.Key is FunctionSymbol fn && fn.ContainingClass == null && !fn.IsConstructor)
                {
                    var key = (pair.Value, fn.Namespace, fn.Name);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }

            foreach (var group in counts)
            {
                if (group.Value > 1)
                {
                    _codOverloadedGroups.Add(group.Key);
                }
            }
        }

        private IlAssemblyRef CodAssemblyRef(string assemblyName)
        {
            if (!_codAssemblyRefs.TryGetValue(assemblyName, out var reference))
            {
                reference = _metadata.DefineAssemblyRef(assemblyName, new Version(0, 0, 0, 0), Array.Empty<byte>(), null, flags: 0);
                _codAssemblyRefs[assemblyName] = reference;
            }

            return reference;
        }

        /// <summary>库侧顶层函数容器 TypeRef（名字含尖括号，非法标识符故不与用户类型冲突）。</summary>
        private IlTypeRef CodTopLevelTypeRef(string assemblyName)
        {
            return CodTypeRef(assemblyName, "", "<CocoaTopLevel>");
        }

        private IlTypeRef CodClassRef(NamedTypeSymbol classType, string assemblyName)
        {
            // 与库侧 TypeDef 命名同构：Namespace/Name 原样拆分
            return CodTypeRef(assemblyName, classType.Namespace, classType.Name);
        }

        private IlTypeRef CodTypeRef(string assemblyName, string namespaceName, string name)
        {
            var key = (assemblyName, namespaceName, name);
            if (!_codTypeRefs.TryGetValue(key, out var reference))
            {
                reference = _metadata.DefineTypeRef(CodAssemblyRef(assemblyName), namespaceName, name);
                _codTypeRefs[key] = reference;
            }

            return reference;
        }

        /// <summary>
        /// cod 函数的 MemberRef 合成：类方法挂宿主类 TypeRef（方法名 EmitName 原样）；顶层函数挂
        /// &lt;CocoaTopLevel&gt; TypeRef（EmitName 全名 + 重载后缀，规则与库侧发射一致）。
        /// </summary>
        private IlMethodRef CodMethodRef(FunctionSymbol function, string assemblyName)
        {
            IlTypeRef declaringType;
            string methodName;
            if (function.ContainingClass != null)
            {
                declaringType = CodClassRef(function.ContainingClass, assemblyName);
                methodName = function.IsConstructor
                    ? (function.IsStatic ? ".cctor" : ".ctor")
                    : function.EmitName;
            }
            else
            {
                declaringType = CodTopLevelTypeRef(assemblyName);
                methodName = function.IsConstructor
                    ? (function.IsStatic ? ".cctor" : ".ctor")
                    : function.EmitName;
                if (_codOverloadedGroups.Contains((assemblyName, function.Namespace, function.Name)))
                {
                    methodName += "$" + string.Join("$", function.Parameters.Select(p => EncodeTypeNameForMethodName(p.Type)));
                }
            }

            var returnType = ToIlType(function.ReturnType);
            var parameterTypes = function.Parameters.Select(p => ToIlType(p.Type)).ToArray();
            // 静态位与本地方法发射一致（316 行）：顶层函数（ContainingClass==null）恒 static；类成员按 IsStatic。
            var isStaticMethod = function.ContainingClass == null || function.IsStatic;
            return _metadata.DefineMethodRef(declaringType, methodName, returnType, parameterTypes, isStatic: isStaticMethod);
        }

        private IlTypeRef ResolveExternalTypeRef(NamedTypeSymbol classType)
        {
            return _framework.RequireType(classType.FullName);
        }

        // ------------------------------------------------------------------
        // 语句
        // ------------------------------------------------------------------

    }
}
