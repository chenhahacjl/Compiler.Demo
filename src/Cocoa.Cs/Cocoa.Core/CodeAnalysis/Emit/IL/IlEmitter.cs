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
    internal sealed class IlEmitter
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
            if (function.EnvironmentClass != null && function.Syntax is LambdaExpressionSyntax)
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
            if (type is NamedTypeSymbol { IsDelegateClass: true } delegateClassType)
            {
                var sig = delegateClassType.GetDelegateSignature();
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

        private void EmitStatement(IlAssembler il, BoundStatement node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    foreach (var statement in ((BoundBlockStatement)node).Statements)
                    {
                        EmitStatement(il, statement);
                    }

                    break;
                case BoundNodeKind.NopStatement:
                    il.Emit(IlOpCodeTable.Get("Nop"));
                    break;
                case BoundNodeKind.VariableDeclaration:
                    EmitVariableDeclaration(il, (BoundVariableDeclaration)node);
                    break;
                case BoundNodeKind.LabelStatement:
                    EmitLabelStatement(il, (BoundLabelStatement)node);
                    break;
                case BoundNodeKind.GotoStatement:
                    EmitGotoStatement(il, (BoundGotoStatement)node);
                    break;
                case BoundNodeKind.ConditionalGotoStatement:
                    EmitConditionalGotoStatement(il, (BoundConditionalGotoStatement)node);
                    break;
                case BoundNodeKind.ReturnStatement:
                    EmitReturnStatement(il, (BoundReturnStatement)node);
                    break;
                case BoundNodeKind.ThrowStatement:
                    EmitThrowStatement(il, (BoundThrowStatement)node);
                    break;
                case BoundNodeKind.TryStatement:
                    EmitTryStatement(il, (BoundTryStatement)node);
                    break;
                case BoundNodeKind.ExpressionStatement:
                    EmitExpressionStatement(il, (BoundExpressionStatement)node);
                    break;
                case BoundNodeKind.SequencePointStatement:
                    EmitSequencePointStatement(il, (BoundSequencePointStatement)node);
                    break;
                default:
                    throw new System.Exception($"Unexpected node kind {node.Kind}");
            }
        }

        private void EmitVariableDeclaration(IlAssembler il, BoundVariableDeclaration node)
        {
            // 6e-M26：值类型默认（形如 `var p: Point` 未显式初始化）用 initobj 清零，不可 ldnull
            // （ldnull 存入 valuetype 局部 = 类型不匹配 → InvalidProgram）。仅当初始化器确为 null 字面量时才走此分支，
            // 否则（如 `var p = new Point(...)` 的对象创建）须正常发射初始化器。
            if (node.Initializer is BoundLiteralExpression { ConstantValue.Value: null } && node.Variable.Type is NamedTypeSymbol { IsValueType: true })
            {
                il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)_locals[node.Variable]);
                il.Emit(IlOpCodeTable.Get("Initobj"), ToIlType(node.Variable.Type));
                return;
            }

            EmitExpression(il, node.Initializer);

            // 6e-M22 C5-c：捕获变量声明 → 初始化值写入环境字段
            if (node.Variable.IsCaptured && _closureEnvLocalIndex.HasValue)
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                il.Emit(IlOpCodeTable.Get("Stfld"), _closureFieldDefs![node.Variable.Name]);
                return;
            }

            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)_locals[node.Variable]);
        }

        private void EmitLabelStatement(IlAssembler il, BoundLabelStatement node)
        {
            // 占位 Nop（CollectLabels 预建）：分支目标引用此指令，编码时自动重定位
            il.Emit(_labelTargets[node.Label]);
        }

        private void EmitGotoStatement(IlAssembler il, BoundGotoStatement node)
        {
            il.Emit(IlOpCodeTable.Get("Br"), _labelTargets[node.Label]);
        }

        private void EmitConditionalGotoStatement(IlAssembler il, BoundConditionalGotoStatement node)
        {
            EmitExpression(il, node.Condition);
            var opCode = node.JumpIfTrue ? "Brtrue" : "Brfalse";
            il.Emit(IlOpCodeTable.Get(opCode), _labelTargets[node.Label]);
        }

        private void EmitReturnStatement(IlAssembler il, BoundReturnStatement node)
        {
            if (node.Expression != null)
            {
                EmitExpression(il, node.Expression);
            }
            else if (_entryVoidMain)
            {
                // void main() 的（显式 return; 或隐式函数尾）返回 = 默认退出码 0
                il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
            }

            il.Emit(IlOpCodeTable.Get("Ret"));
        }

        private void EmitExpressionStatement(IlAssembler il, BoundExpressionStatement node)
        {
            EmitExpression(il, node.Expression);

            if (node.Expression.Type != TypeSymbol.Void)
            {
                il.Emit(IlOpCodeTable.Get("Pop"));
            }
        }

        private void EmitThrowStatement(IlAssembler il, BoundThrowStatement node)
        {
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Throw"));
        }

        private void EmitTryStatement(IlAssembler il, BoundTryStatement node)
        {
            // 结束标签（前向引用，最终作为方法体内的 Nop 落位）
            var endLabel = new IlInstruction(IlOpCodeTable.Get("Nop"), null);

            var tryStart = EmitLabel(il);
            // 空 try 体（无任何语句）：插入平衡无害指令，避免"try 区域仅含 leave"被 CLR EH 校验拒绝。
            if (node.TryBlock is BoundBlockStatement tryBlock && tryBlock.Statements.Length == 0)
            {
                il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                il.Emit(IlOpCodeTable.Get("Pop"));
            }
            EmitStatement(il, node.TryBlock);
            il.Emit(IlOpCodeTable.Get("Leave"), endLabel);

            var firstHandlerStart = EmitLabel(il); // try 区域终点 = 首个 handler 起点

            var catchStart = firstHandlerStart;
            foreach (var catchClause in node.Catches)
            {
                var handlerStart = catchStart;
                var localIndex = _locals[catchClause.Variable];
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)localIndex);
                EmitStatement(il, catchClause.Body);
                il.Emit(IlOpCodeTable.Get("Leave"), endLabel);
                var handlerEnd = EmitLabel(il); // 本 catch 终点 = 下一 handler 起点

                il.ExceptionClauses.Add(new ExceptionClause
                {
                    TryStart = tryStart,
                    TryEnd = firstHandlerStart,
                    HandlerStart = handlerStart,
                    HandlerEnd = handlerEnd,
                    HandlerKind = 0, // COR_ILEXCEPTION_CLAUSE_EXCEPTION (catch)
                    CatchType = ToIlType(catchClause.CatchType),
                });

                catchStart = handlerEnd;
            }

            var finallyStart = catchStart; // 所有 catch 之后的边界（无 catch 则为首个 handler 起点）
            if (node.FinallyBlock != null)
            {
                EmitStatement(il, node.FinallyBlock);
                il.Emit(IlOpCodeTable.Get("Endfinally"));

                il.ExceptionClauses.Add(new ExceptionClause
                {
                    TryStart = tryStart,
                    TryEnd = finallyStart,
                    HandlerStart = finallyStart,
                    HandlerEnd = endLabel,
                    HandlerKind = 2, // COR_ILEXCEPTION_CLAUSE_FINALLY
                });
            }

            il.Emit(endLabel);
        }

        /// <summary>在指令流中插入一个 Nop 标签并返回其 IlInstruction（供 leave/异常区域引用）。</summary>
        private IlInstruction EmitLabel(IlAssembler il)
        {
            var label = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
            il.Emit(label);
            return label;
        }

        private void EmitSequencePointStatement(IlAssembler il, BoundSequencePointStatement node)
        {
            EmitStatement(il, node.Statement);
        }

        /// <summary>函数值构造（6e-M22 C4-b）：[接收者|ldnull] ldftn 目标方法 newobj Func`N::.ctor(object, native int)。</summary>
        private void EmitFunctionValueExpression(IlAssembler il, BoundFunctionValueExpression node)
        {
            var shape = _delegateShapes.Resolve((FunctionTypeSymbol)node.Type, ToIlType);

            if (node.EnvironmentClass != null)
            {
                // 6e-M22 C5-c：捕获闭包——target = 当前环境对象
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex!.Value);
            }
            else if (node.Receiver != null)
            {
                // 实例方法组：接收者为委托 target（用户类引用型，无需装箱）
                EmitExpression(il, node.Receiver);
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Ldnull"));
            }

            il.Emit(IlOpCodeTable.Get("Ldftn"), _methods[node.Function]);
            il.Emit(IlOpCodeTable.Get("Newobj"), shape.Ctor);
        }

        /// <summary>间接调用（6e-M22 C4-b/D-B）：callee + args → callvirt Func\`N/委托类::Invoke。</summary>
        private void EmitInvocationExpression(IlAssembler il, BoundInvocationExpression node)
        {
            var functionType = node.Callee.Type switch
            {
                FunctionTypeSymbol ft => ft,
                NamedTypeSymbol { IsDelegateClass: true } dc => dc.GetDelegateSignature()!,
                _ => throw new System.Exception($"Unexpected callee type {node.Callee.Type}"),
            };
            var shape = _delegateShapes.Resolve(functionType, ToIlType);

            EmitExpression(il, node.Callee);
            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            il.Emit(IlOpCodeTable.Get("Callvirt"), shape.Invoke);
        }

        // ------------------------------------------------------------------
        // 表达式
        // ------------------------------------------------------------------

        private void EmitExpression(IlAssembler il, BoundExpression node)
        {
            if (node.ConstantValue != null)
            {
                EmitConstantExpression(il, node);
                return;
            }

            switch (node.Kind)
            {
                case BoundNodeKind.VariableExpression:
                    EmitVariableExpression(il, (BoundVariableExpression)node);
                    break;
                case BoundNodeKind.AssignmentExpression:
                    EmitAssignmentExpression(il, (BoundAssignmentExpression)node);
                    break;
                case BoundNodeKind.UnaryExpression:
                    EmitUnaryExpression(il, (BoundUnaryExpression)node);
                    break;
                case BoundNodeKind.BinaryExpression:
                    EmitBinaryExpression(il, (BoundBinaryExpression)node);
                    break;
                case BoundNodeKind.ConditionalExpression:
                    EmitConditionalExpression(il, (BoundConditionalExpression)node);
                    break;
                case BoundNodeKind.CallExpression:
                    EmitCallExpression(il, (BoundCallExpression)node);
                    break;
                case BoundNodeKind.ConversionExpression:
                    EmitConversionExpression(il, (BoundConversionExpression)node);
                    break;
                case BoundNodeKind.FormatExpression:
                    EmitFormatExpression(il, (BoundFormatExpression)node);
                    break;
                case BoundNodeKind.ArrayCreationExpression:
                    EmitArrayCreationExpression(il, (BoundArrayCreationExpression)node);
                    break;
                case BoundNodeKind.ElementAccessExpression:
                    EmitElementAccessExpression(il, (BoundElementAccessExpression)node);
                    break;
                case BoundNodeKind.ElementAssignmentExpression:
                    EmitElementAssignmentExpression(il, (BoundElementAssignmentExpression)node);
                    break;
                case BoundNodeKind.MemberAccessExpression:
                    EmitMemberAccessExpression(il, (BoundMemberAccessExpression)node);
                    break;
                case BoundNodeKind.MemberCallExpression:
                    EmitMemberCallExpression(il, (BoundMemberCallExpression)node);
                    break;
                case BoundNodeKind.MemberAssignmentExpression:
                    EmitMemberAssignmentExpression(il, (BoundMemberAssignmentExpression)node);
                    break;
                case BoundNodeKind.ObjectCreationExpression:
                    EmitObjectCreationExpression(il, (BoundObjectCreationExpression)node);
                    break;
                case BoundNodeKind.ThisExpression:
                    EmitThisExpression(il, (BoundThisExpression)node);
                    break;
                case BoundNodeKind.BaseExpression:
                    EmitThisExpression(il, new BoundThisExpression(node.Syntax, (NamedTypeSymbol)node.Type));
                    break;
                case BoundNodeKind.StaticTypeExpression:
                    break; // 静态类型引用：无实例值
                case BoundNodeKind.ConstructorChainExpression:
                    EmitConstructorChainExpression(il, (BoundConstructorChainExpression)node);
                    break;
                case BoundNodeKind.IsExpression:
                    EmitIsExpression(il, (BoundIsExpression)node);
                    break;
                case BoundNodeKind.AsExpression:
                    EmitAsExpression(il, (BoundAsExpression)node);
                    break;

                // 6e-M22 C4-b：函数值构造（ldnull/接收者; ldftn; newobj Func`N::.ctor）与间接调用（callvirt Invoke）
                case BoundNodeKind.FunctionValueExpression:
                    EmitFunctionValueExpression(il, (BoundFunctionValueExpression)node);
                    break;
                case BoundNodeKind.InvocationExpression:
                    EmitInvocationExpression(il, (BoundInvocationExpression)node);
                    break;
                case BoundNodeKind.ByRefArgument:
                    EmitByRefArgument(il, (BoundByRefArgument)node);
                    break;
                default:
                    throw new System.Exception($"Unexpected node kind {node.Kind}");
            }
        }

        /// <summary>6e-M19 M5-b：is → isinst + ldnull + cgt.un（C# 规范模式：非 null 引用 &gt; null）。</summary>
        private void EmitIsExpression(IlAssembler il, BoundIsExpression node)
        {
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Isinst"), ToIlType(node.TargetType));
            il.Emit(IlOpCodeTable.Get("Ldnull"));
            il.Emit(IlOpCodeTable.Get("Cgt_Un"));
        }

        /// <summary>6e-M19 M5-b：as → isinst（失败栈上即 null，与 C# 语义一致）。</summary>
        private void EmitAsExpression(IlAssembler il, BoundAsExpression node)
        {
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Isinst"), ToIlType(node.TargetType));
        }

        /// <summary>
        /// 6e-M21 Phase 4：数值↔数值转换的系统化 CIL 发射。
        /// 栈表示：≤32 位整数均为 int32 栈；i64/u64 为 int64 栈；f32/f64 为 F 栈。
        /// 无符号宽整型转浮点先归位（Conv_U4/Conv_U8）再转，保证大值正确。
        /// </summary>
        private bool TryEmitNumericConversion(IlAssembler il, TypeSymbol from, TypeSymbol to)
        {
            if (to.IsPlaceholder128 || from.IsPlaceholder128)
            {
                return false;
            }

            var fromIsNumericLike = from.IsNumeric || from == TypeSymbol.Char || from is NamedTypeSymbol { TypeKind: TypeKind.Enum };
            if (!to.IsNumeric || !fromIsNumericLike)
            {
                return false;
            }

            if (from == TypeSymbol.String || to == TypeSymbol.String)
            {
                return false; // 字符串互转走原有专用路径
            }

            switch (to.Name)
            {
                case "sbyte":
                    il.Emit(IlOpCodeTable.Get("Conv_I1"));
                    return true;
                case "byte":
                    il.Emit(IlOpCodeTable.Get("Conv_U1"));
                    return true;
                case "short":
                    il.Emit(IlOpCodeTable.Get("Conv_I2"));
                    return true;
                case "ushort":
                    il.Emit(IlOpCodeTable.Get("Conv_U2"));
                    return true;
                case "int":
                    if (from == TypeSymbol.Int64)
                        il.Emit(IlOpCodeTable.Get("Conv_I4"));
                    else if (from == TypeSymbol.UInt64)
                        il.Emit(IlOpCodeTable.Get("Conv_U4"));
                    else if (from.IsFloat)
                        il.Emit(IlOpCodeTable.Get("Conv_I4"));
                    // ≤32 位整数/char/enum → int：栈同宽，无需指令
                    return true;
                case "uint":
                    if (from == TypeSymbol.Int64 || from == TypeSymbol.UInt64 || from.IsFloat)
                        il.Emit(IlOpCodeTable.Get("Conv_U4"));
                    return true;
                case "long":
                    if (from != TypeSymbol.Int64 && from != TypeSymbol.UInt64)
                    {
                        if (from == TypeSymbol.UInt32 || from == TypeSymbol.UInt16 || from == TypeSymbol.UInt8)
                        {
                            // 零扩展到 int64 栈
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_I8"));
                        }
                    }

                    return true;
                case "ulong":
                    if (from != TypeSymbol.Int64 && from != TypeSymbol.UInt64)
                    {
                        if (from == TypeSymbol.Int8 || from == TypeSymbol.Int16 ||
                            from == TypeSymbol.Int32 || from == TypeSymbol.Char ||
                            from is NamedTypeSymbol { TypeKind: TypeKind.Enum })
                        {
                            // 符号扩展位模式进入 int64 栈
                            il.Emit(IlOpCodeTable.Get("Conv_I8"));
                        }
                        else if (from.IsFloat)
                        {
                            // 浮点→u64：C# 语义为截断取整后按 ulong 解释
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                    }

                    return true;
                case "float":
                    if (from == TypeSymbol.Double)
                    {
                        il.Emit(IlOpCodeTable.Get("Conv_R4"));
                    }
                    else if (!from.IsFloat)
                    {
                        if (from == TypeSymbol.UInt64)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else if (from == TypeSymbol.UInt32)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U4"));
                        }

                        il.Emit(IlOpCodeTable.Get("Conv_R4"));
                    }

                    return true;
                case "double":
                    if (from == TypeSymbol.Float)
                    {
                        il.Emit(IlOpCodeTable.Get("Conv_R8"));
                    }
                    else if (!from.IsFloat)
                    {
                        if (from == TypeSymbol.UInt64)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else if (from == TypeSymbol.UInt32)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U4"));
                        }

                        il.Emit(IlOpCodeTable.Get("Conv_R8"));
                    }

                    return true;
            }

            return false;
        }

        private void EmitConstantExpression(IlAssembler il, BoundExpression node)
        {
            if (node.ConstantValue!.Value == null)
            {
                il.Emit(IlOpCodeTable.Get("Ldnull"));
            }
            else if (node.Type == TypeSymbol.Boolean)
            {
                var value = (bool)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get(value ? "Ldc_I4_1" : "Ldc_I4_0"));
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                var value = (int)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.Int64)
            {
                var value = (long)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldc_I8"), value);
            }
            else if (node.Type == TypeSymbol.Char)
            {
                var value = (int)(char)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.UInt8)
            {
                var value = Convert.ToInt32(node.ConstantValue.Value);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.Int8 ||
                     node.Type == TypeSymbol.Int16 ||
                     node.Type == TypeSymbol.UInt16 ||
                     node.Type == TypeSymbol.UInt32)
            {
                // 8/16/32 位整数在 CIL 栈上均为 int32
                var value = node.Type == TypeSymbol.UInt32
                    ? unchecked((int)(uint)node.ConstantValue.Value)
                    : System.Convert.ToInt32(node.ConstantValue.Value);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.UInt64)
            {
                var value = unchecked((long)(ulong)node.ConstantValue.Value);
                il.Emit(IlOpCodeTable.Get("Ldc_I8"), value);
            }
            else if (node.Type == TypeSymbol.Float)
            {
                var value = (float)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldc_R4"), value);
            }
            else if (node.Type == TypeSymbol.Double)
            {
                var value = (double)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldc_R8"), value);
            }
            else if (node.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                var value = (int)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.String)
            {
                var value = (string)node.ConstantValue.Value;
                il.Emit(IlOpCodeTable.Get("Ldstr"), value);
            }
            else
            {
                throw new System.Exception($"Unexpected constant expression kind {node.Kind}");
            }
        }

        private void EmitVariableExpression(IlAssembler il, BoundVariableExpression node)
        {
            // 6e-M22 C5-c：捕获变量读环境字段
            if (node.Variable.IsCaptured && _closureEnvLocalIndex.HasValue)
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                il.Emit(IlOpCodeTable.Get("Ldfld"), _closureFieldDefs![node.Variable.Name]);
                return;
            }

            if (node.Variable is ParameterSymbol parameter)
            {
                // 实例方法 arg0 = this，参数从 arg1 起
                var argIndex = parameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)argIndex);

                if (parameter.IsByRef)
                {
                    // 6e-M23 R6：byref 形参读 = 解引用
                    EmitLoadIndirect(il, node.Variable.Type);
                }
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_locals[node.Variable]);
            }
        }

        /// <summary>
        /// byref 实参取址（6e-M23 R6）：
        /// 形参/局部 → ldarga/ldloca；实例字段 → 接收者 + ldflda；静态字段 → ldsflda；
        /// 数组元素 → 数组 + 索引 + ldelema（CLR 自带越界检查）。字符串元素不可作 byref 目标（绑定层拒绝非数组元素访问?）
        /// ——string 索引为只读字符，绑定层 lvalue 校验已排除。
        /// </summary>
        private void EmitByRefArgument(IlAssembler il, BoundByRefArgument node)
        {
            switch (node.Expression)
            {
                case BoundVariableExpression variable:
                    if (variable.Variable is ParameterSymbol parameter)
                    {
                        var argIndex = parameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                        il.Emit(IlOpCodeTable.Get("Ldarga"), (ushort)argIndex);
                    }
                    else
                    {
                        il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)_locals[variable.Variable]);
                    }

                    return;

                case BoundMemberAccessExpression member when member.Field is { IsStatic: true } staticField:
                    il.Emit(IlOpCodeTable.Get("Ldsflda"), _fieldDefs[staticField]);
                    return;

                case BoundMemberAccessExpression member when member.Field != null:
                    EmitExpression(il, member.Target);
                    il.Emit(IlOpCodeTable.Get("Ldflda"), _fieldDefs[member.Field]);
                    return;

                case BoundElementAccessExpression element when element.Target.Type != TypeSymbol.String &&
                                                               element.Target.Type.ElementType != null:
                    EmitExpression(il, element.Target);
                    EmitExpression(il, element.Index);
                    il.Emit(IlOpCodeTable.Get("Ldelema"), _metadata.DefineTypeSpec(ToIlType(node.Type)));
                    return;

                default:
                    throw new System.Exception($"Unexpected by-ref argument target {node.Expression.Kind}");
            }
        }

        /// <summary>byref 解引用读（6e-M23 R6）：按元素类型选 ldind 变体。</summary>
        private void EmitLoadIndirect(IlAssembler il, TypeSymbol type)
        {
            var name = type switch
            {
                _ when type == TypeSymbol.Boolean || type == TypeSymbol.UInt8 => "Ldind_U1",
                _ when type == TypeSymbol.Int8 => "Ldind_I1",
                _ when type == TypeSymbol.UInt16 || type == TypeSymbol.Char => "Ldind_U2",
                _ when type == TypeSymbol.Int16 => "Ldind_I2",
                _ when type == TypeSymbol.Int32 || type == TypeSymbol.UInt32 => "Ldind_I4",
                _ when type == TypeSymbol.Int64 || type == TypeSymbol.UInt64 => "Ldind_I8",
                _ when type == TypeSymbol.Float => "Ldind_R4",
                _ when type == TypeSymbol.Double => "Ldind_R8",
                _ => "Ldind_Ref",
            };
            il.Emit(IlOpCodeTable.Get(name));
        }

        /// <summary>byref 间接写（6e-M23 R6）：栈顶为值、次顶为地址。</summary>
        private void EmitStoreIndirect(IlAssembler il, TypeSymbol type)
        {
            var name = type switch
            {
                _ when type == TypeSymbol.Boolean || type == TypeSymbol.UInt8 || type == TypeSymbol.Int8 => "Stind_I1",
                _ when type == TypeSymbol.UInt16 || type == TypeSymbol.Char || type == TypeSymbol.Int16 => "Stind_I2",
                _ when type == TypeSymbol.Int32 || type == TypeSymbol.UInt32 => "Stind_I4",
                _ when type == TypeSymbol.Int64 || type == TypeSymbol.UInt64 => "Stind_I8",
                _ when type == TypeSymbol.Float => "Stind_R4",
                _ when type == TypeSymbol.Double => "Stind_R8",
                _ => "Stind_Ref",
            };
            il.Emit(IlOpCodeTable.Get(name));
        }

        private void EmitAssignmentExpression(IlAssembler il, BoundAssignmentExpression node)
        {
            // 6e-M23 R6：byref 形参目标 = 值存临时局部 → 取址 → 值+stind（避免 dup 与托管指针在栈上交叠，
            // RyuJIT 对该形态的优化会产生错误寻址；临时局部方案与 csc 同构）
            if (node.Variable is ParameterSymbol { IsByRef: true } byRefParameter)
            {
                var temporaryLocal = AllocateTemporaryLocal(node);
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);

                var argIndex = byRefParameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)argIndex);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                EmitStoreIndirect(il, node.Variable.Type);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                return;
            }

            // 6e-M22 C5-c：捕获变量写环境字段（值同时在栈顶作为表达式结果）
            if (node.Variable.IsCaptured && _closureEnvLocalIndex.HasValue)
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                il.Emit(IlOpCodeTable.Get("Stfld"), _closureFieldDefs![node.Variable.Name]);
                return;
            }

            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)_locals[node.Variable]);
        }

        private void EmitUnaryExpression(IlAssembler il, BoundUnaryExpression node)
        {
            EmitExpression(il, node.Operand);

            if (node.Op.Kind == BoundUnaryOperatorKind.Identity)
            {
                // Done
            }
            else if (node.Op.Kind == BoundUnaryOperatorKind.LogicalNegation)
            {
                il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                il.Emit(IlOpCodeTable.Get("Ceq"));
            }
            else if (node.Op.Kind == BoundUnaryOperatorKind.Negation)
            {
                il.Emit(IlOpCodeTable.Get("Neg"));
            }
            else if (node.Op.Kind == BoundUnaryOperatorKind.OnesComplement)
            {
                il.Emit(IlOpCodeTable.Get("Not"));
            }
            else
            {
                throw new System.Exception($"Unexpected unary operator {SyntaxFacts.GetText(node.Op.SyntaxKind)}({node.Operand.Type})");
            }
        }

        private void EmitBinaryExpression(IlAssembler il, BoundBinaryExpression node)
        {
            if (node.Op.Kind == BoundBinaryOperatorKind.Addition)
            {
                if (node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.String)
                {
                    EmitStringConcatExpression(il, node);
                    return;
                }

                if (node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.Double)
                {
                    EmitExpression(il, node.Left);
                    EmitExpression(il, node.Right);
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Double"));
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.StringConcat2);
                    return;
                }
            }

            EmitExpression(il, node.Left);
            EmitExpression(il, node.Right);

            if (node.Op.Kind == BoundBinaryOperatorKind.Equals)
            {
                if (node.Left.Type == TypeSymbol.Any && node.Right.Type == TypeSymbol.Any ||
                    node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.String)
                {
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectEquals);
                    return;
                }
            }

            if (node.Op.Kind == BoundBinaryOperatorKind.NotEquals)
            {
                if (node.Left.Type == TypeSymbol.Any && node.Right.Type == TypeSymbol.Any ||
                    node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.String)
                {
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectEquals);
                    il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    return;
                }
            }

            // 6e-M21 Phase 4：无符号整数走 _un 变体（浮点保持有符号比较指令）
            var isUnsigned = node.Type.IsInteger && !node.Type.IsSigned && !node.Type.IsPlaceholder128;

            switch (node.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    il.Emit(IlOpCodeTable.Get("Add"));
                    break;
                case BoundBinaryOperatorKind.Subtraction:
                    il.Emit(IlOpCodeTable.Get("Sub"));
                    break;
                case BoundBinaryOperatorKind.Multiplication:
                    il.Emit(IlOpCodeTable.Get("Mul"));
                    break;
                case BoundBinaryOperatorKind.Division:
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Div_Un" : "Div"));
                    break;
                case BoundBinaryOperatorKind.Modulo:
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Rem_Un" : "Rem"));
                    break;
                case BoundBinaryOperatorKind.ShiftLeft:
                    il.Emit(IlOpCodeTable.Get("Shl"));
                    break;
                case BoundBinaryOperatorKind.ShiftRight:
                    // Shr=算术右移；Shr_Un=逻辑右移（无符号类型）
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Shr_Un" : "Shr"));
                    break;
                case BoundBinaryOperatorKind.LogicalAnd:
                case BoundBinaryOperatorKind.BitwiseAnd:
                    il.Emit(IlOpCodeTable.Get("And"));
                    break;
                case BoundBinaryOperatorKind.LogicalOr:
                case BoundBinaryOperatorKind.BitwiseOr:
                    il.Emit(IlOpCodeTable.Get("Or"));
                    break;
                case BoundBinaryOperatorKind.BitwiseXor:
                    il.Emit(IlOpCodeTable.Get("Xor"));
                    break;
                case BoundBinaryOperatorKind.Equals:
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.NotEquals:
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    break;

                // 6e-M19 M2-c：类类型引用相等——ceq 对栈上引用即指针比较（值语义走 Equals 分支不受影响）
                case BoundBinaryOperatorKind.ReferenceEquals:
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.Less:
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Clt_Un" : "Clt"));
                    break;
                case BoundBinaryOperatorKind.LessOrEquals:
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Cgt_Un" : "Cgt"));
                    il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.Greater:
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Cgt_Un" : "Cgt"));
                    break;
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    il.Emit(IlOpCodeTable.Get(isUnsigned ? "Clt_Un" : "Clt"));
                    il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodeTable.Get("Ceq"));
                    break;
                default:
                    throw new System.Exception($"Unexpected binary operator {SyntaxFacts.GetText(node.Op.SyntaxKind)}({node.Left.Type}, {node.Right.Type})");
            }
        }

        private void EmitConditionalExpression(IlAssembler il, BoundConditionalExpression node)
        {
            var elseLabel = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
            var endLabel = new IlInstruction(IlOpCodeTable.Get("Nop"), null);

            EmitExpression(il, node.Condition);
            il.Emit(IlOpCodeTable.Get("Brfalse"), elseLabel);
            EmitExpression(il, node.WhenTrue);
            il.Emit(IlOpCodeTable.Get("Br"), endLabel);
            il.Emit(elseLabel);
            EmitExpression(il, node.WhenFalse);
            il.Emit(endLabel);
        }

        private void EmitStringConcatExpression(IlAssembler il, BoundBinaryExpression node)
        {
            var nodes = FoldConstants(node.Syntax, Flatten(node)).ToList();

            switch (nodes.Count)
            {
                case 0:
                    il.Emit(IlOpCodeTable.Get("Ldstr"), string.Empty);
                    break;
                case 1:
                    EmitExpression(il, nodes[0]);
                    break;
                case 2:
                    EmitExpression(il, nodes[0]);
                    EmitExpression(il, nodes[1]);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.StringConcat2);
                    break;
                case 3:
                    EmitExpression(il, nodes[0]);
                    EmitExpression(il, nodes[1]);
                    EmitExpression(il, nodes[2]);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.StringConcat3);
                    break;
                case 4:
                    EmitExpression(il, nodes[0]);
                    EmitExpression(il, nodes[1]);
                    EmitExpression(il, nodes[2]);
                    EmitExpression(il, nodes[3]);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.StringConcat4);
                    break;
                default:
                    il.Emit(IlOpCodeTable.Get("Ldc_I4"), nodes.Count);
                    il.Emit(IlOpCodeTable.Get("Newarr"), _framework.StringType);
                    for (var i = 0; i < nodes.Count; i++)
                    {
                        il.Emit(IlOpCodeTable.Get("Dup"));
                        il.Emit(IlOpCodeTable.Get("Ldc_I4"), i);
                        EmitExpression(il, nodes[i]);
                        il.Emit(IlOpCodeTable.Get("Stelem_Ref"));
                    }

                    il.Emit(IlOpCodeTable.Get("Call"), _framework.StringConcatArray);
                    break;
            }

            static IEnumerable<BoundExpression> Flatten(BoundExpression node)
            {
                if (node is BoundBinaryExpression binaryExpression &&
                    binaryExpression.Op.Kind == BoundBinaryOperatorKind.Addition &&
                    binaryExpression.Left.Type == TypeSymbol.String &&
                    binaryExpression.Right.Type == TypeSymbol.String)
                {
                    foreach (var result in Flatten(binaryExpression.Left))
                    {
                        yield return result;
                    }

                    foreach (var result in Flatten(binaryExpression.Right))
                    {
                        yield return result;
                    }
                }
                else
                {
                    if (node.Type != TypeSymbol.String)
                    {
                        throw new System.Exception($"Unexpected node type in string concatenation: {node.Type}");
                    }

                    yield return node;
                }
            }

            static IEnumerable<BoundExpression> FoldConstants(SyntaxNode syntax, IEnumerable<BoundExpression> nodes)
            {
                System.Text.StringBuilder? stringBuilder = null;
                foreach (var node in nodes)
                {
                    if (node.ConstantValue != null)
                    {
                        var stringValue = (string)node.ConstantValue.Value;
                        if (string.IsNullOrEmpty(stringValue))
                        {
                            continue;
                        }

                        stringBuilder ??= new System.Text.StringBuilder();
                        stringBuilder.Append(stringValue);
                    }
                    else
                    {
                        if (stringBuilder?.Length > 0)
                        {
                            yield return new BoundLiteralExpression(syntax, stringBuilder.ToString());
                            stringBuilder.Clear();
                        }

                        yield return node;
                    }
                }

                if (stringBuilder?.Length > 0)
                {
                    yield return new BoundLiteralExpression(syntax, stringBuilder.ToString());
                }
            }
        }

        private void EmitCallExpression(IlAssembler il, BoundCallExpression node)
        {
            if (node.Function.BuiltinKind != null)
            {
                EmitBuiltinCall(il, node.Function, node.Arguments);
                return;
            }

            // facade 类成员：优先重定向到 BCL（解析失败回退下方 codAssemblies 的 Cocoa 体）
            if (TryEmitFacadeBclCall(il, node))
            {
                return;
            }

            // 动态链接（阶段 A3）：cod 顶层函数 → <CocoaTopLevel>.MemberRef 外部调用
            if (_codAssemblies.TryGetValue(node.Function, out var codAssembly))
            {
                foreach (var argument in node.Arguments)
                {
                    EmitExpression(il, argument);
                }

                il.Emit(IlOpCodeTable.Get("Call"), CodMethodRef(node.Function, codAssembly));
                return;
            }

            var isStructInstance = node.Function.ContainingClass is { IsValueType: true } && !node.Function.IsStatic
                && node.Arguments.Length > 0 && node.Function.Parameters.Length > 0;
            foreach (var argument in node.Arguments)
            {
                if (isStructInstance && argument == node.Arguments[0])
                {
                    // struct 实例方法：this 按托管指针传参（ldarga/ldloca 或临时局部取址）
                    var receiverLocal = AllocateTemporaryLocal(argument, argument.Type);
                    EmitExpression(il, argument);
                    il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)receiverLocal);
                    il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)receiverLocal);
                }
                else
                {
                    EmitExpression(il, argument);
                }
            }

            var methodDefinition = _methods[node.Function];
            il.Emit(IlOpCodeTable.Get("Call"), methodDefinition);
        }

        /// <summary>
        /// facade 类成员在 IL 端重定向到 BCL：按 ContainingClass.FullName + 方法名 + 实参类型
        /// 调 _framework.FindMethod → callvirt/call 到 BCL；解析失败返回 false（调用方回退到
        /// codAssemblies 的 Cocoa 体）。泛型 facade 的重定向（直构 MemberRef）见后续实现。
        /// 规则见 docs-dev/对象模型设计.md §5.4。
        /// </summary>
        private bool IsFacadeRedirect(NamedTypeSymbol classType)
        {
            if (classType.IsFacadeClass) return true;
            if (classType is InstantiatedTypeSymbol inst && inst.GenericDefinition?.IsFacadeClass == true) return true;
            return false;
        }

        /// <summary>facade 类型运行期映射到的 BCL 全名：优先用 FacadeThisType（struct facade 由此提供 BCL 值类型名；
        /// class facade 的 FacadeThisType 即 BCL 目标，与自身 FullName 一致，故回退到 FullName 等价）。</summary>
        private string FacadeBclFullName(NamedTypeSymbol classType)
            => classType.FacadeThisType is NamedTypeSymbol nts ? nts.FullName : classType.FullName;

        private static bool IsValueTypeSymbol(TypeSymbol type)
            => type == TypeSymbol.Boolean || type == TypeSymbol.Int32 || type == TypeSymbol.Int64 || type == TypeSymbol.Char ||
               type == TypeSymbol.UInt8 || type == TypeSymbol.Double || type is NamedTypeSymbol { TypeKind: TypeKind.Enum } ||
               type is NamedTypeSymbol { IsFacadeClass: true, TypeKind: TypeKind.Struct } ||
               type == TypeSymbol.Int8 || type == TypeSymbol.Int16 || type == TypeSymbol.UInt16 ||
               type == TypeSymbol.UInt32 || type == TypeSymbol.UInt64 || type == TypeSymbol.Float;

        /// <summary>
        /// facade BCL 调用时计算实参的 IL 类型序列（用于 FindMethod 形参签名 / 泛型直构 MemberRef）。
        /// arguments 不含实例方法的 this 接收者（其位于 node.Expression）；对应形参下标整体右移 1。
        /// byref 形参（out/ref）追加 &（IlType.ByRefOf），与方法真实签名一致。
        /// </summary>
        private IlType[] GetFacadeArgumentIlTypes(FunctionSymbol method, bool isInstance, IEnumerable<BoundExpression> arguments)
        {
            var args = arguments.ToList();
            var argOffset = isInstance ? 1 : 0;
            var types = new IlType[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                var p = method.Parameters[i + argOffset];
                var t = ToIlType(args[i].Type);
                if (p.IsByRef)
                {
                    t = IlType.ByRefOf(t);
                }
                types[i] = t;
            }
            return types;
        }

        /// <summary>
        /// 发射 facade 实例调用的接收者：
        /// 引用类型直接入栈 + Callvirt；值类型存入临时局部后取地址（ldloca）+ Call（非虚，this 按托管指针传参）。
        /// </summary>
        private void EmitFacadeInstanceReceiver(IlAssembler il, BoundExpression receiver)
        {
            if (IsValueTypeSymbol(receiver.Type))
            {
                var local = AllocateTemporaryLocal(receiver);
                EmitExpression(il, receiver);
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)local);
                il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)local);
            }
            else
            {
                EmitExpression(il, receiver);
            }
        }

        private bool TryEmitFacadeBclCall(IlAssembler il, BoundCallExpression node)
        {
            var fn = node.Function;
            var cc = fn.ContainingClass;
            if (cc == null || !IsFacadeRedirect(cc)) return false;

            // facade 实例方法已降级为静态（首参 = this）；真正静态方法无 this 首参。
            // this 标记经 .cod 序列化保留（IsThisParameter ⇔ IsReadOnly）。
            var isInstance = fn.Parameters.Length > 0 && fn.Parameters[0].IsThisParameter;
            var methodArgs = isInstance ? node.Arguments.Skip(1) : node.Arguments;

            IlMethodRef? methodRef;
            if (cc is InstantiatedTypeSymbol inst)
            {
                // 泛型 facade：直构 MemberRef（绕过 MetadataReader 对 GENERICINST 的解析缺口）
                methodRef = ResolveFacadeGenericMethodRef(inst, fn, methodArgs, isInstance);
            }
            else
            {
                var argTypeNames = GetFacadeArgumentIlTypes(fn, isInstance, methodArgs).Select(t => t.FullName).ToArray();
                methodRef = _framework.FindMethod(FacadeBclFullName(cc), fn.Name, argTypeNames);
            }

            if (methodRef == null) return false;

            if (isInstance) EmitFacadeInstanceReceiver(il, node.Arguments[0]);

            foreach (var a in methodArgs) EmitExpression(il, a);
            var callOp = !isInstance || IsValueTypeSymbol(node.Arguments[0].Type) ? "Call" : "Callvirt";
            il.Emit(IlOpCodeTable.Get(callOp), methodRef);
            return true;
        }

        private IlMethodRef? ResolveFacadeGenericMethodRef(InstantiatedTypeSymbol inst, FunctionSymbol fn, IEnumerable<BoundExpression> methodArgs, bool isInstance)
        {
            var def = inst.GenericDefinition!;
            var openName = def.FullName + "`" + def.TypeParameters.Length;
            var genericDef = _framework.RequireType(openName);
            var declaringSpec = _metadata.DefineTypeSpec(IlType.GenericInstance(genericDef, inst.TypeArguments.Select(ToIlType).ToArray()));
            var returnIlType = ToFacadeIlType(fn.ReturnType, inst);
            var args = methodArgs.ToList();
            var argOffset = isInstance ? 1 : 0;
            var paramIlTypes = args.Select((a, i) =>
            {
                var p = fn.Parameters[i + argOffset];
                var t = ToFacadeIlType(a.Type, inst);
                if (p.IsByRef) t = IlType.ByRefOf(t);
                return t;
            }).ToArray();
            return _metadata.DefineMethodRef(declaringSpec, fn.Name, returnIlType, paramIlTypes, isStatic: !isInstance);
        }

        private IlMethodRef? ResolveFacadeCtor(NamedTypeSymbol classType, ImmutableArray<BoundExpression> arguments)
        {
            var paramTypes = arguments.Select(a => ToIlType(a.Type)).ToArray();
            if (classType is InstantiatedTypeSymbol inst)
            {
                var def = inst.GenericDefinition!;
                var openName = FacadeBclFullName(def) + "`" + def.TypeParameters.Length;
                var genericDef = _framework.RequireType(openName);
                var declaringSpec = _metadata.DefineTypeSpec(IlType.GenericInstance(genericDef, inst.TypeArguments.Select(ToIlType).ToArray()));
                return _metadata.DefineMethodRef(declaringSpec, ".ctor", IlType.Void, paramTypes, isStatic: false);
            }

            var parameterNames = arguments.Select(a => ToIlType(a.Type).FullName).ToArray();
            return _framework.FindMethod(FacadeBclFullName(classType), ".ctor", parameterNames);
        }

        private IlType ToFacadeIlType(TypeSymbol type, InstantiatedTypeSymbol inst)
        {
            if (type is TypeParameterSymbol tp)
            {
                var def = inst.GenericDefinition!;
                for (var i = 0; i < def.TypeParameters.Length; i++)
                {
                    if (def.TypeParameters[i] == tp) return ToIlType(inst.TypeArguments[i]);
                }

                return ToIlType(type);
            }

            if (type.ElementType != null)
            {
                return IlType.SzArrayOf(ToFacadeIlType(type.ElementType, inst));
            }

            return ToIlType(type);
        }


        private void EmitBuiltinCall(IlAssembler il, FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            if (function.BuiltinKind == BuiltinKind.Random)
            {
                // 6d-4：Random.get_Shared 是 .NET 6+ API，mscorlib 没有；改用 new Random() 双运行时兼容。
                il.Emit(IlOpCodeTable.Get("Newobj"), _framework.RandomCtor);
                foreach (var argument in arguments)
                {
                    EmitExpression(il, argument);
                }

                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.RandomNext);
                return;
            }

            foreach (var argument in arguments)
            {
                EmitExpression(il, argument);
            }

            switch (function.BuiltinKind)
            {
                case BuiltinKind.WriteLine:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleWriteLine);
                    break;
                case BuiltinKind.Write:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleWrite);
                    break;
                case BuiltinKind.ReadLine:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleReadLine);
                    break;
                case BuiltinKind.ReadKey:
                    // Console.ReadKey(intercept) → ConsoleKeyInfo（struct 栈值）→ box 后 callvirt get_KeyChar → char
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleReadKey);
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.ConsoleKeyInfoType);
                    il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.ConsoleKeyInfoKeyChar);
                    break;
                case BuiltinKind.Sleep:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ThreadSleep);
                    break;
                case BuiltinKind.TickCount:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.EnvironmentTickCount);
                    break;
                case BuiltinKind.Exit:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.EnvironmentExit);
                    break;
                case BuiltinKind.Sqrt:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathSqrt);
                    break;
                case BuiltinKind.Floor:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathFloor);
                    break;
                case BuiltinKind.Ceiling:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathCeiling);
                    break;
                case BuiltinKind.Truncate:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathTruncate);
                    break;
                case BuiltinKind.Round:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathRound);
                    break;
                case BuiltinKind.Beep:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleBeep);
                    break;
                case BuiltinKind.Int32ToString:
                case BuiltinKind.UInt64ToString:
                    // box 值（框架 TypeRef）→ Convert.ToString(object)
                    il.Emit(
                        IlOpCodeTable.Get("Box"),
                        function.BuiltinKind == BuiltinKind.Int32ToString ? (object)_framework.Int32Type : _framework.UInt64Type);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    break;
                case BuiltinKind.Int64ToString:
                    il.Emit(
                        IlOpCodeTable.Get("Box"),
                        (object)_framework.Int64Type);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    break;
                case BuiltinKind.DoubleToString:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToStringDouble);
                    break;
                case BuiltinKind.BooleanToString:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToStringBoolean);
                    break;
                case BuiltinKind.CharToString:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToStringChar);
                    break;
                case BuiltinKind.ParseInt64:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToInt64FromString);
                    break;
                case BuiltinKind.StringFromChars:
                    // 6e-G7 ③a：new string(char[])
                    il.Emit(IlOpCodeTable.Get("Newobj"), _framework.StringCtorCharArray);
                    break;
                case BuiltinKind.Sha256Hash:
                    // 6e-G7 ⑤a：native+IL 接入待 IlFramework 惰性引用基础设施就绪
                    throw new Exception("Sha256Hash IL emission requires lazy framework references (G7-⑤a follow-up)");
                case BuiltinKind.FileReadAllText:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "ReadAllText", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.File.ReadAllText not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileWriteAllText:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "WriteAllText", new[] { "System.String", "System.String" });
                    if (m == null) throw new Exception("System.IO.File.WriteAllText not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileExists:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "Exists", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.File.Exists not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.GetEnvironmentVariable:
                {
                    var m = _framework.ResolveMethod("System.Environment", "GetEnvironmentVariable", new[] { "System.String" });
                    if (m == null) throw new Exception("System.Environment.GetEnvironmentVariable not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.GetCurrentDirectory:
                {
                    var m = _framework.ResolveMethod("System.Environment", "get_CurrentDirectory", Array.Empty<string>());
                    if (m == null) throw new Exception("System.Environment.CurrentDirectory not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileDelete:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "Delete", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.File.Delete not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileCopy:
                {
                    // File.Copy(src, dst, overwrite=true)
                    var src = _framework.ResolveMethod("System.IO.File", "Copy", new[] { "System.String", "System.String", "System.Boolean" });
                    if (src == null) throw new Exception("System.IO.File.Copy not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), src);
                    break;
                }
                case BuiltinKind.DirectoryExists:
                {
                    var m = _framework.ResolveMethod("System.IO.Directory", "Exists", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.Directory.Exists not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.SetCurrentDirectory:
                {
                    var m = _framework.ResolveMethod("System.Environment", "SetCurrentDirectory", new[] { "System.String" });
                    if (m == null) throw new Exception("System.Environment.SetCurrentDirectory not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.GetExecutablePath:
                {
                    // AppContext.BaseDirectory 作为可执行文件路径的近似
                    var m = _framework.ResolveMethod("AppContext", "get_BaseDirectory", Array.Empty<string>());
                    if (m == null) throw new Exception("AppContext.BaseDirectory not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }

                // 6e-M19 M2-c：System.Object 静态方法（Object.Equals(a,b) / Object.ReferenceEquals(a,b)，参数 any→object）
                case BuiltinKind.ObjectStaticEquals:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectEquals);
                    break;
                case BuiltinKind.ObjectReferenceEquals:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectReferenceEquals);
                    break;
                default:
                    throw new Exception($"Unknown builtin kind {function.BuiltinKind}");
            }
        }

        private void EmitConversionExpression(IlAssembler il, BoundConversionExpression node)
        {
            EmitExpression(il, node.Expression);

            // 6e-M19 M5-a：null 字面量 → 引用型（类/接口/string/数组/any）——栈上已是 ldnull，直通
            if (node.Expression.Type == TypeSymbol.Null)
            {
                return;
            }

            // 6e-M21 Phase 4：数值↔数值系统化转换（含 char/enum 源），命中即返回
            if (TryEmitNumericConversion(il, node.Expression.Type, node.Type))
            {
                return;
            }

            if (node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.String)
            {
                var type = _framework.RequireType("System.Char");
                il.Emit(IlOpCodeTable.Get("Box"), type);
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.UInt8 && node.Type == TypeSymbol.Int32)
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.UInt8)
            {
                // 无符号字节截断，与 C# (byte)300 == 44 语义一致
                il.Emit(IlOpCodeTable.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Double ||
                node.Expression.Type == TypeSymbol.UInt8 && node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Conv_R8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Conv_R8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.Int64)
            {
                // 与 C# 一致：截断取整
                il.Emit(IlOpCodeTable.Get("Conv_I8"));
                return;
            }

            if (node.Expression.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum } && node.Type == TypeSymbol.Int64 ||
                node.Expression.Type == TypeSymbol.UInt8 && node.Type == TypeSymbol.Int64 ||
                node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Conv_I8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Int64)
            {
                // 符号扩展（C# int→long 隐式）
                il.Emit(IlOpCodeTable.Get("Conv_I8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodeTable.Get("Conv_I4"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.Char)
            {
                il.Emit(IlOpCodeTable.Get("Conv_U2"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Int64"));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            // 6e-M21 Phase 7：其余数值类型 → string（装箱 + Convert.ToString）
            if ((node.Expression.Type.IsInteger && node.Expression.Type != TypeSymbol.Boolean) &&
                !node.Expression.Type.IsPlaceholder128 && node.Type == TypeSymbol.String)
            {
                var boxedName = node.Expression.Type == TypeSymbol.Int8 ? "System.SByte"
                    : node.Expression.Type == TypeSymbol.Int16 ? "System.Int16"
                    : node.Expression.Type == TypeSymbol.UInt16 ? "System.UInt16"
                    : node.Expression.Type == TypeSymbol.UInt32 ? "System.UInt32"
                    : "System.UInt64";
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType(boxedName));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Float && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Single"));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.String && node.Type == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToInt64);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodeTable.Get("Conv_I4"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Double"));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Char ||
                node.Expression.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum } && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            if (node.Expression.Type is NamedTypeSymbol fromClass && node.Type is NamedTypeSymbol toClass)
            {
                if (toClass.IsInterface &&
                    (fromClass == toClass || fromClass.IsBaseOf(toClass) || fromClass.GetAllInterfaces().Contains(toClass)))
                {
                    // 类/接口 → 其实现的接口（含继承链）：引用转换，栈上引用不变
                    return;
                }

                if (fromClass.IsInterface &&
                    (toClass.IsBaseOf(fromClass) || toClass.GetAllInterfaces().Contains(fromClass)))
                {
                    // 接口 → 类：显式向下引用转换（castclass）
                    il.Emit(IlOpCodeTable.Get("Castclass"), ToIlType(toClass));
                    return;
                }

                if (!toClass.IsInterface && toClass.IsBaseOf(fromClass))
                {
                    // 派生类 → 基类（6e-M19 M2-c 方向修正）：引用转换，栈上引用不变
                    return;
                }

                if (!fromClass.IsInterface && !toClass.IsInterface && fromClass.IsBaseOf(toClass))
                {
                    // 基类 → 派生类：显式向下引用转换（castclass）
                    il.Emit(IlOpCodeTable.Get("Castclass"), ToIlType(toClass));
                    return;
                }
            }

            EmitBoxIfValueType(il, node.Expression.Type);

            if (node.Type == TypeSymbol.Any)
            {
                // Done
            }
            else if (node.Type == TypeSymbol.Boolean)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToBoolean);
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToInt32);
            }
            else if (node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
            }
            else
            {
                throw new System.Exception($"Unexpected conversion from {node.Expression.Type} to {node.Type}");
            }
        }

        /// <summary>值类型（bool/int/long/char/byte/double/短整型/浮点/枚举）→ 装箱为 System.Object 参数。</summary>
        private void EmitBoxIfValueType(IlAssembler il, TypeSymbol type)
        {
            if (type != TypeSymbol.Boolean && type != TypeSymbol.Int32 && type != TypeSymbol.Int64 && type != TypeSymbol.Char &&
                type != TypeSymbol.UInt8 && type != TypeSymbol.Double && type is not NamedTypeSymbol { TypeKind: TypeKind.Enum } &&
                type != TypeSymbol.Int8 && type != TypeSymbol.Int16 && type != TypeSymbol.UInt16 &&
                type != TypeSymbol.UInt32 && type != TypeSymbol.UInt64 && type != TypeSymbol.Float)
            {
                return;
            }

            var boxed = type == TypeSymbol.Boolean ? "System.Boolean"
                : type == TypeSymbol.Int32 ? "System.Int32"
                : type == TypeSymbol.Int64 ? "System.Int64"
                : type == TypeSymbol.Char ? "System.Char"
                : type == TypeSymbol.UInt8 ? "System.Byte"
                : type == TypeSymbol.Int8 ? "System.SByte"
                : type == TypeSymbol.Int16 ? "System.Int16"
                : type == TypeSymbol.UInt16 ? "System.UInt16"
                : type == TypeSymbol.UInt32 ? "System.UInt32"
                : type == TypeSymbol.UInt64 ? "System.UInt64"
                : type == TypeSymbol.Float ? "System.Single"
                : type == TypeSymbol.Double ? "System.Double"
                : "System.Int32"; // 枚举底层 int
            il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType(boxed));
        }

        private void EmitFormatExpression(IlAssembler il, BoundFormatExpression node)
        {
            var format = "{" + 0;
            if (node.Width != null)
            {
                format += "," + node.Width;
            }

            if (node.Format != null)
            {
                format += ":" + node.Format;
            }

            format += "}";

            il.Emit(IlOpCodeTable.Get("Ldstr"), format);
            EmitExpression(il, node.Value);
            EmitBoxIfValueType(il, node.Value.Type);
            il.Emit(IlOpCodeTable.Get("Call"), _framework.StringFormat);
        }

        private void EmitArrayCreationExpression(IlAssembler il, BoundArrayCreationExpression node)
        {
            EmitExpression(il, node.Length);

            var elementType = node.Type.ElementType!;
            if (IsReferenceElement(elementType))
            {
                // 6e-M22 C5+ 多播事件：类/delegate/函数类型元素数组 —— 类走 TypeDef/TypeRef，
                // 泛型实例化（Func\`N）经 TypeSpec 表注册后回填 token。
                il.Emit(IlOpCodeTable.Get("Newarr"), OperandForTypeToken(ToIlType(elementType)));
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Newarr"), _framework.RequireType(PrimitiveArrayElementTypeName(elementType)));
            }

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), i);
                EmitExpression(il, node.Initializers[i]);
                EmitElementStore(il, elementType);
            }
        }

        /// <summary>InlineType 操作数（Newarr 等）：泛型实例化必须注册为 TypeSpec（其 .Reference 指向的是
        /// 泛型定义 TypeRef，直接回填会得到错误 token）；TypeDef/TypeRef 直用；String 标记型映射框架 TypeRef。</summary>
        private object OperandForTypeToken(IlType type)
        {
            if (type.Kind == IlTypeKind.GenericInst)
            {
                return _metadata.DefineTypeSpec(type);
            }

            if (type.TypeDef != null || type.Reference != null)
            {
                return type;
            }

            if (type.Kind == IlTypeKind.String)
            {
                return _framework.RequireType("System.String");
            }

            return _metadata.DefineTypeSpec(type);
        }

        /// <summary>基元值类型元素的 Newarr 框架类型名（enum 按 int32 表示）。</summary>
        private string PrimitiveArrayElementTypeName(TypeSymbol elementType)
        {
            return elementType switch
            {
                _ when elementType == TypeSymbol.Int32 => "System.Int32",
                _ when elementType == TypeSymbol.Int64 => "System.Int64",
                _ when elementType == TypeSymbol.Char => "System.Char",
                _ when elementType == TypeSymbol.UInt8 => "System.Byte",
                _ when elementType == TypeSymbol.Double => "System.Double",
                _ when elementType == TypeSymbol.Boolean => "System.Boolean",
                _ when elementType is NamedTypeSymbol { TypeKind: TypeKind.Enum } => "System.Int32",
                _ => throw new System.NotSupportedException($"Array of '{elementType}' is not yet supported by the IL emitter."),
            };
        }

        /// <summary>引用型元素判定：非基元值类型（含函数类型 / delegate 类 / 用户类 / string / 数组 / Object/any）一律按 ref 存取。</summary>
        private static bool IsReferenceElement(TypeSymbol elementType)
        {
            if (elementType == TypeSymbol.Boolean || elementType == TypeSymbol.UInt8 || elementType == TypeSymbol.Int8 ||
                elementType == TypeSymbol.Int16 || elementType == TypeSymbol.UInt16 ||
                elementType == TypeSymbol.Int32 || elementType == TypeSymbol.UInt32 ||
                elementType == TypeSymbol.Int64 || elementType == TypeSymbol.UInt64 ||
                elementType == TypeSymbol.Char || elementType == TypeSymbol.Float || elementType == TypeSymbol.Double)
            {
                return false;
            }

            if (elementType is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return false;
            }

            return true;
        }

        private void EmitElementAccessExpression(IlAssembler il, BoundElementAccessExpression node)
        {
            EmitExpression(il, node.Target);
            EmitExpression(il, node.Index);

            if (node.Target.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringChars);
                return;
            }

            if (IsReferenceElement(node.Type))
            {
                // 6e-M22 C5+ 多播事件：函数值/delegate/类元素数组按引用加载
                il.Emit(IlOpCodeTable.Get("Ldelem_Ref"));
            }
            else if (node.Type == TypeSymbol.Char)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_U2"));
            }
            else if (node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_R8"));
            }
            else if (node.Type == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_I8"));
            }
            else if (node.Type == TypeSymbol.Boolean || node.Type == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_U1"));
            }
            else if (node.Type.ElementType != null)
            {
                throw new System.NotSupportedException("Jagged arrays are not yet supported by the IL emitter.");
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_I4"));
            }
        }

        private void EmitElementAssignmentExpression(IlAssembler il, BoundElementAssignmentExpression node)
        {
            var temporaryLocal = AllocateTemporaryLocal(node);

            EmitExpression(il, node.Target.Target);
            EmitExpression(il, node.Target.Index);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
            EmitElementStore(il, node.Type);
            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private int AllocateTemporaryLocal(BoundExpression node, TypeSymbol? typeOverride = null)
        {
            if (!_temporaryLocalIndices.TryGetValue(node, out var index))
            {
                index = _currentFunctionLocals!.Count;
                _temporaryLocalIndices.Add(node, index);
                _currentFunctionLocals.Add(ToIlType(typeOverride ?? node.Type));
            }

            return index;
        }

        private static void EmitElementStore(IlAssembler il, TypeSymbol elementType)
        {
            if (IsReferenceElement(elementType))
            {
                // 6e-M22 C5+ 多播事件：函数值/delegate/类元素数组按引用存储
                il.Emit(IlOpCodeTable.Get("Stelem_Ref"));
            }
            else if (elementType == TypeSymbol.Boolean || elementType == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I1"));
            }
            else if (elementType == TypeSymbol.Char)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I2"));
            }
            else if (elementType == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_R8"));
            }
            else if (elementType == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I8"));
            }
            else if (elementType.ElementType != null)
            {
                throw new System.NotSupportedException("Jagged arrays are not yet supported by the IL emitter.");
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I4"));
            }
        }

        /// <summary>
        /// struct 字段访问/赋值取址（6e-M26 值语义）：把值类型接收者压为托管指针——
        /// 变量 → ldarga/ldloca；this → ldarga.0；嵌套字段 → 递归取址 + ldflda。
        /// 仅支持可寻址 lvalue（MVP：局部/参数/this/字段链）。
        /// </summary>
        private void EmitValueTypeReceiverAddress(IlAssembler il, BoundExpression target)
        {
            switch (target)
            {
                case BoundVariableExpression variable:
                    if (variable.Variable is ParameterSymbol parameter)
                    {
                        var argIndex = parameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                        il.Emit(IlOpCodeTable.Get("Ldarga"), (ushort)argIndex);
                    }
                    else
                    {
                        il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)_locals[variable.Variable]);
                    }

                    return;

                case BoundThisExpression:
                    // struct 实例方法：this 已是托管指针（Point&），直接加载即可（不可 ldarga，否则变 Point&*）
                    il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)0);
                    return;

                case BoundMemberAccessExpression member when member.Field != null:
                    EmitValueTypeReceiverAddress(il, member.Target);
                    il.Emit(IlOpCodeTable.Get("Ldflda"), _fieldDefs[member.Field]);
                    return;

                default:
                    throw new System.Exception($"struct 字段访问的接收者必须可寻址：{target.Kind}");
            }
        }

        private void EmitMemberAccessExpression(IlAssembler il, BoundMemberAccessExpression node)
        {
            if (node.Field != null && node.Field.IsStatic)
            {
                il.Emit(IlOpCodeTable.Get("Ldsfld"), _fieldDefs[node.Field]);
                return;
            }

            if (node.Field != null)
            {
                if (node.Field.ContainingClass!.IsValueType)
                {
                    EmitValueTypeReceiverAddress(il, node.Target);
                }
                else
                {
                    EmitExpression(il, node.Target);
                }

                il.Emit(IlOpCodeTable.Get("Ldfld"), _fieldDefs[node.Field]);
                return;
            }

            // 非字段成员访问（如数组 Length、string 属性）：接收者须先入栈
            EmitExpression(il, node.Target);

            if (node.Target.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringLength);
                return;
            }

            il.Emit(IlOpCodeTable.Get("Ldlen"));
        }

        private void EmitMemberCallExpression(IlAssembler il, BoundMemberCallExpression node)
        {
            var isStatic = node.Method != null && node.Method.IsStatic;

            // 6e-M19 M2-c：System.Object 实例方法（receiver 在栈上，值类型先装箱）→ mscorlib callvirt；
            // 用户类 override 经 CLR callvirt 天然虚分派；base.Method() 用 Call 直调基类实现（防虚分派回 override）
            if (node.Method?.BuiltinKind != null && !isStatic)
            {
                var objectCallOp = node.IsBase ? "Call" : "Callvirt";
                switch (node.Method.BuiltinKind.Value)
                {
                    case BuiltinKind.ObjectToString:
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        il.Emit(IlOpCodeTable.Get(objectCallOp), _framework.ObjectToString);
                        return;
                    case BuiltinKind.ObjectGetHashCode:
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        il.Emit(IlOpCodeTable.Get(objectCallOp), _framework.ObjectGetHashCode);
                        return;
                    case BuiltinKind.ObjectEquals:
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        EmitExpression(il, node.Arguments[0]);
                        EmitBoxIfValueType(il, node.Arguments[0].Type);
                        il.Emit(IlOpCodeTable.Get(objectCallOp), _framework.ObjectEqualsInstance);
                        return;
                    case BuiltinKind.ObjectGetType:
                        // GetType 非虚：base./this. 语义一致
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.ObjectGetType);
                        return;

                    // 6e-M19 M3-b：System.Type 只读属性（receiver 为 CLR Type 引用，无装箱）。
                    // Name = FullName.Substring(FullName.LastIndexOf('.')+1)——无点时 -1+1=0 回退全名
                    case BuiltinKind.TypeName:
                        EmitExpression(il, node.Expression);
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.TypeGetFullName);
                        il.Emit(IlOpCodeTable.Get("Dup"));
                        il.Emit(IlOpCodeTable.Get("Ldc_I4_S"), (sbyte)'.');
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringLastIndexOfChar);
                        il.Emit(IlOpCodeTable.Get("Ldc_I4_1"));
                        il.Emit(IlOpCodeTable.Get("Add"));
                        il.Emit(IlOpCodeTable.Get("Call"), _framework.StringSubstringFrom);
                        return;
                    case BuiltinKind.TypeFullName:
                        EmitExpression(il, node.Expression);
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.TypeGetFullName);
                        return;
                }
            }

            if (node.Method?.BuiltinKind != null)
            {
                // syscall 静态方法调用：复用内置函数分发（如 System.Runtime.Runtime.Print → Console.WriteLine）
                EmitBuiltinCall(il, node.Method, node.Arguments);
                return;
            }

            // facade 类成员调用：降级到 BCL（非泛型 → FindMethod；泛型实例化 → 直构 MemberRef）。
            // 实例性以“方法首参是否为 this 形参”判定（对齐 TryEmitFacadeBclCall；值类型接收者用托管指针 + Call）。
            // 必须在本方法统一的 receiver/参数发射之前处理，避免重复压栈导致栈不平衡。
            if (node.Method != null)
            {
                var cc = node.Method.ContainingClass;
                if (cc != null && IsFacadeRedirect(cc))
                {
                    var isInstance = node.Method.Parameters.Length > 0 && node.Method.Parameters[0].IsThisParameter;
                    var receiver = isInstance ? node.Expression : null;
                    var paramTypes = GetFacadeArgumentIlTypes(node.Method, isInstance, node.Arguments).Select(t => t.FullName).ToArray();
                    IlMethodRef? methodRef;

                    InstantiatedTypeSymbol? instType = cc as InstantiatedTypeSymbol
                        ?? (receiver?.Type as InstantiatedTypeSymbol);
                    if (instType != null)
                    {
                        methodRef = ResolveFacadeGenericMethodRef(instType, node.Method, node.Arguments, isInstance);
                    }
                    else
                    {
                        methodRef = _framework.FindMethod(FacadeBclFullName(cc), node.Identifier, paramTypes);
                    }

                    if (methodRef != null)
                    {
                        if (isInstance)
                        {
                            EmitFacadeInstanceReceiver(il, receiver!);
                        }

                        foreach (var a in node.Arguments)
                        {
                            EmitExpression(il, a);
                        }

                        var callOp = !isInstance || (receiver != null && IsValueTypeSymbol(receiver.Type)) ? "Call" : "Callvirt";
                        il.Emit(IlOpCodeTable.Get(callOp), methodRef);
                        return;
                    }
                    // 未找到 BCL 对应（Cocoa 独有成员）→ 回退下方 Cocoa 体发射
                }
            }

            if (!isStatic)
            {
                EmitExpression(il, node.Expression);
            }

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            // 动态链接（阶段 A3）：cod 容器类静态方法 → MemberRef 外部调用
            if (node.Method != null)
            {
                if (_codAssemblies.TryGetValue(node.Method, out var codAssembly))
                {
                    il.Emit(IlOpCodeTable.Get("Call"), CodMethodRef(node.Method, codAssembly));
                    return;
                }

                if (node.Method.ContainingClass!.IsExternal)
                {
                    var parameterNames = new string[node.Arguments.Length];
                    for (var i = 0; i < node.Arguments.Length; i++)
                    {
                        parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                    }

                    var methodRef = _framework.FindMethod(node.Method.ContainingClass.FullName, node.Identifier, parameterNames);
                    if (methodRef == null)
                    {
                        throw new System.Exception($"外部方法 {node.Method.ContainingClass.FullName}.{node.Identifier} 未找到。");
                    }

                    il.Emit(IlOpCodeTable.Get("Callvirt"), methodRef);
                    return;
                }

                // 静态方法：call；base.Method()：非虚 call；实例方法：callvirt 虚分派
                var op = isStatic || node.IsBase ? "Call" : "Callvirt";
                il.Emit(IlOpCodeTable.Get(op), _methods[node.Method]);
                return;
            }

            if (node.Expression.Type == TypeSymbol.String && node.Identifier == "substring")
            {
                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringSubstring);
                return;
            }

            throw new System.Exception($"Unexpected member call {node.Identifier}");
        }

        private void EmitMemberAssignmentExpression(IlAssembler il, BoundMemberAssignmentExpression node)
        {
            // 临时局部按字段类型分配（表达式可为 null 字面量——TypeSymbol.Null 无 IL 映射；槽语义 = 存入字段的值）
            var temporaryLocal = AllocateTemporaryLocal(node, node.Field.Type);

            if (node.Field.IsStatic)
            {
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
                il.Emit(IlOpCodeTable.Get("Stsfld"), _fieldDefs[node.Field]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                return;
            }

            if (node.Field.ContainingClass!.IsValueType)
            {
                EmitValueTypeReceiverAddress(il, node.Target);
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
                il.Emit(IlOpCodeTable.Get("Stfld"), _fieldDefs[node.Field]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                return;
            }

            EmitExpression(il, node.Target);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
            il.Emit(IlOpCodeTable.Get("Stfld"), _fieldDefs[node.Field]);
            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private void EmitObjectCreationExpression(IlAssembler il, BoundObjectCreationExpression node)
        {
            var classType = (NamedTypeSymbol)node.Type;

            // facade 类构造：重定向到 BCL .ctor（泛型直构 MemberRef）
            if (IsFacadeRedirect(classType))
            {
                var ctorRef = ResolveFacadeCtor(classType, node.Arguments);
                if (ctorRef != null)
                {
                    foreach (var argument in node.Arguments)
                    {
                        EmitExpression(il, argument);
                    }

                    il.Emit(IlOpCodeTable.Get("Newobj"), ctorRef);
                    return;
                }
            }

            if (classType.IsValueType)
            {
                // 6e-M26 值语义：临时局部 + ldloca + call .ctor + ldloc（非 newobj）
                var tempLocal = AllocateTemporaryLocal(node, classType);
                il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)tempLocal);
                foreach (var argument in node.Arguments)
                {
                    EmitExpression(il, argument);
                }

                var vtCtor = classType.GetMethod(classType.Name);
                if (vtCtor == null)
                {
                    throw new System.Exception($"struct {classType.Name} has no constructor.");
                }

                il.Emit(IlOpCodeTable.Get("Call"), _methods[vtCtor]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)tempLocal);
                return;
            }

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            if (classType.IsExternal)
            {
                var parameterNames = new string[node.Arguments.Length];
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                }

                var ctorRef = _framework.FindMethod(classType.FullName, ".ctor", parameterNames);
                if (ctorRef == null)
                {
                    throw new System.Exception($"外部类型 {classType.FullName} 的构造函数未找到。");
                }

                il.Emit(IlOpCodeTable.Get("Newobj"), ctorRef);
                return;
            }

            var ctor = classType.GetMethod(classType.Name);
            if (ctor == null)
            {
                throw new System.Exception($"Class {classType.Name} has no constructor.");
            }

            il.Emit(IlOpCodeTable.Get("Newobj"), _methods[ctor]);
        }

        private void EmitThisExpression(IlAssembler il, BoundThisExpression node)
        {
            // this 恒为 arg.0：引用类型=对象引用(O)；struct 实例方法=托管指针(Point&)（调用端按 ref 传参）
            il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)0);
        }

        private void EmitConstructorChainExpression(IlAssembler il, BoundConstructorChainExpression node)
        {
            // 6e-M19 M2-c：链到内建 System.Object（无 .ctor 符号）——0 参 no-op，CLR newobj 已隐式调 object::.ctor
            if (node.Constructor == null)
            {
                return;
            }

            // this(arg0) + args → call 基类/本类 .ctor
            il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)0);
            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            var target = node.Constructor;
            if (target.ContainingClass!.IsExternal)
            {
                var parameterNames = new string[node.Arguments.Length];
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                }

                var methodRef = _framework.FindMethod(target.ContainingClass.FullName, ".ctor", parameterNames);
                if (methodRef == null)
                {
                    throw new System.Exception($"外部构造函数 {target.ContainingClass.FullName} 未找到。");
                }

                il.Emit(IlOpCodeTable.Get("Call"), methodRef);
                return;
            }

            // 6e-M25：facade 基类 .ctor 链（class MyError extends Exception → call System.Exception::.ctor）
            if (IsFacadeRedirect(target.ContainingClass!))
            {
                var methodRef = ResolveFacadeCtor(target.ContainingClass!, node.Arguments);
                if (methodRef == null)
                {
                    throw new System.Exception($"facade 构造函数 {target.ContainingClass.FullName} 未找到。");
                }

                il.Emit(IlOpCodeTable.Get("Call"), methodRef);
                return;
            }

            il.Emit(IlOpCodeTable.Get("Call"), _methods[target]);
        }
    }
}
