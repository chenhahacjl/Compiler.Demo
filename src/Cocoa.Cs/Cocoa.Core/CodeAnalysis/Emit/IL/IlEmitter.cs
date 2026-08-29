using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit.IL
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
        private List<IlType>? _currentFunctionLocals;
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

    public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target, bool emitLibrary, bool publishPublicSurface = false)
    {
        // 库产物的分发面即其公共契约：internal 门面类/方法在 dll 形态下发布为 public。
        // 仅 `.cod` 动态链接库启用（CodLibraryCompiler）——消费方跨程序集访问需要；
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
            // 6e-M18：补入函数引用的注入容器类（System.Core.cod 的 Console/Math 等，不在 program.Classes 的源码声明集内）
            var classes = program.Classes.Where(c => !c.IsFacadeClass).ToList();
            foreach (var f in orderedFunctions)
            {
                if (f.ContainingClass != null && !f.ContainingClass.IsFacadeClass && !classes.Contains(f.ContainingClass))
                {
                    classes.Add(f.ContainingClass);
                }
            }

            var emitted = new HashSet<NamedTypeSymbol>();

            // 1a：先注册全部 TypeDef 壳（6e-M20：泛型实例化类的字段可前向引用兄弟实例化类，
            // 依赖序无法仅按基类链排序——壳先行入表，Extends/字段随后填充）
            foreach (var classType in classes)
            {
                var typeDef = new IlTypeDef(classType.Name, classType.Namespace, null, isPublic: _publishPublicSurface || classType.Visibility == Visibility.Public, baseTypeDef: null)
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
                    var ctorDef = new IlMethodDef(".ctor", IlType.Void, Array.Empty<IlType>(), null, isStatic: false) { Visibility = Visibility.Public };
                    _metadata.AddMethodDef(_classTypeDefs[classType], ctorDef);
                    environmentCtorDefs[classType] = ctorDef;
                    
                    environmentCtorBodies.Add((classType, ctorDef));
                }
            }

            // 1.5 InterfaceImpl：所有 TypeDef 就绪后，把类实现/继承的接口（含基类链与接口继承）写入各自 TypeDef
            foreach (var classType in program.Classes)
            {
                if (classType.IsFacadeClass) continue;
                var typeDef = _classTypeDefs[classType];
                foreach (var iface in classType.GetAllInterfaces())
                {
                    if (iface.IsExternal)
                    {
                        typeDef.Interfaces.Add(new IlInterfaceImpl(null, ResolveExternalTypeRef(iface)));
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
                if (function.ContainingClass?.IsFacadeClass == true) continue;
                if (function.BuiltinKind != null)
                {
                    // syscall 内部原语：无方法体、调用点按 BuiltinKind 分发，不声明为 IL 方法
                    continue;
                }

                EmitFunctionDeclaration(function);
            }

            // 2.5 属性定义（getter/setter 方法已发射）
            foreach (var classType in program.Classes)
            {
                if (classType.IsFacadeClass) continue;
                var typeDef = _classTypeDefs[classType];
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
                if (function.ContainingClass?.IsFacadeClass == true) continue;
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

            var method = new IlMethodDef(name, returnType, parameterTypes, null, function.IsExtern ? function.DllName : null, function.EntryPoint, callingConvention, isStatic: !isInstance, charSet: function.CharSet ?? CharSet.Unicode)
            {
                Visibility = _publishPublicSurface ? Visibility.Public : function.Visibility,
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
                var fieldDef = new IlFieldDef(field.Name, ToIlType(field.Type), field.Visibility, isStatic: field.IsStatic);
                typeDef.Fields.Add(fieldDef);
                _fieldDefs.Add(field, fieldDef);
            }
        }

        private (byte[] Code, uint LocalSigToken, int MaxStack, byte[]? ExceptionTable) EmitFunctionBody(IlMethodDef method, FunctionSymbol function, BoundBlockStatement body)
        {
            _locals.Clear();
            _labelTargets.Clear();
            _temporaryLocalIndices.Clear();
            _currentMethodIsInstance = !method.IsStatic;

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
            var needsImplicitRet = body.Statements.Length == 0 ||
                                   body.Statements[^1].Kind != BoundNodeKind.ReturnStatement;
            if (needsImplicitRet)
            {
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

            if (type == TypeSymbol.String)
            {
                return IlType.String;
            }

            if (type == TypeSymbol.Void)
            {
                return IlType.Void;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return IlType.Int32;
            }

            // 6e-M22 D-B：delegate 类 → Func`N 等价类型（运行期表示与函数值一致）
            if (type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateClassType)
            {
                var sig = delegateClassType.DelegateSignature();
                if (sig != null)
                {
                    return _delegateShapes.Resolve(sig, ToIlType).Type;
                }
            }

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

                return IlType.Class(_classTypeDefs[classType], isValueType: classType.IsValueType);
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
                methodName = function.EmitName;
            }
            else
            {
                declaringType = CodTopLevelTypeRef(assemblyName);
                methodName = function.EmitName;
                if (_codOverloadedGroups.Contains((assemblyName, function.Namespace, function.Name)))
                {
                    methodName += "$" + string.Join("$", function.Parameters.Select(p => EncodeTypeNameForMethodName(p.Type)));
                }
            }

            var returnType = ToIlType(function.ReturnType);
            var parameterTypes = function.Parameters.Select(p => ToIlType(p.Type)).ToArray();
            return _metadata.DefineMethodRef(declaringType, methodName, returnType, parameterTypes, isStatic: true);
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
