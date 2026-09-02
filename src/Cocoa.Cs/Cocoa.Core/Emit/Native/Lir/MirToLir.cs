using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>
    /// MIR → LIR（<c>MirToLir</c>）：输入 = MIR（<see cref="BoundProgram.Functions"/> 规范树，
    /// 即 Lowerer「Hir→Mir」输出 —— goto/CFG 形态、无 for/while/if 等结构节点；消费契约见 CanonicalIr）。 
    /// 输出 = LIR（3 地址码，<see cref="LirProgram"/>）。逐方法对照 NativeCodeEmitter 的发射语义；
    /// 字节宽仅按类型区分；仅当 double 作 8 字节运行时的寄存器参数时按平台调整 ordinal（x86 拆 low/high 两寄存器）。
    /// 帧布局/对齐/TEB 检查收敛到 LirToAssembler。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// LIR 为 native 私有独立类型族（Lir*）；MIR 与 LIR 之间一步完成（无独立 MIR 数据结构落盘）。
    /// </summary>
    internal sealed partial class MirToLir
    {
        private readonly BoundProgram _program;
        private readonly bool _isX64;
        private readonly LirVirtualRegisterAllocator _allocator = new();
        private readonly LirProgram _irProgram;

        private readonly Dictionary<FunctionSymbol, LirFunction> _functionMap = new();
        private readonly Dictionary<VariableSymbol, LirVirtualRegister> _variables = new();
        private readonly Dictionary<BoundLabel, int> _labels = new();

        /// <summary>6e-M22 C4-c：env-first 形态的提升 lambda 集合（参数区前置 8 字节环境槽）。</summary>
        private readonly Dictionary<FunctionSymbol, LirFunction> _staticThunks = new();
        private readonly HashSet<FunctionSymbol> _environmentFirstFunctions = new();

        /// <summary>6e-M22 C5：当前函数的环境对象寄存器与布局类（无捕获 = null）。</summary>
        private LirVirtualRegister? _closureRegister;
        private NamedTypeSymbol? _closureClass;

        /// <summary>M4：存活类集合（new 可达 → 类 + 基类链），vtable 发射与可达成员入队的依据。</summary>
        private readonly HashSet<NamedTypeSymbol> _liveClasses = new();

        /// <summary>M4：已登记的虚方法根（Object 固定三虚根预种子）。</summary>
        private readonly HashSet<FunctionSymbol> _virtualRoots = new();

        /// <summary>M4：根方法 → vtable 槽索引（可达性收敛后分配）。</summary>
        private Dictionary<FunctionSymbol, int> _virtualSlots = new();

        /// <summary>M4：类实例布局缓存（字段偏移 + 实例尺寸）。</summary>
        private readonly Dictionary<NamedTypeSymbol, (Dictionary<FieldSymbol, int> Offsets, int InstanceSize)> _layoutCache = new();

        /// <summary>M4：已发射的伪 vtable（System.Type 对象）key 集合。</summary>
        private readonly HashSet<string> _pseudoVTableKeys = new();

        private LirFunction _currentFunction = null!;
        private LirVirtualRegister? _thisRegister;
        private int _nextLabelId;

        private static readonly FunctionSymbol[] ObjectBuiltinVirtualRoots =
        {
            SystemObjectMembers.ToString,
            SystemObjectMembers.GetHashCode,
            SystemObjectMembers.Equals,
        };

        private MirToLir(BoundProgram program, TargetPlatform platform)
        {
            _program = program;
            _isX64 = platform.Arch == Architecture.X64;
            _irProgram = new LirProgram(program.MainFunction!.Name);
        }

        public static LirProgram Generate(BoundProgram program, TargetPlatform platform)
        {
            var generator = new MirToLir(program, platform);
            generator.EmitProgram();
            generator.EmitVTableData();
            return generator._irProgram;
        }

        /// <summary>
        /// M4：为全部存活具体类发射 vtable 数据项（即 System.Type 对象）。
        /// 槽 0..2 固定 Object 面（override 经 FindImplementation 覆写），GetType=3，
        /// 用户虚根按槽位续接；类未继承的根槽以 ObjectGetType 填充（合法已发射函数名）。
        /// </summary>
        private void EmitVTableData()
        {
            foreach (var classType in _liveClasses.OrderBy(c => c.FullName, System.StringComparer.Ordinal))
            {
                // 数组按实际最大槽号定长（无用户虚根时仅 Object 固定四槽，不留 null 尾部）
                var maxSlot = NativeObjectModel.SlotGetType;
                foreach (var used in _virtualSlots.Values)
                {
                    if (used > maxSlot)
                    {
                        maxSlot = used;
                    }
                }

                var slots = new string[maxSlot + 1];

                slots[NativeObjectModel.SlotToString] = ResolveObjectSlot(classType, SystemObjectMembers.ToString, "ObjectToString");
                slots[NativeObjectModel.SlotGetHashCode] = ResolveObjectSlot(classType, SystemObjectMembers.GetHashCode, "ObjectGetHashCode");
                slots[NativeObjectModel.SlotEquals] = ResolveObjectSlot(classType, SystemObjectMembers.Equals, "ObjectEquals");
                slots[NativeObjectModel.SlotGetType] = "ObjectGetType";

                foreach (var kvp in _virtualSlots.Where(k => !NativeObjectModel.IsObjectBuiltinRoot(k.Key)).OrderBy(k => k.Value))
                {
                    // 类未继承该根 → 填充占位；继承 → 沿链最近实现
                    var inherited = InheritsRoot(classType, kvp.Key);
                    slots[kvp.Value] = inherited
                        ? ResolveRequiredImplementation(classType, kvp.Key)
                        : "ObjectGetType";
                }

                var key = NativeObjectModel.VTableKey(classType);
                if (!_irProgram.Data.ContainsKey(key))
                {
                    // 全部 vtable 一律自引用头（[0]=自身地址）：使 Type 值（vtable 指针）与对象
                    // 共用同一访问公式 [[x]+8] 取类型名——ObjectToString/Name/FullName 三路一致。
                    // （typeId 字段无消费方，M4 不分配；后续 is/typeid 需求再扩展头部。）
                    _irProgram.AddData(LirDataItem.VTable(key, -1, _irProgram.InternString(classType.FullName), slots));
                }
            }
        }

        /// <summary>类继承链是否包含虚根的声明类。</summary>
        private static bool InheritsRoot(NamedTypeSymbol classType, FunctionSymbol root)
        {
            var declaringClass = root.ContainingClass;
            if (declaringClass == null)
            {
                return false;
            }

            var seen = new HashSet<NamedTypeSymbol>();
            for (var current = classType; current != null && !current.IsSystemObjectRoot && seen.Add(current); current = current.BaseType)
            {
                if (current == declaringClass)
                {
                    return true;
                }
            }

            return false;
        }

        private string ResolveObjectSlot(NamedTypeSymbol classType, FunctionSymbol root, string runtimeFallback)
        {
            var implementation = NativeObjectModel.FindImplementation(classType, root);
            return implementation != null
                ? NativeObjectModel.FunctionIrName(implementation)
                : runtimeFallback;
        }

        private string ResolveRequiredImplementation(NamedTypeSymbol classType, FunctionSymbol root)
        {
            var implementation = NativeObjectModel.FindImplementation(classType, root)
                ?? throw new Exception($"vtable slot for '{root.Name}' has no implementation in concrete class '{classType.FullName}'");

            if (!_functionMap.ContainsKey(implementation))
            {
                throw new Exception($"vtable implementation '{implementation.Name}' of '{classType.FullName}' was not emitted (unreachable)");
            }

            return NativeObjectModel.FunctionIrName(implementation);
        }

        private void EmitProgram()
        {
            // 可达性过滤（6e-M17）：仅发射从入口可达的函数；M4 起存活类把构造函数与
            // 虚槽实现并入可达集（vtable 分派目标无法静态枚举，须随类存活整体发射）
            var reachable = ComputeReachableFunctions(_program.MainFunction!);
            // 6e-M26：program.Functions（ImmutableDictionary，引用哈希进程随机）枚举不稳定 →
            // 按确定性键排序，保证 native 产物函数顺序可复现。
            var functionsToEmit = _program.Functions
                .Where(kv => reachable.Contains(kv.Key))
                .OrderBy(kv => FunctionSortKey(kv.Key), StringComparer.Ordinal)
                .ToArray();

            foreach (var (function, body) in functionsToEmit)
            {
                // 入口函数保持裸名（LirToAssembler 以 Name==EntryFunctionName 标记入口标签；
                // 入口可为命名空间/类静态方法，mangle 名会破坏匹配）
                var irName = function == _program.MainFunction ? function.Name : NativeObjectModel.FunctionIrName(function);

                // 6e-M22 C4-c：提升 lambda 统一 env-first 形态（前置 8 字节环境槽）——
                // 函数值对象调用约定恒为 (env, args...)；lambda 体不读该槽
                var parameters = CreateParameters(function);
                if (function.IsStatic && function.IsLambda)
                {
                    parameters.Insert(0, new LirParameter("__env", 0));
                    for (var p = 0; p < parameters.Count; p++)
                    {
                        parameters[p] = new LirParameter(parameters[p].Name, p);
                    }

                    _environmentFirstFunctions.Add(function);
                }

                var irFunction = new LirFunction(irName, parameters);
                irFunction.ReturnSize = ReturnSize(function.ReturnType);
                _functionMap.Add(function, irFunction);
                _irProgram.Functions.Add(irFunction);
            }

            foreach (var (function, body) in functionsToEmit)
            {
                EmitFunction(_functionMap[function], function, body);
            }
        }

        /// <summary>6e-M26：函数确定性排序键（ContainingClass.FullName + 命名空间 + 方法名 + 参数签名，Ordinal）。</summary>
        private static string FunctionSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }

        // ------------------------------------------------------------------
        // 可达性与存活类（M4）
        // ------------------------------------------------------------------

        /// <summary>
        /// 从入口沿绑定调用图收集可达函数；BoundObjectCreationExpression 把类标记为存活，
        /// 存活类连带基类链、全部实例构造与虚槽实现（新根登记时对既有存活类补扫）。
        /// </summary>
        private HashSet<FunctionSymbol> ComputeReachableFunctions(FunctionSymbol entry)
        {
            foreach (var root in ObjectBuiltinVirtualRoots)
            {
                _virtualRoots.Add(root);
            }

            var reachable = new HashSet<FunctionSymbol>();
            var pendingFunctions = new Stack<FunctionSymbol>();
            var pendingClasses = new Stack<NamedTypeSymbol>();
            pendingFunctions.Push(entry);

            while (pendingFunctions.Count > 0 || pendingClasses.Count > 0)
            {
                if (pendingClasses.Count > 0)
                {
                    ProcessLiveClass(pendingClasses.Pop(), pendingFunctions, pendingClasses);
                    continue;
                }

                var function = pendingFunctions.Pop();
                if (!reachable.Add(function))
                {
                    continue;
                }

                if (_program.Functions.TryGetValue(function, out var body))
                {
                    foreach (var called in CollectCalledFunctions(body))
                    {
                        if (!reachable.Contains(called))
                        {
                            pendingFunctions.Push(called);
                        }
                    }

                    foreach (var created in CollectCreatedClasses(body))
                    {
                        if (!_liveClasses.Contains(created))
                        {
                            pendingClasses.Push(created);
                        }
                    }
                }
            }

            _virtualSlots = NativeObjectModel.AssignVirtualSlots(_liveClasses);
            return reachable;
        }

        private void ProcessLiveClass(NamedTypeSymbol classType, Stack<FunctionSymbol> pendingFunctions, Stack<NamedTypeSymbol> pendingClasses)
        {
            if (!_liveClasses.Add(classType))
            {
                return;
            }

            // 基类链全部存活
            var seenBases = new HashSet<NamedTypeSymbol>();
            for (var baseType = classType.BaseType; baseType != null && !baseType.IsSystemObjectRoot && seenBases.Add(baseType); baseType = baseType.BaseType)
            {
                if (!_liveClasses.Contains(baseType))
                {
                    pendingClasses.Push(baseType);
                }
            }

            // 实例构造函数（new 绑定的 ctor 由发射端查找；保守入队全部实例构造）
            foreach (var method in classType.Methods)
            {
                if (method.IsConstructor && !method.IsStatic)
                {
                    pendingFunctions.Push(method);
                }
            }

            // 本类链上新登记的虚根：对全部存活类补扫实现（override 所在派生类可能先于声明类处理）
            foreach (var method in EnumerateDeclaredVirtualMethods(classType))
            {
                var root = NativeObjectModel.VirtualRoot(method);
                if (_virtualRoots.Add(root))
                {
                    EnqueueImplementations(root, pendingFunctions);
                }
            }

            // 已知全部虚根在本类的生效实现
            foreach (var root in _virtualRoots)
            {
                EnqueueImplementation(classType, root, pendingFunctions);
            }
        }

        private void EnqueueImplementations(FunctionSymbol root, Stack<FunctionSymbol> pendingFunctions)
        {
            foreach (var liveClass in _liveClasses)
            {
                EnqueueImplementation(liveClass, root, pendingFunctions);
            }
        }

        private void EnqueueImplementation(NamedTypeSymbol classType, FunctionSymbol root, Stack<FunctionSymbol> pendingFunctions)
        {
            if (classType.IsAbstract || classType.IsInterface)
            {
                return;
            }

            var implementation = NativeObjectModel.FindImplementation(classType, root);
            if (implementation != null)
            {
                pendingFunctions.Push(implementation);
            }
        }

        private static IEnumerable<NamedTypeSymbol> CollectCreatedClasses(BoundNode node)
        {
            if (node.Kind == BoundNodeKind.ObjectCreationExpression && ((BoundObjectCreationExpression)node).Type is NamedTypeSymbol created)
            {
                yield return created;
            }

            // 6e-M19 M5-b：is/as 目标类标记存活（vtable 链比对依赖目标及祖先已发射）；抽象/接口无 vtable 不入
            if (node.Kind == BoundNodeKind.IsExpression && ((BoundIsExpression)node).TargetType is NamedTypeSymbol isTarget &&
                !isTarget.IsAbstract && !isTarget.IsInterface)
            {
                yield return isTarget;
            }

            if (node.Kind == BoundNodeKind.AsExpression && ((BoundAsExpression)node).TargetType is NamedTypeSymbol asTarget &&
                !asTarget.IsAbstract && !asTarget.IsInterface)
            {
                yield return asTarget;
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                foreach (var nested in CollectCreatedClasses(child))
                {
                    yield return nested;
                }
            }
        }

        private IEnumerable<FunctionSymbol> EnumerateDeclaredVirtualMethods(NamedTypeSymbol classType)
        {
            var seenTypes = new HashSet<NamedTypeSymbol>();
            for (var current = classType; current != null && !current.IsSystemObjectRoot && seenTypes.Add(current); current = current.BaseType)
            {
                foreach (var method in current.Methods)
                {
                    if (!method.IsConstructor && !method.IsStatic && (method.IsVirtual || method.IsOverride))
                    {
                        yield return method;
                    }
                }
            }
        }

        private static IEnumerable<FunctionSymbol> CollectCalledFunctions(BoundNode node)
        {
            switch (node)
            {
                case BoundCallExpression call:
                    yield return call.Function;
                    break;
                case BoundMemberCallExpression memberCall:
                    if (memberCall.Method != null)
                    {
                        yield return memberCall.Method;
                    }
                    break;
                case BoundFunctionValueExpression functionValue:
                    // 6e-M22 C4-c：函数值目标（lambda/方法组）必须随发射集存活
                    yield return functionValue.Function;
                    break;
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                foreach (var called in CollectCalledFunctions(child))
                {
                    yield return called;
                }
            }
        }

        private static List<LirParameter> CreateParameters(FunctionSymbol function)
        {
            var parameters = new List<LirParameter>();
            foreach (var parameter in function.Parameters)
            {
                parameters.Add(new LirParameter(parameter.Name, parameter.Ordinal));
            }

            return parameters;
        }

        private static int ReturnSize(TypeSymbol type)
        {
            if (type == TypeSymbol.Void)
            {
                return 0;
            }

            return Is8ByteType(type) ? 8 : 4;
        }

        private static bool Is8ByteType(TypeSymbol type) => type == TypeSymbol.String || type == TypeSymbol.Any ||
            type == TypeSymbol.Double || type == TypeSymbol.Int64 || type == TypeSymbol.UInt64 ||
            type.ElementType != null || (type is NamedTypeSymbol { IsValueType: false }) ||
            (type is NamedTypeSymbol { TypeKind: TypeKind.Struct } && !type.IsPrimitiveValueType) || type is FunctionTypeSymbol;

        /// <summary>类型符号 → LirType（Phase 2 LirType 落位）：宽度与运算语义由类型驱动。</summary>
        private static LirType TypeOf(TypeSymbol type)
        {
            if (type == TypeSymbol.Double)
            {
                return LirType.F64;
            }

            if (type == TypeSymbol.Float)
            {
                return LirType.F32;
            }

            if (type == TypeSymbol.Int64 || type == TypeSymbol.UInt64)
            {
                return LirType.I64;
            }

            // 引用/数组/字符串/函数值/任意 → 指针（逻辑宽 8 字节）
            if (type == TypeSymbol.String || type == TypeSymbol.Any ||
                type.ElementType != null || (type is NamedTypeSymbol { IsValueType: false }) ||
                (type is NamedTypeSymbol { TypeKind: TypeKind.Struct } && !type.IsPrimitiveValueType) || type is FunctionTypeSymbol)
            {
                return LirType.Addr;
            }

            return LirType.I32;
        }
        /// <summary>M4：实例方法/实例构造含隐藏 this 首参（静态成员与顶层函数无）。</summary>
        private static bool HasThisParameter(FunctionSymbol function)
            => function.ContainingClass != null && !function.IsStatic;

        /// <summary>this 参数在参数区占用的字节数（两架构均 8 字节槽；x86 双 dword）。</summary>
        private int ThisParamBytes(FunctionSymbol function) => HasThisParameter(function) || _environmentFirstFunctions.Contains(function) ? 8 : 0;

        /// <summary>byref 形参的参数区槽宽 = 指针宽（6e-M23 R7）；其余按值类型宽度。</summary>
        private int ParamSlotSize(ParameterSymbol parameter) => parameter.IsByRef ? (_isX64 ? 8 : 4) : ReturnSize(parameter.Type);

        /// <summary>x86 用户函数实参/形参的字节偏移：double 占 8 字节，byref 占指针宽，其余 4 字节（x64 统一每参 8 字节槽）。实例方法前置 8 字节 this 槽。</summary>
        private int ParamByteOffset(FunctionSymbol function, int index, int count)
        {
            var offset = ThisParamBytes(function);
            if (_isX64)
            {
                return offset + 8 * index;
            }

            for (var i = 0; i < index && i < count; i++)
            {
                offset += ParamSlotSize(function.Parameters[i]);
            }

            return offset;
        }

        private int ParamsTotalBytes(FunctionSymbol function, int count)
        {
            var total = ThisParamBytes(function);
            if (_isX64)
            {
                return total + 8 * count;
            }

            for (var i = 0; i < count; i++)
            {
                total += ParamSlotSize(function.Parameters[i]);
            }

            return total;
        }

        // ------------------------------------------------------------------
        // 函数
        // ------------------------------------------------------------------

        private void EmitFunction(LirFunction irFunction, FunctionSymbol function, BoundBlockStatement body)
        {
            _currentFunction = irFunction;
            _variables.Clear();
            _labels.Clear();
            _nextLabelId = 0;
            _thisRegister = null;

            irFunction.EndLabelId = AllocLabel();
            Add(irFunction.Instructions, new LirInstruction(LirOpCode.StackCheck));

            // 6e-M22 C5：闭包环境接线
            _closureRegister = null;
            _closureClass = function.EnvironmentClass;

            if (_closureClass != null && function.IsLambda)
            {
                // lambda：隐藏 __env 首参（LirParameter 已在创建时前置）即环境对象
                _closureRegister = AllocateRegister(LirType.Addr);
                Add(irFunction.Instructions, new LirInstruction(LirOpCode.InitParam, _closureRegister, LirOperand.Constant(0)));
            }

            if (HasThisParameter(function))
            {
                // M4：隐藏 this = 参数区偏移 0（BoundThisExpression/BaseExpression 映射该寄存器）
                _thisRegister = AllocateRegister(LirType.Addr);
                Add(irFunction.Instructions, new LirInstruction(LirOpCode.InitParam, _thisRegister, LirOperand.Constant(0)));
            }

            foreach (var parameter in function.Parameters)
            {
                // 6e-M23 R7：byref 形参寄存器持指针（槽宽 = 指针宽），点类型尺寸仅用于解引用读写
                var register = AllocateRegister(parameter, parameter.IsByRef ? LirType.Addr : TypeOf(parameter.Type));
                if (function.Name == _irProgram.EntryFunctionName)
                {
                    // 入口函数参数（main(args: string[])）由运行时从命令行构造，无需 ABI 传参。
                    Add(irFunction.Instructions, new LirInstruction(LirOpCode.Call, register, LirOperand.Runtime("BuildArgs")));
                }
                else
                {
                    Add(irFunction.Instructions, new LirInstruction(LirOpCode.InitParam, register, LirOperand.Constant(ParamByteOffset(function, parameter.Ordinal, function.Parameters.Length))));
                }

                if (parameter.IsOut)
                {
                    // 明确赋值防御兜底（设计 §5.3）：out 形参入口写穿透默认值，杜绝未赋值读到帧垃圾
                    var valueSize = ReturnSize(parameter.Type);
                    var zero = AllocateRegister(TypeOf(parameter.Type));
                    Add(irFunction.Instructions, new LirInstruction(LirOpCode.Const, zero, LirOperand.Constant(0)));
                    Add(irFunction.Instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(register), LirOperand.Reg(zero), 0, valueSize));
                }
            }

            // 宿主函数：入口处创建环境对象（清零 + 捕获参数播种）
            if (_closureClass != null && !function.IsLambda)
            {
                var (envOffsets, envSize) = NativeObjectModel.BuildLayout(_closureClass);
                var pointerSize = _isX64 ? 8 : 4;

                var sizeRegister = EmitConst(envSize + pointerSize);
                var envObject = AllocateRegister(LirType.Addr);
                Add(irFunction.Instructions, new LirInstruction(LirOpCode.SetArg, LirOperand.Constant(0), LirOperand.Reg(sizeRegister)));
                Add(irFunction.Instructions, new LirInstruction(LirOpCode.Call, envObject, LirOperand.Runtime("Alloc"), LirOperand.Constant(0)));

                // [0] typeId 占位 0
                var zero = AllocateRegister(4);
                Add(irFunction.Instructions, new LirInstruction(LirOpCode.Const, zero, LirOperand.Constant(0)));
                Add(irFunction.Instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(envObject), LirOperand.Reg(zero), 0, 4));

                // 字段清零
                foreach (var field in NativeObjectModel.CollectInstanceFields(_closureClass))
                {
                    var fieldSize = NativeObjectModel.FieldSize(field.Type);
                    var zeroField = AllocateRegister(fieldSize == 8 ? 8 : 4);
                    Add(irFunction.Instructions, new LirInstruction(LirOpCode.Const, zeroField, LirOperand.Constant(0)));
                    Add(irFunction.Instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(envObject), LirOperand.Reg(zeroField), envOffsets[field], fieldSize));
                }

                // 捕获参数播种：入参值写入环境字段
                if (function.CapturedVariables != null)
                {
                    foreach (var captured in function.CapturedVariables)
                    {
                        if (captured is ParameterSymbol parameter)
                        {
                            var field = _closureClass.GetField(captured.Name)!;
                            var value = GetVariable(parameter);
                            Add(irFunction.Instructions, new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(envObject), LirOperand.Reg(value), envOffsets[field], NativeObjectModel.FieldSize(captured.Type)));
                        }
                    }
                }

                _closureRegister = envObject;
            }

            EmitStatement(body);

            Add(irFunction.Instructions, new LirInstruction(LirOpCode.Ret, LirOperand.Label(irFunction.EndLabelId)));
        }

        private LirVirtualRegister AllocateRegister(VariableSymbol? symbol, int size)
        {
            var register = _allocator.Allocate(size == 8 ? LirType.I64 : LirType.I32);
            _currentFunction.Register(register);
            if (symbol != null)
            {
                _variables.Add(symbol, register);
            }

            return register;
        }

        private LirVirtualRegister AllocateRegister(int size)
        {
            var register = _allocator.Allocate(size == 8 ? LirType.I64 : LirType.I32);
            _currentFunction.Register(register);
            return register;
        }

        private LirVirtualRegister AllocateRegister(LirType type)
        {
            var register = _allocator.Allocate(type);
            _currentFunction.Register(register);
            return register;
        }

        private LirVirtualRegister AllocateRegister(VariableSymbol? symbol, LirType type)
        {
            var register = _allocator.Allocate(type);
            _currentFunction.Register(register);
            if (symbol != null)
            {
                _variables.Add(symbol, register);
            }

            return register;
        }

        private void Add(List<LirInstruction> instructions, LirInstruction instruction)
        {
            instructions.Add(instruction);
        }

        // ------------------------------------------------------------------
        // 语句
        // ------------------------------------------------------------------

    }
}
