using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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
        private readonly Dictionary<FunctionSymbol, IlMethodDef> _methods = new Dictionary<FunctionSymbol, IlMethodDef>();
        private readonly Dictionary<VariableSymbol, int> _locals = new Dictionary<VariableSymbol, int>();
        private readonly Dictionary<BoundExpression, int> _temporaryLocalIndices = new Dictionary<BoundExpression, int>();
        private List<IlType>? _currentFunctionLocals;
        private readonly Dictionary<BoundLabel, IlInstruction> _labelTargets = new Dictionary<BoundLabel, IlInstruction>();

        private FunctionSymbol? _entryFunction;
        private bool _entryVoidMain;

        private readonly IlTypeDef _typeDefinition;
        private readonly Dictionary<ClassTypeSymbol, IlTypeDef> _classTypeDefs = new Dictionary<ClassTypeSymbol, IlTypeDef>();
        private readonly Dictionary<FieldSymbol, IlFieldDef> _fieldDefs = new Dictionary<FieldSymbol, IlFieldDef>();
    private readonly DelegateShapeCache _delegateShapes;
        private HashSet<(string Namespace, string Name)>? _overloadedGroups;
        private bool _currentMethodIsInstance;

        /// <summary>6e-M22 C5-c：当前方法的环境对象局部槽索引与布局类（无捕获 = null）。</summary>
        private int? _closureEnvLocalIndex;
        private ClassTypeSymbol? _closureClass;
        private Dictionary<string, IlFieldDef>? _closureFieldDefs;
        private readonly Dictionary<ClassTypeSymbol, IlMethodDef> environmentCtorDefs = new();

        /// <summary>闭包环境类判定：Binder 合成的 `__Env_<fn>` 命名约定。</summary>
        private static bool IsClosureEnvironmentClass(ClassTypeSymbol classType)
            => classType.Name.StartsWith("__Env_", StringComparison.Ordinal);

        private IlEmitter(string moduleName, string[] references)
        {
            _moduleName = moduleName;
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
            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            var emitter = new IlEmitter(moduleName, references);

            return emitter.Emit(program, outputPath, target, emitLibrary);
        }

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath)
            => Emit(program, outputPath, IlTarget.Default);

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target)
            => Emit(program, outputPath, target, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target, bool emitLibrary)
        {
            _entryFunction = emitLibrary ? null : program.MainFunction;

            // 1. 收集 class（基类在前）→ 建 IlTypeDef + 字段
            // 6e-M18：补入函数引用的注入容器类（System.Core.cod 的 Console/Math 等，不在 program.Classes 的源码声明集内）
            var classes = program.Classes.ToList();
            foreach (var f in program.Functions.Keys)
            {
                if (f.ContainingClass != null && !classes.Contains(f.ContainingClass))
                {
                    classes.Add(f.ContainingClass);
                }
            }

            var emitted = new HashSet<ClassTypeSymbol>();

            // 1a：先注册全部 TypeDef 壳（6e-M20：泛型实例化类的字段可前向引用兄弟实例化类，
            // 依赖序无法仅按基类链排序——壳先行入表，Extends/字段随后填充）
            foreach (var classType in classes)
            {
                var typeDef = new IlTypeDef(classType.Name, classType.Namespace, null, isPublic: classType.Visibility == Visibility.Public, baseTypeDef: null)
                {
                    IsAbstract = classType.IsAbstract,
                    IsSealed = classType.IsSealed,
                    IsInterface = classType.IsInterface,
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
            var environmentCtorBodies = new List<(ClassTypeSymbol ClassType, IlMethodDef Ctor)>();
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
            foreach (var f in program.Functions.Keys)
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

            foreach (var functionWithBody in program.Functions)
            {
                if (functionWithBody.Key.BuiltinKind != null)
                {
                    // syscall 内部原语：无方法体、调用点按 BuiltinKind 分发，不声明为 IL 方法
                    continue;
                }

                EmitFunctionDeclaration(functionWithBody.Key);
            }

            // 2.5 属性定义（getter/setter 方法已发射）
            foreach (var classType in program.Classes)
            {
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

            foreach (var functionWithBody in program.Functions)
            {
                if (functionWithBody.Key.IsExtern || functionWithBody.Key.IsAbstract || functionWithBody.Key.BuiltinKind != null)
                {
                    continue;
                }

                var method = _methods[functionWithBody.Key];
                methods.Add(method);
                _entryVoidMain = _entryFunction == functionWithBody.Key && functionWithBody.Key.ReturnType == TypeSymbol.Void;
                var (code, localSigToken, maxStack) = EmitFunctionBody(method, functionWithBody.Key, functionWithBody.Value);
                bodies.Add(new ManagedPEWriter.MethodBodyBlob(code, localSigToken, (ushort)maxStack));
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
            var parameterTypes = new List<IlType>();
            foreach (var parameter in function.Parameters)
            {
                parameterTypes.Add(ToIlType(parameter.Type));
            }

            var callingConvention = function.CallingConvention switch
            {
                CallingConvention.Cdecl => IlCallingConvention.Cdecl,
                CallingConvention.StdCall => IlCallingConvention.StdCall,
                _ => IlCallingConvention.Winapi,
            };

            var isInstance = function.ContainingClass != null && !function.IsStatic;
            // 顶层函数：命名空间限定名（EmitName）；重载组内追加参数类型后缀保证同一 TypeDef 内元数据方法名唯一
            var name = function.IsConstructor ? (function.IsStatic ? ".cctor" : ".ctor") : function.EmitName;
            if (function.ContainingClass == null && !function.IsConstructor &&
                _overloadedGroups!.Contains((function.Namespace, function.Name)))
            {
                name += "$" + string.Join("$", function.Parameters.Select(p => EncodeTypeNameForMethodName(p.Type)));
            }

            var implementsInterfaceMember = isInstance &&
                function.ContainingClass!.GetAllInterfaces().Any(i =>
                    i.GetDeclaredMethod(function.Name) != null ||
                    i.Properties.Any(p => p.Getter?.Name == function.Name || p.Setter?.Name == function.Name));

            var method = new IlMethodDef(name, returnType, parameterTypes, null, function.IsExtern ? function.DllName : null, function.EntryPoint, callingConvention, isStatic: !isInstance, charSet: function.CharSet ?? CharSet.Unicode)
            {
                Visibility = function.Visibility,
                IsVirtual = function.IsVirtual || function.IsOverride || implementsInterfaceMember,
                IsAbstract = function.IsAbstract,
                IsSealed = function.IsSealed,
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

        private void EmitClassDeclaration(ClassTypeSymbol classType, IlTypeRef multicastDelegateRef)
        {
            var typeDef = _classTypeDefs[classType];
            var hasUserBase = classType.BaseType != null && !classType.BaseType.IsSystemObjectRoot;
            IlTypeDef? baseTypeDef = null;
            IlTypeRef? baseTypeRef = null;

            if (hasUserBase)
            {
                if (classType.BaseType == ClassTypeSymbol.SystemMulticastDelegate)
                {
                    // delegate 子类 extends System.MulticastDelegate → 框架 TypeRef
                    baseTypeRef = multicastDelegateRef;
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

        private (byte[] Code, uint LocalSigToken, int MaxStack) EmitFunctionBody(IlMethodDef method, FunctionSymbol function, BoundBlockStatement body)
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

            return (code, localSigToken, maxStack);
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

            if (type is EnumTypeSymbol)
            {
                return IlType.Int32;
            }

            // 6e-M22 D-B：delegate 类 → Func`N 等价类型（运行期表示与函数值一致）
            if (type is ClassTypeSymbol { IsDelegateClass: true } delegateClassType)
            {
                var sig = delegateClassType.GetDelegateSignature();
                if (sig != null)
                {
                    return _delegateShapes.Resolve(sig, ToIlType).Type;
                }
            }

            if (type is ClassTypeSymbol classType)
            {
                if (classType.IsExternal)
                {
                    return IlType.Class(ResolveExternalTypeRef(classType));
                }

                // 6e-M19 M2-a：内建 Object/Type 单例不产生 TypeDef，映射框架 TypeRef
                if (classType.IsSystemObjectRoot)
                {
                    return IlType.Class(_framework.ObjectType);
                }

                if (classType == ClassTypeSymbol.SystemType)
                {
                    return IlType.Class(_framework.RequireType("System.Type"));
                }

                return IlType.Class(_classTypeDefs[classType]);
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

        /// <summary>类型名编码进方法名后缀（`int[]` 的 `[]` 非法，转下划线）。</summary>
        private static string EncodeTypeNameForMethodName(TypeSymbol type)
        {
            return type.Name.Replace("[", "_").Replace("]", "_");
        }

        private IlTypeRef ResolveExternalTypeRef(ClassTypeSymbol classType)
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
                ClassTypeSymbol { IsDelegateClass: true } dc => dc.GetDelegateSignature()!,
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
                    EmitThisExpression(il, new BoundThisExpression(node.Syntax, (ClassTypeSymbol)node.Type));
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

            var fromIsNumericLike = from.IsNumeric || from == TypeSymbol.Char || from is EnumTypeSymbol;
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
                            from is EnumTypeSymbol)
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
            else if (node.Type is EnumTypeSymbol)
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
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_locals[node.Variable]);
            }
        }

        private void EmitAssignmentExpression(IlAssembler il, BoundAssignmentExpression node)
        {
            EmitExpression(il, node.Expression);

            // 6e-M22 C5-c：捕获变量写环境字段（值同时在栈顶作为表达式结果）
            if (node.Variable.IsCaptured && _closureEnvLocalIndex.HasValue)
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                il.Emit(IlOpCodeTable.Get("Stfld"), _closureFieldDefs![node.Variable.Name]);
                return;
            }

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

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            var methodDefinition = _methods[node.Function];
            il.Emit(IlOpCodeTable.Get("Call"), methodDefinition);
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

            if (node.Expression.Type is EnumTypeSymbol && node.Type == TypeSymbol.Int64 ||
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
                node.Expression.Type is EnumTypeSymbol && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type is EnumTypeSymbol)
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            if (node.Expression.Type is ClassTypeSymbol fromClass && node.Type is ClassTypeSymbol toClass)
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
                type != TypeSymbol.UInt8 && type != TypeSymbol.Double && type is not EnumTypeSymbol &&
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
                _ when elementType is EnumTypeSymbol => "System.Int32",
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

            if (elementType is EnumTypeSymbol)
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

        private void EmitMemberAccessExpression(IlAssembler il, BoundMemberAccessExpression node)
        {
            if (node.Field != null && node.Field.IsStatic)
            {
                il.Emit(IlOpCodeTable.Get("Ldsfld"), _fieldDefs[node.Field]);
                return;
            }

            EmitExpression(il, node.Target);

            if (node.Field != null)
            {
                il.Emit(IlOpCodeTable.Get("Ldfld"), _fieldDefs[node.Field]);
                return;
            }

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

            if (!isStatic)
            {
                EmitExpression(il, node.Expression);
            }

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            if (node.Method != null)
            {
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

            EmitExpression(il, node.Target);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
            il.Emit(IlOpCodeTable.Get("Stfld"), _fieldDefs[node.Field]);
            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private void EmitObjectCreationExpression(IlAssembler il, BoundObjectCreationExpression node)
        {
            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            var classType = (ClassTypeSymbol)node.Type;

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

            il.Emit(IlOpCodeTable.Get("Call"), _methods[target]);
        }
    }
}
