using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>
    /// 绑定树（Lowerer 输出）→ IR。逐方法对照 NativeCodeEmitter 的发射语义；
    /// 字节宽仅按类型区分；仅当 double 作 8 字节运行时的寄存器参数时按平台调整 ordinal（x86 拆 low/high 两寄存器）。
    /// 帧布局/对齐/TEB 检查收敛到 IrToAssembler。
    /// 表达式求值顺序与现有实现完全一致（二元右操作数后求值、调用参数右→左求值、混合副作用保持）。
    /// </summary>
    internal sealed class BoundTreeToIr
    {
        private readonly BoundProgram _program;
        private readonly bool _isX64;
        private readonly IrVirtualRegisterAllocator _allocator = new();
        private readonly IrProgram _irProgram;

        private readonly Dictionary<FunctionSymbol, IrFunction> _functionMap = new();
        private readonly Dictionary<VariableSymbol, IrVirtualRegister> _variables = new();
        private readonly Dictionary<BoundLabel, int> _labels = new();

        /// <summary>6e-M22 C4-c：env-first 形态的提升 lambda 集合（参数区前置 8 字节环境槽）。</summary>
        private readonly Dictionary<FunctionSymbol, IrFunction> _staticThunks = new();
        private readonly HashSet<FunctionSymbol> _environmentFirstFunctions = new();

        /// <summary>6e-M22 C5：当前函数的环境对象寄存器与布局类（无捕获 = null）。</summary>
        private IrVirtualRegister? _closureRegister;
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

        private IrFunction _currentFunction = null!;
        private IrVirtualRegister? _thisRegister;
        private int _nextLabelId;

        private static readonly FunctionSymbol[] ObjectBuiltinVirtualRoots =
        {
            SystemObjectMembers.ToString,
            SystemObjectMembers.GetHashCode,
            SystemObjectMembers.Equals,
        };

        private BoundTreeToIr(BoundProgram program, TargetPlatform platform)
        {
            _program = program;
            _isX64 = platform.Arch == Architecture.X64;
            _irProgram = new IrProgram(program.MainFunction!.Name);
        }

        public static IrProgram Generate(BoundProgram program, TargetPlatform platform)
        {
            var generator = new BoundTreeToIr(program, platform);
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
                    _irProgram.AddData(IrDataItem.VTable(key, -1, _irProgram.InternString(classType.FullName), slots));
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
                // 入口函数保持裸名（IrToAssembler 以 Name==EntryFunctionName 标记入口标签；
                // 入口可为命名空间/类静态方法，mangle 名会破坏匹配）
                var irName = function == _program.MainFunction ? function.Name : NativeObjectModel.FunctionIrName(function);

                // 6e-M22 C4-c：提升 lambda 统一 env-first 形态（前置 8 字节环境槽）——
                // 函数值对象调用约定恒为 (env, args...)；lambda 体不读该槽
                var parameters = CreateParameters(function);
                if (function.IsStatic && function.Syntax is LambdaExpressionSyntax)
                {
                    parameters.Insert(0, new IrParameter("__env", 0));
                    for (var p = 0; p < parameters.Count; p++)
                    {
                        parameters[p] = new IrParameter(parameters[p].Name, p);
                    }

                    _environmentFirstFunctions.Add(function);
                }

                var irFunction = new IrFunction(irName, parameters);
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

        private static List<IrParameter> CreateParameters(FunctionSymbol function)
        {
            var parameters = new List<IrParameter>();
            foreach (var parameter in function.Parameters)
            {
                parameters.Add(new IrParameter(parameter.Name, parameter.Ordinal));
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

        private void EmitFunction(IrFunction irFunction, FunctionSymbol function, BoundBlockStatement body)
        {
            _currentFunction = irFunction;
            _variables.Clear();
            _labels.Clear();
            _nextLabelId = 0;
            _thisRegister = null;

            irFunction.EndLabelId = AllocLabel();
            Add(irFunction.Instructions, new IrInstruction(IrOpCode.StackCheck));

            // 6e-M22 C5：闭包环境接线
            _closureRegister = null;
            _closureClass = function.EnvironmentClass;

            if (_closureClass != null && function.Syntax is LambdaExpressionSyntax)
            {
                // lambda：隐藏 __env 首参（IrParameter 已在创建时前置）即环境对象
                _closureRegister = AllocateRegister(8);
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.InitParam, _closureRegister, IrOperand.Constant(0)));
            }

            if (HasThisParameter(function))
            {
                // M4：隐藏 this = 参数区偏移 0（BoundThisExpression/BaseExpression 映射该寄存器）
                _thisRegister = AllocateRegister(8);
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.InitParam, _thisRegister, IrOperand.Constant(0)));
            }

            foreach (var parameter in function.Parameters)
            {
                // 6e-M23 R7：byref 形参寄存器持指针（槽宽 = 指针宽），点类型尺寸仅用于解引用读写
                var register = AllocateRegister(parameter, ParamSlotSize(parameter));
                if (function.Name == _irProgram.EntryFunctionName)
                {
                    // 入口函数参数（main(args: string[])）由运行时从命令行构造，无需 ABI 传参。
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.Call, register, IrOperand.Runtime("BuildArgs")));
                }
                else
                {
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.InitParam, register, IrOperand.Constant(ParamByteOffset(function, parameter.Ordinal, function.Parameters.Length))));
                }

                if (parameter.IsOut)
                {
                    // 明确赋值防御兜底（设计 §5.3）：out 形参入口写穿透默认值，杜绝未赋值读到帧垃圾
                    var valueSize = ReturnSize(parameter.Type);
                    var zero = AllocateRegister(valueSize);
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(register), IrOperand.Reg(zero), 0, valueSize));
                }
            }

            // 宿主函数：入口处创建环境对象（清零 + 捕获参数播种）
            if (_closureClass != null && function.Syntax is not LambdaExpressionSyntax)
            {
                var (envOffsets, envSize) = NativeObjectModel.BuildLayout(_closureClass);
                var pointerSize = _isX64 ? 8 : 4;

                var sizeRegister = EmitConst(envSize + pointerSize);
                var envObject = AllocateRegister(8);
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(sizeRegister)));
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.Call, envObject, IrOperand.Runtime("Alloc"), IrOperand.Constant(0)));

                // [0] typeId 占位 0
                var zero = AllocateRegister(4);
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
                Add(irFunction.Instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(envObject), IrOperand.Reg(zero), 0, 4));

                // 字段清零
                foreach (var field in NativeObjectModel.CollectInstanceFields(_closureClass))
                {
                    var fieldSize = NativeObjectModel.FieldSize(field.Type);
                    var zeroField = AllocateRegister(fieldSize == 8 ? 8 : 4);
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.Const, zeroField, IrOperand.Constant(0)));
                    Add(irFunction.Instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(envObject), IrOperand.Reg(zeroField), envOffsets[field], fieldSize));
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
                            Add(irFunction.Instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(envObject), IrOperand.Reg(value), envOffsets[field], NativeObjectModel.FieldSize(captured.Type)));
                        }
                    }
                }

                _closureRegister = envObject;
            }

            EmitStatement(body);

            Add(irFunction.Instructions, new IrInstruction(IrOpCode.Ret, IrOperand.Label(irFunction.EndLabelId)));
        }

        private IrVirtualRegister AllocateRegister(VariableSymbol? symbol, int size)
        {
            var register = _allocator.Allocate();
            _currentFunction.RegisterSizes.Add(register, size);
            if (symbol != null)
            {
                _variables.Add(symbol, register);
            }

            return register;
        }

        private IrVirtualRegister AllocateRegister(int size)
        {
            var register = _allocator.Allocate();
            _currentFunction.RegisterSizes.Add(register, size);
            return register;
        }

        private void Add(List<IrInstruction> instructions, IrInstruction instruction)
        {
            instructions.Add(instruction);
        }

        // ------------------------------------------------------------------
        // 语句
        // ------------------------------------------------------------------

        private void EmitStatement(BoundStatement node)
        {
            var instructions = _currentFunction.Instructions;

            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    foreach (var statement in ((BoundBlockStatement)node).Statements)
                    {
                        EmitStatement(statement);
                    }
                    break;

                case BoundNodeKind.NopStatement:
                    break;

                case BoundNodeKind.SequencePointStatement:
                    EmitStatement(((BoundSequencePointStatement)node).Statement);
                    break;

                case BoundNodeKind.VariableDeclaration:
                    {
                        var declaration = (BoundVariableDeclaration)node;
                        var value = EmitExpression(declaration.Initializer);

                        // 6e-M22 C5：捕获变量声明 → 初始化值写入环境对象字段
                        if (declaration.Variable.IsCaptured && _closureRegister != null)
                        {
                            var field = _closureClass!.GetField(declaration.Variable.Name)!;
                            var offset = NativeObjectModel.BuildLayout(_closureClass).Offsets[field];
                            var size = NativeObjectModel.FieldSize(field.Type);
                            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(_closureRegister), IrOperand.Reg(value), offset, size));
                            break;
                        }

                        var variable = GetVariable(declaration.Variable);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, variable, IrOperand.Reg(value)));
                        break;
                    }

                case BoundNodeKind.IfStatement:
                    {
                        var statement = (BoundIfStatement)node;
                        var elseLabel = AllocLabel();
                        var doneLabel = AllocLabel();
                        var condition = EmitExpression(statement.Condition);

                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(elseLabel)));

                        EmitStatement(statement.ThenStatement);
                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(doneLabel)));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(elseLabel)));
                        if (statement.ElseStatement != null)
                        {
                            EmitStatement(statement.ElseStatement);
                        }

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.WhileStatement:
                    {
                        var statement = (BoundWhileStatement)node;
                        var loopLabel = AllocLabel();
                        var doneLabel = AllocLabel();

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loopLabel)));
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(doneLabel)));

                        EmitStatement(statement.Body);
                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(loopLabel)));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.DoWhileStatement:
                    {
                        var statement = (BoundDoWhileStatement)node;
                        var loopLabel = AllocLabel();

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loopLabel)));
                        EmitStatement(statement.Body);
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.NotEqual), IrOperand.Label(loopLabel)));
                        break;
                    }

                case BoundNodeKind.ForStatement:
                    {
                        var statement = (BoundForStatement)node;
                        var loopLabel = AllocLabel();
                        var doneLabel = AllocLabel();

                        var variable = GetVariable(statement.Variable);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, variable, IrOperand.Reg(EmitExpression(statement.LowerBound))));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loopLabel)));
                        var upper = EmitExpression(statement.UpperBound);
                        var less = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(variable), IrOperand.Reg(upper)));
                        Add(instructions, new IrInstruction(IrOpCode.Setcc, less, IrOperand.Constant((int)IrCond.Less)));
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(less), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(doneLabel)));

                        EmitStatement(statement.Body);

                        // 递增：i = i + step（无 step 时 + 1）
                        var stepExpression = statement.Step ?? new BoundLiteralExpression(statement.Syntax, 1);
                        var increment = new BoundBinaryExpression(
                            statement.Syntax,
                            new BoundVariableExpression(statement.Syntax, statement.Variable),
                            BoundBinaryOperator.Bind(SyntaxKind.PlusToken, TypeSymbol.Int32, TypeSymbol.Int32)!,
                            stepExpression);
                        EmitExpression(new BoundAssignmentExpression(statement.Syntax, statement.Variable, increment));

                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(loopLabel)));

                        Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
                        break;
                    }

                case BoundNodeKind.LabelStatement:
                    Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(GetLabel(((BoundLabelStatement)node).Label))));
                    break;

                case BoundNodeKind.GotoStatement:
                    Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(GetLabel(((BoundGotoStatement)node).Label))));
                    break;

                case BoundNodeKind.ConditionalGotoStatement:
                    {
                        var statement = (BoundConditionalGotoStatement)node;
                        var condition = EmitExpression(statement.Condition);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Jcc,
                            IrOperand.Constant((int)(statement.JumpIfTrue ? IrCond.NotEqual : IrCond.Equal)),
                            IrOperand.Label(GetLabel(statement.Label))));
                        break;
                    }

                case BoundNodeKind.ReturnStatement:
                    {
                        var statement = (BoundReturnStatement)node;
                        if (statement.Expression != null)
                        {
                            var value = EmitExpression(statement.Expression);
                            Add(instructions, new IrInstruction(IrOpCode.StoreRet, IrOperand.Reg(value)));
                        }

                        Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(_currentFunction.EndLabelId)));
                        break;
                    }

                case BoundNodeKind.ExpressionStatement:
                    EmitExpression(((BoundExpressionStatement)node).Expression);
                    break;

                default:
                    throw new Exception($"Unexpected statement: {node.Kind}");
            }
        }

        // ------------------------------------------------------------------
        // 表达式
        // ------------------------------------------------------------------

        private IrVirtualRegister EmitExpression(BoundExpression node)
        {
            if (node.ConstantValue != null)
            {
                return EmitConstant(node);
            }

            switch (node.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    return EmitLiteralExpression((BoundLiteralExpression)node);

                case BoundNodeKind.VariableExpression:
                    {
                        var variable = ((BoundVariableExpression)node).Variable;

                        // 6e-M22 C5：捕获变量读环境对象字段
                        if (variable.IsCaptured && _closureRegister != null)
                        {
                            var field = _closureClass!.GetField(variable.Name)!;
                            var offset = NativeObjectModel.BuildLayout(_closureClass).Offsets[field];
                            var size = NativeObjectModel.FieldSize(field.Type);
                            var result = AllocateRegister(size);
                            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Load, result, IrOperand.Reg(_closureRegister), IrOperand.None, offset, size));
                            return result;
                        }

                        var value = GetVariable(variable);

                        // 6e-M23 R7：byref 形参读 = 解引用（寄存器持指针）
                        if (variable is ParameterSymbol { IsByRef: true } byRefParameter)
                        {
                            var loaded = AllocateRegister(ReturnSize(byRefParameter.Type));
                            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Load, loaded, IrOperand.Reg(value), IrOperand.None, 0, ReturnSize(byRefParameter.Type)));
                            return loaded;
                        }

                        return value;
                    }

                case BoundNodeKind.AssignmentExpression:
                    {
                        var assignment = (BoundAssignmentExpression)node;
                        var value = EmitExpression(assignment.Expression);

                        // 6e-M22 C5：捕获变量写环境对象字段
                        if (assignment.Variable.IsCaptured && _closureRegister != null)
                        {
                            var field = _closureClass!.GetField(assignment.Variable.Name)!;
                            var offset = NativeObjectModel.BuildLayout(_closureClass).Offsets[field];
                            var size = NativeObjectModel.FieldSize(field.Type);
                            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(_closureRegister), IrOperand.Reg(value), offset, size));
                            return value;
                        }

                        // 6e-M23 R7：byref 形参写 = 穿透指针
                        if (assignment.Variable is ParameterSymbol { IsByRef: true })
                        {
                            var pointer = GetVariable(assignment.Variable);
                            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(pointer), IrOperand.Reg(value), 0, ReturnSize(assignment.Variable.Type)));
                            return value;
                        }

                        var variable = GetVariable(assignment.Variable);
                        Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Mov, variable, IrOperand.Reg(value)));
                        return variable;
                    }

                case BoundNodeKind.UnaryExpression:
                    return EmitUnaryExpression((BoundUnaryExpression)node);

                case BoundNodeKind.BinaryExpression:
                    return EmitBinaryExpression((BoundBinaryExpression)node);

                case BoundNodeKind.ConditionalExpression:
                    return EmitConditionalExpression((BoundConditionalExpression)node);

                case BoundNodeKind.CallExpression:
                    return EmitCallExpression((BoundCallExpression)node);

                case BoundNodeKind.ConversionExpression:
                    return EmitConversionExpression((BoundConversionExpression)node);

                case BoundNodeKind.ArrayCreationExpression:
                    return EmitArrayCreationExpression((BoundArrayCreationExpression)node);

                case BoundNodeKind.ElementAccessExpression:
                    return EmitElementAccessExpression((BoundElementAccessExpression)node);

                case BoundNodeKind.ElementAssignmentExpression:
                    return EmitElementAssignmentExpression((BoundElementAssignmentExpression)node);

                case BoundNodeKind.MemberAccessExpression:
                    return EmitMemberAccessExpression((BoundMemberAccessExpression)node);

                case BoundNodeKind.MemberCallExpression:
                    return EmitMemberCallExpression((BoundMemberCallExpression)node);

                case BoundNodeKind.ThisExpression:
                    return _thisRegister ?? throw new Exception("'this' used outside instance context");

                case BoundNodeKind.BaseExpression:
                    // base 与 this 同一对象表示（字段布局含基类区、直调基类实现由调用端处理）
                    return _thisRegister ?? throw new Exception("'base' used outside instance context");

                case BoundNodeKind.ObjectCreationExpression:
                    return EmitObjectCreationExpression((BoundObjectCreationExpression)node);

                case BoundNodeKind.ConstructorChainExpression:
                    return EmitConstructorChainExpression((BoundConstructorChainExpression)node);

                case BoundNodeKind.MemberAssignmentExpression:
                    return EmitMemberAssignmentExpression((BoundMemberAssignmentExpression)node);

                case BoundNodeKind.FormatExpression:
                    return EmitFormatExpression((BoundFormatExpression)node);

                case BoundNodeKind.IsExpression:
                    return EmitIsExpression((BoundIsExpression)node);

                case BoundNodeKind.AsExpression:
                    return EmitAsExpression((BoundAsExpression)node);

                // 6e-M22 C4-c：函数值对象与间接调用
                case BoundNodeKind.FunctionValueExpression:
                    return EmitFunctionValueExpression((BoundFunctionValueExpression)node);

                case BoundNodeKind.InvocationExpression:
                    return EmitInvocationExpression((BoundInvocationExpression)node);

                case BoundNodeKind.ByRefArgument:
                    return EmitByRefArgument((BoundByRefArgument)node);

                case BoundNodeKind.ErrorExpression:
                    return EmitConst(0);

                default:
                    throw new Exception($"Unexpected expression: {node.Kind}");
            }
        }

        private IrVirtualRegister EmitConstant(BoundExpression node)
        {
            var value = node.ConstantValue!.Value;

            if (value == null)
            {
                var register = AllocateRegister(8);
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(0)));
                return register;
            }

            if (value is string stringValue)
            {
                return EmitStringLiteral(stringValue);
            }

            if (value is int intValue)
            {
                return EmitConst(intValue);
            }

            // 6e-M21 Phase 5：8/16/32 位整数常量统一按 32 位槽发射
            if (value is sbyte or short or byte or ushort or uint)
            {
                return EmitConst((int)System.Convert.ToInt64(value));
            }

            if (value is long longConstValue)
            {
                return EmitLongConst(longConstValue);
            }

            if (value is ulong ulongConstValue)
            {
                return EmitLongConst(unchecked((long)ulongConstValue));
            }

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            if (value is char charValue)
            {
                return EmitConst(charValue);
            }

            if (value is double doubleValue)
            {
                return EmitDoubleConst(doubleValue);
            }

            if (value is float floatConst)
            {
                return EmitFloatConst(floatConst);
            }

            throw new Exception($"Unexpected constant: {value}");
        }

        private IrVirtualRegister EmitLiteralExpression(BoundLiteralExpression node)
        {
            var value = node.Value;

            if (value == null)
            {
                var register = AllocateRegister(8);
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(0)));
                return register;
            }

            if (value is string stringValue)
            {
                return EmitStringLiteral(stringValue);
            }

            if (value is int intValue)
            {
                return EmitConst(intValue);
            }

            // 6e-M21 Phase 5：8/16/32 位整数常量统一按 32 位槽发射
            if (value is sbyte or short or byte or ushort or uint)
            {
                return EmitConst((int)System.Convert.ToInt64(value));
            }

            if (value is long longValue)
            {
                return EmitLongConst(longValue);
            }

            if (value is ulong ulongLiteral)
            {
                return EmitLongConst(unchecked((long)ulongLiteral));
            }

            if (value is bool boolValue)
            {
                return EmitConst(boolValue ? 1 : 0);
            }

            if (value is double doubleValue)
            {
                return EmitDoubleConst(doubleValue);
            }

            if (value is float floatLiteral)
            {
                return EmitFloatConst(floatLiteral);
            }

            throw new Exception($"Unexpected literal: {value}");
        }

        private IrVirtualRegister EmitConst(int value)
        {
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(value)));
            return register;
        }

        /// <summary>64 位整型常量：8 字节槽（x86 由 IrToAssembler 拆低/高两个 dword 立即数）。</summary>
        private IrVirtualRegister EmitLongConst(long value)
        {
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Const, register, IrOperand.Constant(value)));
            return register;
        }

        private IrVirtualRegister EmitStringLiteral(string text)
        {
            var key = _irProgram.InternString(text);
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.LeaData, register, IrOperand.Data(key)));
            return register;
        }

        private IrVirtualRegister EmitDoubleConst(double value)
        {
            var bits = unchecked((long)BitConverter.DoubleToInt64Bits(value));
            var key = "d:" + unchecked((ulong)bits).ToString("X16");
            _irProgram.AddData(IrDataItem.ByteArray(key, new[]
            {
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
                (byte)(bits >> 32), (byte)(bits >> 40), (byte)(bits >> 48), (byte)(bits >> 56),
            }));
            var register = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.FConst, register, IrOperand.Data(key)));
            return register;
        }

        /// <summary>float 常量：4 字节数据段 + FConst（single 标志 → movss 装载）（6e-M21 Phase 5b）。</summary>
        private IrVirtualRegister EmitFloatConst(float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            var key = "f:" + unchecked((uint)bits).ToString("X8");
            _irProgram.AddData(IrDataItem.ByteArray(key, new[]
            {
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
            }));
            var register = AllocateRegister(4);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.FConst, register, IrOperand.Data(key), IrOperand.None, 0, 0, true));
            return register;
        }

        private static int ElementSize(TypeSymbol type)
        {
            if (type == TypeSymbol.Boolean || type == TypeSymbol.UInt8 || type == TypeSymbol.Int8)
            {
                return 1;
            }

            if (type == TypeSymbol.Char || type == TypeSymbol.UInt16 || type == TypeSymbol.Int16)
            {
                return 2;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return 4;
            }

            if (type == TypeSymbol.Int64 || type == TypeSymbol.UInt64 || type == TypeSymbol.Double)
            {
                return 8;
            }

            if (type == TypeSymbol.Float)
            {
                return 4;
            }

            return type == TypeSymbol.Int32 || type == TypeSymbol.UInt32 ? 4 : 8;
        }

        // ------------------------------------------------------------------
        // 数组
        // ------------------------------------------------------------------

        private IrVirtualRegister EmitArrayCreationExpression(BoundArrayCreationExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var elementType = node.Type.ElementType!;
            var elementSize = ElementSize(elementType);

            var length = EmitExpression(node.Length);
            var array = AllocateRegister(8);
            var elementSizeRegister = EmitConst(elementSize);
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(length)));
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(elementSizeRegister)));
            Add(instructions, new IrInstruction(IrOpCode.Call, array, IrOperand.Runtime("NewArray"), IrOperand.Constant(0)));

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                var index = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Const, index, IrOperand.Constant(i)));
                EmitArrayBoundsCheck(instructions, index, length);

                var address = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(array), IrOperand.None, 8 + i * elementSize, 0));
                var value = EmitExpression(node.Initializers[i]);
                Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(address), IrOperand.Reg(value), 0, elementSize));
            }

            // M4：引用类型数组元素零值默认（null）——bump 分配器不复位脏页，逐元素清零（值类型数组不在此列）
            if (elementType is NamedTypeSymbol && !elementType.IsPrimitiveValueType && node.Initializers.Length == 0)
            {
                EmitZeroFillElements(instructions, array, length, elementSize);
            }

            return array;
        }

        /// <summary>M4：把数组数据区（[8..8+len·elem]）清零（i 循环，仅引用宽度场景使用）。</summary>
        private void EmitZeroFillElements(List<IrInstruction> instructions, IrVirtualRegister array, IrVirtualRegister length, int elementSize)
        {
            var index = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, index, IrOperand.Constant(0)));

            var loop = AllocLabel();
            var done = AllocLabel();

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(loop)));
            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(index), IrOperand.Reg(length)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.AboveOrEqual), IrOperand.Label(done)));

            var offset = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Mov, offset, IrOperand.Reg(index)));
            if (elementSize == 2)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(1)));
            }
            else if (elementSize == 4)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(2)));
            }
            else if (elementSize == 8)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(3)));
            }

            var address = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(array), IrOperand.None, 8, 0));
            Add(instructions, new IrInstruction(IrOpCode.Add, address, IrOperand.Reg(address), IrOperand.Reg(offset)));

            var zero = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(address), IrOperand.Reg(zero), 0, elementSize));

            Add(instructions, new IrInstruction(IrOpCode.Add, index, IrOperand.Reg(index), IrOperand.Constant(1)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(loop)));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(done)));
        }

        /// <summary>
        /// byref 实参取址（6e-M23 R7）：变量 → LeaVar（帧槽地址）；静态字段 → 数据段符号地址；
        /// 实例字段 → 对象指针 + 布局偏移；数组元素 → 越界检查后数据区地址（复用 EmitElementAddress）。
        /// </summary>
        private IrVirtualRegister EmitByRefArgument(BoundByRefArgument node)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;

            switch (node.Expression)
            {
                case BoundVariableExpression variable:
                {
                    var variableRegister = GetVariable(variable.Variable);
                    var address = AllocateRegister(pointerSize);
                    Add(instructions, new IrInstruction(IrOpCode.LeaVar, address, IrOperand.Reg(variableRegister)));
                    return address;
                }

                case BoundMemberAccessExpression member when member.Field is { IsStatic: true } staticField:
                    return EmitStaticFieldAddress(staticField);

                case BoundMemberAccessExpression member when member.Field != null:
                {
                    var target = EmitExpression(member.Target);
                    var (offsets, _) = GetLayout((NamedTypeSymbol)member.Field.ContainingClass);
                    var address = AllocateRegister(pointerSize);
                    Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(target), IrOperand.None, offsets[member.Field], 0));
                    return address;
                }

                case BoundElementAccessExpression element when element.Target.Type != TypeSymbol.String &&
                                                               element.Target.Type.ElementType != null:
                {
                    var array = EmitExpression(element.Target);
                    var index = EmitExpression(element.Index);
                    return EmitElementAddress(instructions, array, index, ElementSize(node.Type));
                }

                default:
                    throw new Exception($"Unexpected by-ref argument target {node.Expression.Kind}");
            }
        }

        private IrVirtualRegister EmitElementAccessExpression(BoundElementAccessExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var elementType = node.Type;
            var elementSize = ElementSize(elementType);

            var target = EmitExpression(node.Target);
            var index = EmitExpression(node.Index);

            if (node.Target.Type == TypeSymbol.String)
            {
                // 字符串布局 [len:4][chars:2×len]，数据区紧邻长度头（offset 4）
                var length = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Load, length, IrOperand.Reg(target), IrOperand.None, 0, 4));
                EmitArrayBoundsCheck(instructions, index, length);

                var offset = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Mov, offset, IrOperand.Reg(index)));
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(1)));

                var address = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(target), IrOperand.None, 4, 0));
                Add(instructions, new IrInstruction(IrOpCode.Add, address, IrOperand.Reg(address), IrOperand.Reg(offset)));

                var result = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.Load, result, IrOperand.Reg(address), IrOperand.None, 0, 2));
                return result;
            }

            var array = EmitElementAddress(instructions, target, index, elementSize);

            var value = AllocateRegister(elementSize == 8 ? 8 : 4);
            Add(instructions, new IrInstruction(IrOpCode.Load, value, IrOperand.Reg(array), IrOperand.None, 0, elementSize));
            return value;
        }

        private IrVirtualRegister EmitElementAssignmentExpression(BoundElementAssignmentExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var elementType = node.Target.Type;
            var elementSize = ElementSize(elementType);

            var array = EmitExpression(node.Target.Target);
            var index = EmitExpression(node.Target.Index);
            var address = EmitElementAddress(instructions, array, index, elementSize);
            var value = EmitExpression(node.Expression);
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(address), IrOperand.Reg(value), 0, elementSize));
            return value;
        }

        private IrVirtualRegister EmitMemberAccessExpression(BoundMemberAccessExpression node)
        {
            var instructions = _currentFunction.Instructions;

            // M4：类字段读（含静态字段存储槽）
            if (node.Field != null)
            {
                var field = node.Field;
                var fieldSize = NativeObjectModel.FieldSize(field.Type);

                if (field.IsStatic)
                {
                    var slot = EmitStaticFieldAddress(field);
                    var staticResult = AllocateRegister(fieldSize == 8 ? 8 : 4);
                    Add(instructions, new IrInstruction(IrOpCode.Load, staticResult, IrOperand.Reg(slot), IrOperand.None, 0, fieldSize));
                    return staticResult;
                }

                var target = EmitExpression(node.Target);
                var (offsets, _) = GetLayout((NamedTypeSymbol)field.ContainingClass);
                var offset = offsets[field];
                var result = AllocateRegister(fieldSize == 8 ? 8 : 4);
                Add(instructions, new IrInstruction(IrOpCode.Load, result, IrOperand.Reg(target), IrOperand.None, offset, fieldSize));
                return result;
            }

            var lengthTarget = EmitExpression(node.Target);
            var length = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Load, length, IrOperand.Reg(lengthTarget), IrOperand.None, 0, 4));
            return length;
        }

        /// <summary>M4：静态字段 → 数据段零初始化存储槽（.cctor 触发 native 暂不支持，门禁拒绝非空静态构造）。</summary>
        private IrVirtualRegister EmitStaticFieldAddress(FieldSymbol field)
        {
            var key = NativeObjectModel.StaticFieldKey(field);
            _irProgram.AddData(IrDataItem.ByteArray(key, new byte[NativeObjectModel.FieldSize(field.Type)]));
            var slot = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.LeaData, slot, IrOperand.Data(key)));
            return slot;
        }

        /// <summary>M4：类字段写（求值顺序对齐 IL：target 先于 value）。</summary>
        private IrVirtualRegister EmitMemberAssignmentExpression(BoundMemberAssignmentExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var field = node.Field;

            if (field.IsStatic)
            {
                var staticSlot = EmitStaticFieldAddress(field);
                var staticValue = EmitExpression(node.Expression);
                Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(staticSlot), IrOperand.Reg(staticValue), 0, NativeObjectModel.FieldSize(field.Type)));
                return staticValue;
            }

            var target = EmitExpression(node.Target);
            var value = EmitExpression(node.Expression);
            var (offsets, _) = GetLayout((NamedTypeSymbol)field.ContainingClass);
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(target), IrOperand.Reg(value), offsets[field], NativeObjectModel.FieldSize(field.Type)));
            return value;
        }

        /// <summary>
        /// M4：`new Foo(args)` = Alloc(实例尺寸) + 存 vtable 指针 + 字段清零 + call .ctor(this, args)。
        /// </summary>
        private IrVirtualRegister EmitObjectCreationExpression(BoundObjectCreationExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var classType = (NamedTypeSymbol)node.Type;
            var (offsets, instanceSize) = GetLayout(classType);
            var pointerSize = _isX64 ? 8 : 4;

            var sizeRegister = EmitConst(instanceSize);
            var obj = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(sizeRegister)));
            Add(instructions, new IrInstruction(IrOpCode.Call, obj, IrOperand.Runtime("Alloc"), IrOperand.Constant(0)));

            // 对象头 [0] = 具体类 vtable 指针
            var vtable = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.LeaData, vtable, IrOperand.Data(NativeObjectModel.VTableKey(classType))));
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(obj), IrOperand.Reg(vtable), 0, pointerSize));

            // 字段零值默认（bump 分配器不复位脏页）
            foreach (var field in NativeObjectModel.CollectInstanceFields(classType))
            {
                var fieldSize = NativeObjectModel.FieldSize(field.Type);
                var zero = AllocateRegister(fieldSize == 8 ? 8 : 4);
                Add(instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(obj), IrOperand.Reg(zero), offsets[field], fieldSize));
            }

            var ctor = FindInstanceConstructor(classType);
            EmitInvoke(ctor, obj, node.Arguments);
            return obj;
        }

        /// <summary>M4：`base(...)`/`this(...)` 构造链（ctor 体已由绑定前缀注入，this 即隐藏首参）。null = 链到 Object：no-op。</summary>
        private IrVirtualRegister EmitConstructorChainExpression(BoundConstructorChainExpression node)
        {
            if (node.Constructor == null)
            {
                return VoidResult();
            }

            if (_thisRegister == null)
            {
                throw new Exception("constructor chain outside constructor context");
            }

            EmitInvoke(node.Constructor, _thisRegister, node.Arguments);
            return VoidResult();
        }

        private static FunctionSymbol FindInstanceConstructor(NamedTypeSymbol classType)
        {
            foreach (var method in classType.Methods)
            {
                if (method.IsConstructor && !method.IsStatic)
                {
                    return method;
                }
            }

            throw new Exception($"Class {classType.FullName} has no constructor.");
        }

        /// <summary>类实例布局缓存（偏移按继承链基类在前计算）。</summary>
        private (Dictionary<FieldSymbol, int> Offsets, int InstanceSize) GetLayout(NamedTypeSymbol classType)
        {
            if (!_layoutCache.TryGetValue(classType, out var layout))
            {
                layout = NativeObjectModel.BuildLayout(classType);
                _layoutCache.Add(classType, layout);
            }

            return layout;
        }

        /// <summary>
        /// 函数值对象（6e-M22 C4-c）：`[0]=typeId 占位 0 [ps]=函数地址 [2ps]=环境槽(接收者/0)`。
        /// 函数地址经数据项间接取（复用 vtable 槽的数据→代码绝对重定位机制）：
        /// `__fnptr_&lt;IrName&gt;` = VTable 记录(typeId=0, 单槽)，槽内容为该函数绝对地址，偏移 8+ps。
        /// 静态目标（无 this 槽）经 `EnsureStaticThunk` 适配为统一 env-first 形态。
        /// </summary>
        private IrVirtualRegister EmitFunctionValueExpression(BoundFunctionValueExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;
            var function = node.Function;

            var environmentFirst = node.Receiver != null ||
                                   function.Syntax is LambdaExpressionSyntax ||
                                   HasThisParameter(function);
            var targetIr = environmentFirst
                ? _functionMap[function]
                : EnsureStaticThunk(function);

            var key = "__fnptr_" + targetIr.Name;
            _irProgram.AddData(IrDataItem.VTable(key, 0, _irProgram.InternString(function.Name), new[] { targetIr.Name }));

            // obj = Alloc(pointerSize * 3)
            var sizeRegister = EmitConst(pointerSize * 3);
            var obj = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(sizeRegister)));
            Add(instructions, new IrInstruction(IrOpCode.Call, obj, IrOperand.Runtime("Alloc"), IrOperand.Constant(0)));

            // [0] typeId 槽占位 0（函数值不参与虚分派）
            var zero = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(obj), IrOperand.Reg(zero), 0, 4));

            // [ps] 函数地址：LeaData 地址表 → Load 解引用
            var table = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.LeaData, table, IrOperand.Data(key)));
            var functionPointer = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, functionPointer, IrOperand.Reg(table), IrOperand.None, 8 + pointerSize, pointerSize));
            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(obj), IrOperand.Reg(functionPointer), pointerSize, pointerSize));

            // [2ps] 环境槽：实例方法组 = 接收者；静态/lambda = 0
            IrVirtualRegister environment;
            if (node.Receiver != null)
            {
                environment = EmitExpression(node.Receiver);
                if (_currentFunction.RegisterSize(environment) < 8)
                {
                    environment = WidenTo8(environment);
                }
            }
            else if (node.EnvironmentClass != null)
            {
                // 6e-M22 C5：捕获闭包——环境槽 = 当前函数的环境对象
                environment = _closureRegister ?? throw new Exception("closure lambda created outside environment scope");
            }
            else
            {
                environment = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Const, environment, IrOperand.Constant(0)));
            }

            Add(instructions, new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(obj), IrOperand.Reg(environment), pointerSize * 2, pointerSize));
            return obj;
        }

        /// <summary>
        /// 静态目标的 env 适配器（6e-M22 C4-c）：合成 `__thunk_&lt;IrName&gt;(env, p...) → fn(p...)`，
        /// 丢弃首槽环境值后尾调真实函数——使静态目标与实例方法组/lambda 共享统一 env-first 调用约定。
        /// </summary>
        private IrFunction EnsureStaticThunk(FunctionSymbol function)
        {
            if (_staticThunks.TryGetValue(function, out var existing))
            {
                return existing;
            }

            var realIr = _functionMap[function];
            var thunk = new IrFunction("__thunk_" + NativeObjectModel.FunctionIrName(function), new List<IrParameter>())
            {
                ReturnSize = ReturnSize(function.ReturnType),
            };
            thunk.EndLabelId = AllocLabel();

            var savedCurrent = _currentFunction;
            _currentFunction = thunk;
            var instr = thunk.Instructions;

            // 从自身帧加载原参数：env@0，p_i @ 8 + (x64 ? 8i : Σ 前序尺寸)
            var paramRegisters = new List<IrVirtualRegister>();
            for (var i = 0; i < function.Parameters.Length; i++)
            {
                int offset;
                if (_isX64)
                {
                    offset = 8 + 8 * i;
                }
                else
                {
                    offset = 8;
                    for (var j = 0; j < i; j++)
                    {
                        offset += ReturnSize(function.Parameters[j].Type);
                    }
                }

                var size = ReturnSize(function.Parameters[i].Type);
                var register = AllocateRegister(size);
                Add(instr, new IrInstruction(IrOpCode.InitParam, register, IrOperand.Constant(offset)));
                paramRegisters.Add(register);
            }

            var totalBytes = ParamsTotalBytes(function, function.Parameters.Length);
            Add(instr, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(totalBytes)));

            for (var i = function.Parameters.Length - 1; i >= 0; i--)
            {
                Add(instr, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(ParamByteOffset(function, i, function.Parameters.Length)), IrOperand.Reg(paramRegisters[i])));
            }

            IrVirtualRegister? result = function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(function.ReturnType));
            Add(instr, new IrInstruction(IrOpCode.Call, result, IrOperand.Func(realIr), IrOperand.Constant(0)));
            Add(instr, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(totalBytes)));

            // 返回值走 StoreRet/Ret 固定槽（[rbp-slot]），与 EmitFunction 收尾同构
            if (result != null)
            {
                Add(instr, new IrInstruction(IrOpCode.StoreRet, IrOperand.Reg(result)));
            }

            Add(instr, new IrInstruction(IrOpCode.Ret, IrOperand.Label(thunk.EndLabelId)));

            _currentFunction = savedCurrent;

            _irProgram.Functions.Add(thunk);
            _staticThunks[function] = thunk;
            return thunk;
        }

        /// <summary>间接调用（6e-M22 C4-c）：加载 fnptr/env → 参数区(this=env@0 + 实参) → CallReg。签名形状取自被调者静态函数类型。</summary>
        private IrVirtualRegister EmitInvocationExpression(BoundInvocationExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;
            var functionType = node.Callee.Type switch
            {
                FunctionTypeSymbol ft => ft,
                NamedTypeSymbol { TypeKind: TypeKind.Delegate } dc => dc.DelegateSignature()!,
                _ => throw new Exception($"Unexpected callee type {node.Callee.Type}"),
            };

            var callee = EmitExpression(node.Callee);

            var functionPointer = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, functionPointer, IrOperand.Reg(callee), IrOperand.None, pointerSize, pointerSize));

            var environment = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, environment, IrOperand.Reg(callee), IrOperand.None, pointerSize * 2, pointerSize));

            // 参数区：this(env, 8B) + 实参（x64 每参 8；x86 按 ReturnSize 累计）——与 InvokeVirtualSlot 同构
            var argumentOffsets = new int[node.Arguments.Length];
            var running = 8;
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                argumentOffsets[i] = running;
                running += _isX64 ? 8 : ReturnSize(functionType.ParameterTypes[i]);
            }

            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(running)));
            Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(0), IrOperand.Reg(environment)));

            for (var i = node.Arguments.Length - 1; i >= 0; i--)
            {
                var value = EmitExpression(node.Arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(argumentOffsets[i]), IrOperand.Reg(value)));
            }

            var returnType = functionType.ReturnType;
            IrVirtualRegister? result = returnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(returnType));
            Add(instructions, new IrInstruction(IrOpCode.CallReg, result, IrOperand.None, IrOperand.Reg(functionPointer)));
            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(running)));

            return result ?? VoidResult();
        }

        private IrVirtualRegister EmitMemberCallExpression(BoundMemberCallExpression node)
        {
            if (node.Method?.BuiltinKind != null)
            {
                if (!node.Method.IsStatic)
                {
                    // M4c：Object/Type 内建实例方法（receiver 独立于参数）
                    return EmitObjectFaceInstanceCall(node);
                }

                return EmitBuiltinCall(node.Method, node.Arguments);
            }

            // 静态容器类/facade 降级方法调用（6e-M18 + M2-b：receiver 前置首参）：
            // 统一按用户函数/extern 调用发射，跳过实例表达式
            if (node.Method != null && node.Method.IsStatic)
            {
                // extern 类方法（6e-M17 Step 4）：`Kernel32.GetTickCount()` → 导入表符号
                if (node.Method.IsExtern)
                {
                    return EmitExternCall(node.Method, node.Arguments);
                }

                return EmitFunctionCall(node.Method, node.Arguments);
            }

            // 特殊内建：string.substring（Method 为 null 的历史形状，先于实例分派）
            if (node.Method == null)
            {
                var instructions = _currentFunction.Instructions;
                var target = EmitExpression(node.Expression);
                var start = EmitExpression(node.Arguments[0]);
                var count = EmitExpression(node.Arguments[1]);

                if (node.Expression.Type == TypeSymbol.String && node.Identifier == "substring")
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(target)));
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(start)));
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(2), IrOperand.Reg(count)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Substring"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected member call {node.Identifier}");
            }

            // M4：用户类实例方法——base./非虚直调；virtual/override 经 vtable 虚分派
            var method = node.Method;
            var receiver = EmitExpression(node.Expression);

            var isVirtualDispatch = !node.IsBase && (method.IsVirtual || method.IsOverride);
            if (isVirtualDispatch)
            {
                var functionPointer = EmitLoadVirtualFunctionPointer(receiver, method);
                return EmitInvoke(method, receiver, node.Arguments, indirectFunction: functionPointer);
            }

            return EmitInvoke(method, receiver, node.Arguments);
        }

        /// <summary>M4：加载 receiver 具体类的 vtable 槽函数指针（mov vt=[obj] → mov fn=[vt+8+ps·(slot+1)]）。</summary>
        private IrVirtualRegister EmitLoadVirtualFunctionPointer(IrVirtualRegister receiver, FunctionSymbol method)
        {
            var instructions = _currentFunction.Instructions;
            var root = NativeObjectModel.VirtualRoot(method);
            var slot = _virtualSlots[root];
            var pointerSize = _isX64 ? 8 : 4;

            var vtable = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, vtable, IrOperand.Reg(receiver), IrOperand.None, 0, pointerSize));

            var functionPointer = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, functionPointer, IrOperand.Reg(vtable), IrOperand.None, 8 + pointerSize * (slot + 1), pointerSize));
            return functionPointer;
        }

        // ------------------------------------------------------------------
        // M4c：System.Object / System.Type 内建成员面（native）
        // 类 receiver → 固定槽虚分派（override 经槽内容天然生效）；base. → 运行时默认实现直调；
        // 基元 receiver → 对应运行时原语；Type 值（vtable 记录指针）→ 名字字段读取。
        // ------------------------------------------------------------------

        private IrVirtualRegister EmitObjectFaceInstanceCall(BoundMemberCallExpression node)
        {
            var kind = node.Method!.BuiltinKind!.Value;
            var receiverType = node.Expression.Type;

            switch (kind)
            {
                case BuiltinKind.TypeFullName:
                {
                    // Type 值即 vtable 记录指针：[8] = 类型全名字符串
                    var typeValue = EmitExpression(node.Expression);
                    return EmitLoadPointerField(typeValue, 8);
                }

                case BuiltinKind.TypeName:
                {
                    // Name = TypeSimpleName(FullName)——与 IL 组合语义一致（无点回退全名）
                    var typeValue = EmitExpression(node.Expression);
                    var fullName = EmitLoadPointerField(typeValue, 8);
                    var simple = AllocateRegister(8);
                    Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(fullName)));
                    Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Call, simple, IrOperand.Runtime("TypeSimpleName"), IrOperand.Constant(0)));
                    return simple;
                }

                case BuiltinKind.ObjectGetType:
                {
                    var receiver = EmitExpression(node.Expression);
                    if (receiverType is NamedTypeSymbol { IsValueType: false } userClass && !userClass.IsFacadeClass)
                    {
                        // 用户类：对象头 [0] 即具体类 vtable（= System.Type 实例）
                        return EmitLoadPointerField(receiver, 0);
                    }

                    // 基元/string/facade：伪记录（System.Int32 等）作为 Type 对象
                    return EmitPseudoVTable(FacadeFullNameOfType(receiverType));
                }

                case BuiltinKind.ObjectToString:
                {
                    var receiver = EmitExpression(node.Expression);

                    if (receiverType == TypeSymbol.String)
                    {
                        return receiver; // 字符串值自身即表示
                    }

                    if (receiverType == NamedTypeSymbol.SystemType)
                    {
                        // M4c：Type 值（vtable 记录指针）——名字字段读取。不走槽分派：
                        // 槽 0 可能是用户 override（期望对象 this），对记录指针调用会踩字段布局。
                        // 运行时默认读 [[x]+8] 与 IL(RuntimeType.ToString)/Evaluator 三后端一致。
                        return EmitStackRuntimeCall("ObjectToString", 8, receiver);
                    }

                    if (receiverType is NamedTypeSymbol { IsValueType: false } cls && !cls.IsFacadeClass)
                    {
                        if (node.IsBase)
                        {
                            return EmitRuntimeCall1("ObjectToString", receiver);
                        }

                        return InvokeVirtualSlot(cls, NativeObjectModel.SlotToString, receiver, node.Arguments);
                    }

                    return EmitPrimitiveToString(receiverType, receiver);
                }

                case BuiltinKind.ObjectGetHashCode:
                {
                    var receiver = EmitExpression(node.Expression);

                    if (receiverType == NamedTypeSymbol.SystemType)
                    {
                        return EmitStackRuntimeCall("ObjectGetHashCode", 4, WidenTo8(receiver));
                    }

                    if (receiverType is NamedTypeSymbol { IsValueType: false } hcls && !hcls.IsFacadeClass)
                    {
                        if (node.IsBase)
                        {
                            return EmitRuntimeCall1("ObjectGetHashCode", receiver);
                        }

                        return InvokeVirtualSlot(hcls, NativeObjectModel.SlotGetHashCode, receiver, node.Arguments);
                    }

                    return EmitRuntimeCall1("ObjectGetHashCode", receiver);
                }

                case BuiltinKind.ObjectEquals:
                {
                    var receiver = EmitExpression(node.Expression);

                    if (receiverType == NamedTypeSymbol.SystemType)
                    {
                        var typeOther = EmitExpression(node.Arguments[0]);
                        return EmitStackRuntimeCall("ObjectEquals", 4, WidenTo8(receiver), WidenTo8(typeOther));
                    }

                    if (receiverType is NamedTypeSymbol { IsValueType: false } ecl && !ecl.IsFacadeClass)
                    {
                        if (node.IsBase)
                        {
                            var baseOther = EmitExpression(node.Arguments[0]);
                            return EmitRuntimeCall2("ObjectEquals", receiver, baseOther);
                        }

                        return InvokeVirtualSlot(ecl, NativeObjectModel.SlotEquals, receiver, node.Arguments);
                    }

                    // 基元/string：string×string 走内容比较，其余按位比较（any 位模式直读，不解引用）
                    if (receiverType == TypeSymbol.String && node.Arguments[0].Type == TypeSymbol.String)
                    {
                        var other = EmitExpression(node.Arguments[0]);
                        return EmitRuntimeBinaryValues(receiver, other, "StrEquals", invert: false);
                    }

                    var widenedReceiver = receiver;
                    var otherValue = node.Arguments.Length > 0 ? EmitExpression(node.Arguments[0]) : EmitConst(0);
                    if (_currentFunction.RegisterSize(widenedReceiver) < 8)
                    {
                        widenedReceiver = WidenTo8(widenedReceiver);
                    }

                    if (_currentFunction.RegisterSize(otherValue) < 8)
                    {
                        otherValue = WidenTo8(otherValue);
                    }

                    return EmitRuntimeBinaryValues(widenedReceiver, otherValue, "ObjectEquals", invert: false);
                }

                default:
                    throw new Exception($"Unexpected object face builtin kind: {kind}");
            }
        }

        /// <summary>用户类 receiver 的固定槽分派（ToString/GetHashCode/Equals）。extraArguments 为 Equals 的实参。</summary>
        private IrVirtualRegister InvokeVirtualSlot(NamedTypeSymbol classType, int slotIndex, IrVirtualRegister receiver, ImmutableArray<BoundExpression> extraArguments)
        {
            var instructions = _currentFunction.Instructions;
            var pointerSize = _isX64 ? 8 : 4;

            var vtable = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, vtable, IrOperand.Reg(receiver), IrOperand.None, 0, pointerSize));

            var functionPointer = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Load, functionPointer, IrOperand.Reg(vtable), IrOperand.None, 8 + pointerSize * (slotIndex + 1), pointerSize));

            // 参数区：this(8) + 实参（x64 每参 8；x86 按 ReturnSize 累计）
            var argumentOffsets = new int[extraArguments.Length];
            var running = 8;
            for (var i = 0; i < extraArguments.Length; i++)
            {
                argumentOffsets[i] = running;
                running += _isX64 ? 8 : ReturnSize(extraArguments[i].Type);
            }

            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(running)));
            Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(0), IrOperand.Reg(receiver)));

            for (var i = extraArguments.Length - 1; i >= 0; i--)
            {
                var value = EmitExpression(extraArguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(argumentOffsets[i]), IrOperand.Reg(value)));
            }

            var resultSize = slotIndex == NativeObjectModel.SlotToString ? 8 : 4;
            var result = AllocateRegister(resultSize);
            Add(instructions, new IrInstruction(IrOpCode.CallReg, result, IrOperand.None, IrOperand.Reg(functionPointer)));
            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(running)));
            return result;
        }

        private IrVirtualRegister EmitLoadPointerField(IrVirtualRegister baseRegister, int offset)
        {
            var result = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Load, result, IrOperand.Reg(baseRegister), IrOperand.None, offset, _isX64 ? 8 : 4));
            return result;
        }

        /// <summary>M4c：基元/伪记录 vtable（System.Type 对象），typeId=-1 表示自引用头。</summary>
        private IrVirtualRegister EmitPseudoVTable(string fullName)
        {
            var key = NativeObjectModel.PseudoVTableKey(fullName);
            if (_pseudoVTableKeys.Add(key))
            {
                _irProgram.AddData(IrDataItem.VTable(key, -1, _irProgram.InternString(fullName), NativeObjectModel.ObjectSlotFunctions));
            }

            var vtable = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.LeaData, vtable, IrOperand.Data(key)));
            return vtable;
        }

        /// <summary>类型对应的封装类全名（伪 vtable 名字来源）。</summary>
        private static string FacadeFullNameOfType(TypeSymbol type)
        {
            if (type == NamedTypeSymbol.SystemType)
            {
                return "System.Type";
            }

            if (type.ElementType != null)
            {
                return "System.Array";
            }

            switch (type.Name)
            {
                case "int": return "System.Int32";
                case "long": return "System.Int64";
                case "double": return "System.Double";
                case "bool": return "System.Boolean";
                case "char": return "System.Char";
                case "byte": return "System.Byte";
                case "sbyte": return "System.SByte";
                case "short": return "System.Int16";
                case "ushort": return "System.UInt16";
                case "uint": return "System.UInt32";
                case "ulong": return "System.UInt64";
                case "float": return "System.Single";
                case "string": return "System.String";
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return enumType.FullName;
            }

            if (type is NamedTypeSymbol classType)
            {
                return classType.FullName;
            }

            return type.Name;
        }

        /// <summary>基元 ToString 分发（facade 未覆盖时的回退路径）。</summary>
        private IrVirtualRegister EmitPrimitiveToString(TypeSymbol type, IrVirtualRegister value)
        {
            var instructions = _currentFunction.Instructions;

            if (type == TypeSymbol.Boolean)
            {
                return EmitSelectString("True", "False", value);
            }

            if (type == TypeSymbol.Double)
            {
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                return text;
            }

            if (type == TypeSymbol.Float)
            {
                var asDouble = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, asDouble, IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(asDouble)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                return text;
            }

            if (type == TypeSymbol.Int64 || type == TypeSymbol.UInt64)
            {
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                return text;
            }

            if (type == TypeSymbol.Char)
            {
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                return text;
            }

            // int/uint/sbyte/short/ushort/byte/enum：32 位规范值
            var intText = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
            Add(instructions, new IrInstruction(IrOpCode.Call, intText, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
            return intText;
        }

        /// <summary>单参运行时调用（栈 ABI，M4；8 字节槽读取场景由调用方先 WidenTo8）。</summary>
        private IrVirtualRegister EmitRuntimeCall1(string runtimeName, IrVirtualRegister value)
            => EmitStackRuntimeCall(runtimeName, 8, value);

        private IrVirtualRegister EmitRuntimeCall2(string runtimeName, IrVirtualRegister first, IrVirtualRegister second)
            => EmitStackRuntimeCall(runtimeName, 4, first, second);

        /// <summary>求值并把窄于 8 字节的值零扩展（ObjectGetHashCode 等按 8 字节槽读取的场景）。</summary>
        private IrVirtualRegister EmitWidenedArgument(BoundExpression expression)
        {
            var value = EmitExpression(expression);
            return _currentFunction.RegisterSize(value) < 8 ? WidenTo8(value) : value;
        }

        private IrVirtualRegister WidenTo8(IrVirtualRegister value)
        {
            var widened = AllocateRegister(8);
            Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Movzx64, widened, IrOperand.Reg(value)));
            return widened;
        }

        /// <summary>两个已求值寄存器的运行时布尔比较。StrEquals 保持寄存器 ABI；ObjectEquals 走栈 ABI（M4）。</summary>
        private IrVirtualRegister EmitRuntimeBinaryValues(IrVirtualRegister left, IrVirtualRegister right, string runtimeName, bool invert)
        {
            var result = AllocateRegister(4);
            if (runtimeName == "ObjectEquals")
            {
                result = EmitStackRuntimeCall(runtimeName, 4, left, right);
            }
            else
            {
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(left)));
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(right)));
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime(runtimeName), IrOperand.Constant(0)));
            }

            if (invert)
            {
                Add(_currentFunction.Instructions, new IrInstruction(IrOpCode.Xor, result, IrOperand.Reg(result), IrOperand.Constant(1)));
            }

            return result;
        }

        /// <summary>M4c：Object.Equals(a,b)/Object.ReferenceEquals(a,b) 静态相等。双侧值类型 → 常量 false（装箱语义）；否则 ObjectEquals 指针比较。</summary>
        private IrVirtualRegister EmitObjectStaticEquality(ImmutableArray<BoundExpression> arguments)
        {
            static bool IsPureValue(BoundExpression expression)
            {
                // 解开装箱转换取原始操作数类型（参数已转换为 any，直接看类型恒非值）
                var current = expression;
                while (current is BoundConversionExpression conversion)
                {
                    current = conversion.Expression;
                }

                var type = current.Type;
                if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
                {
                    return true;
                }

                if ((type is NamedTypeSymbol { IsValueType: false }) ||
                    (type is NamedTypeSymbol { TypeKind: TypeKind.Struct } && !type.IsPrimitiveValueType) ||
                    type.ElementType != null)
                {
                    return false;
                }

                return type != TypeSymbol.String && type != TypeSymbol.Any && type != TypeSymbol.Void && type != TypeSymbol.Error;
            }

            if (IsPureValue(arguments[0]) && IsPureValue(arguments[1]))
            {
                return EmitConst(0);
            }

            var left = EmitWidenedArgument(arguments[0]);
            var right = EmitWidenedArgument(arguments[1]);
            return EmitRuntimeBinaryValues(left, right, "ObjectEquals", invert: false);
        }

        private IrVirtualRegister EmitBuiltinCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            var instructions = _currentFunction.Instructions;

            switch (function.BuiltinKind)
            {
                case BuiltinKind.WriteLine:
                {
                    EmitPrintArguments(arguments[0]);
                    return VoidResult();
                }
                case BuiltinKind.Write:
                {
                    EmitWriteArguments(arguments[0], newline: false);
                    return VoidResult();
                }
                case BuiltinKind.ReadLine:
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Input"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.ReadKey:
                {
                    var intercept = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(intercept)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ReadKey"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Random:
                {
                    var argument = EmitExpression(arguments[0]);
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(argument)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Random"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Sleep:
                {
                    var ms = EmitExpression(arguments[0]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(ms)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("Sleep"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Beep:
                {
                    var frequency = EmitExpression(arguments[0]);
                    var duration = EmitExpression(arguments[1]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(frequency)));
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(duration)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("Beep"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Int32ToString:
                {
                    // int → 字符串：复用打印通道的 IntToString 运行时 helper
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Int64ToString:
                {
                    // long → 字符串：Int64ToString（x64 单 64 位参；x86 拆 low/high 两寄存器，SetArg64 统一）
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.DoubleToString:
                {
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    if (_isX64)
                    {
                        Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    }
                    else
                    {
                        Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                        Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(value)));
                    }

                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.BooleanToString:
                {
                    var value = EmitExpression(arguments[0]);
                    return EmitSelectString("True", "False", value);
                }
                case BuiltinKind.CharToString:
                {
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.StringFromChars:
                {
                    // 6e-G7 ③a：native 运行时实现下一批接入（Evaluator/IL 已可用）
                    throw new Exception("StringFromChars native runtime lands in the next batch (G7-③a follow-up)");
                }
                case BuiltinKind.FileReadAllText:
                case BuiltinKind.FileWriteAllText:
                case BuiltinKind.FileExists:
                case BuiltinKind.FileDelete:
                case BuiltinKind.FileCopy:
                case BuiltinKind.DirectoryExists:
                case BuiltinKind.GetEnvironmentVariable:
                case BuiltinKind.GetCurrentDirectory:
                case BuiltinKind.SetCurrentDirectory:
                case BuiltinKind.GetExecutablePath:
                {
                    // 6e-G7 ④：文件 IO / 环境 syscall native 接入下一批
                    throw new Exception($"Builtin '{function.BuiltinKind}' native runtime lands in a follow-up batch (G7-④)");
                }
                case BuiltinKind.ParseInt64:
                {
                    // string → long：ParseInt64（返回 8 字节，x64 RAX / x86 EDX:EAX）
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseInt64"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.UInt64ToString:
                {
                    // ulong → 字符串：UInt64ToString（无符号十进制，SetArg64 统一双架构）
                    var value = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.ObjectStaticEquals:
                case BuiltinKind.ObjectReferenceEquals:
                {
                    // M4c：装箱语义——双侧均为值类型时恒 false（各自独立表示）；否则指针比较
                    return EmitObjectStaticEquality(arguments);
                }
                case BuiltinKind.TickCount:
                {
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("TickCount"), IrOperand.Constant(0)));
                    return result;
                }
                case BuiltinKind.Exit:
                {
                    var code = EmitExpression(arguments[0]);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(code)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("ExitProcess"), IrOperand.Constant(0)));
                    return VoidResult();
                }
                case BuiltinKind.Sqrt:
                case BuiltinKind.Floor:
                case BuiltinKind.Ceiling:
                case BuiltinKind.Truncate:
                case BuiltinKind.Round:
                {
                    var x = EmitExpression(arguments[0]);
                    var result = AllocateRegister(8);
                    var op = function.BuiltinKind switch
                    {
                        BuiltinKind.Sqrt => IrOpCode.FSqrt,
                        BuiltinKind.Floor => IrOpCode.FFloor,
                        BuiltinKind.Ceiling => IrOpCode.FCeiling,
                        BuiltinKind.Truncate => IrOpCode.FTruncate,
                        _ => IrOpCode.FRound,
                    };
                    Add(instructions, new IrInstruction(op, result, IrOperand.Reg(x)));
                    return result;
                }
                default:
                    throw new Exception($"Unknown builtin kind {function.BuiltinKind}");
            }
        }

        /// <summary>插值洞对齐/格式：单一 StringFormat 入口（value, fmtPtr, fmtLen, width, typeKind）。格式串运行时解析，对齐统一处理。
        /// 6e-M21 Phase 7：新数值类型（i8/i16/u8/u16/u32/u64/f32）预转换为字符串后走 string 通道。</summary>
        private IrVirtualRegister EmitFormatExpression(BoundFormatExpression node)
        {
            var type = node.Value.Type;
            var format = node.Format;
            var width = node.Width ?? 0;

            var value = EmitExpression(node.Value);
            var instructions = _currentFunction.Instructions;

            // 新类型预转字符串（复用既有 ToString 原语），统一走 string 通道
            if (type == TypeSymbol.Float)
            {
                var asDouble = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, asDouble, IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(asDouble)));
                value = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, value, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                type = TypeSymbol.String;
            }
            else if (type == TypeSymbol.UInt32 || type == TypeSymbol.UInt64)
            {
                var src = value;
                if (type == TypeSymbol.UInt32)
                {
                    src = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, src, IrOperand.Reg(value)));
                }

                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(src)));
                value = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, value, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                type = TypeSymbol.String;
            }
            else if (type == TypeSymbol.Int8 || type == TypeSymbol.Int16 ||
                     type == TypeSymbol.UInt8 || type == TypeSymbol.UInt16)
            {
                // 窄整型槽内已是 32 位规范表示，直接走 IntToString
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                value = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, value, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
                type = TypeSymbol.String;
            }

            int typeKind;
            if (type == TypeSymbol.String) typeKind = 2;
            else if (type == TypeSymbol.Boolean) typeKind = 3;
            else if (type == TypeSymbol.Char) typeKind = 4;
            else if (type == TypeSymbol.Double) typeKind = 1;
            else if (type == TypeSymbol.Int64) typeKind = 5; // M1：long 插值格式仅默认十进制（StringFormat 内忽略格式码，见开发计划）
            else typeKind = 0; // int / byte / enum

            var fmtPtr = EmitStringLiteral(format ?? "");

            return EmitStringFormatCall(value, fmtPtr, width, typeKind);
        }

        private IrVirtualRegister EmitStringFormatCall(IrVirtualRegister value, IrVirtualRegister fmtPtr, int width, int typeKind)
        {
            var instructions = _currentFunction.Instructions;
            var packed = ((width & 0xFFFF) << 4) | (typeKind & 0xF);
            var result = AllocateRegister(8);
            var is64 = typeKind == 1 || typeKind == 5; // double / long：值按 64 位传参（x86 拆 low/high）
            if (is64)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 1 : 2), IrOperand.Reg(fmtPtr)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 2 : 3), IrOperand.Reg(EmitConst(packed))));
            }
            else
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                if (!_isX64)
                {
                    // x86 StringFormat 按 (low, high, fmtPtr, packed) 布局接收，非 double 用占位 high
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(value)));
                }
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 1 : 2), IrOperand.Reg(fmtPtr)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(_isX64 ? 2 : 3), IrOperand.Reg(EmitConst(packed))));
            }
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("StringFormat"), IrOperand.Constant(0)));
            return result;
        }

        private IrVirtualRegister EmitElementAddress(List<IrInstruction> instructions, IrVirtualRegister array, IrVirtualRegister index, int elementSize)
        {
            var length = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Load, length, IrOperand.Reg(array), IrOperand.None, 0, 4));
            EmitArrayBoundsCheck(instructions, index, length);

            var offset = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Mov, offset, IrOperand.Reg(index)));
            if (elementSize == 2)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(1)));
            }
            else if (elementSize == 4)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(2)));
            }
            else if (elementSize == 8)
            {
                Add(instructions, new IrInstruction(IrOpCode.Shl, offset, IrOperand.Reg(offset), IrOperand.Constant(3)));
            }

            var address = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Lea, address, IrOperand.Reg(array), IrOperand.None, 8, 0));
            Add(instructions, new IrInstruction(IrOpCode.Add, address, IrOperand.Reg(address), IrOperand.Reg(offset)));
            return address;
        }

        private void EmitArrayBoundsCheck(List<IrInstruction> instructions, IrVirtualRegister index, IrVirtualRegister length)
        {
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(index)));
            Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(length)));
            Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime("ArrayBoundsCheck"), IrOperand.Constant(0)));
        }

        private IrVirtualRegister EmitUnaryExpression(BoundUnaryExpression node)
        {
            var operand = EmitExpression(node.Operand);
            var instructions = _currentFunction.Instructions;

            switch (node.Op.Kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    return operand;

                case BoundUnaryOperatorKind.Negation:
                    {
                        if (node.Operand.Type == TypeSymbol.Float)
                        {
                            // 6e-M21 Phase 5b：单精度取反（4 字节槽翻转符号位）
                            var resultSingle = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.FMov, resultSingle, IrOperand.Reg(operand), IrOperand.None, 0, 0, true));
                            Add(instructions, new IrInstruction(IrOpCode.FNeg, resultSingle, IrOperand.None, IrOperand.None, 0, 0, true));
                            return resultSingle;
                        }

                        if (node.Operand.Type == TypeSymbol.Double)
                        {
                            var result = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.FMov, result, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.FNeg, result));
                            return result;
                        }

                        if (node.Operand.Type == TypeSymbol.Int64 || node.Operand.Type == TypeSymbol.UInt64)
                        {
                            var resultLong = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.Mov, resultLong, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.Neg64, resultLong));
                            return resultLong;
                        }

                        var resultInt = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, resultInt, IrOperand.Reg(operand)));
                        Add(instructions, new IrInstruction(IrOpCode.Neg, resultInt));
                        return resultInt;
                    }

                case BoundUnaryOperatorKind.LogicalNegation:
                    {
                        var result = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(operand), IrOperand.Constant(0)));
                        Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Equal)));
                        return result;
                    }

                case BoundUnaryOperatorKind.OnesComplement:
                    {
                        if (node.Operand.Type == TypeSymbol.Int64)
                        {
                            var resultLong = AllocateRegister(8);
                            Add(instructions, new IrInstruction(IrOpCode.Mov, resultLong, IrOperand.Reg(operand)));
                            Add(instructions, new IrInstruction(IrOpCode.Not64, resultLong));
                            return resultLong;
                        }

                        var result = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(operand)));
                        Add(instructions, new IrInstruction(IrOpCode.Not, result));
                        return result;
                    }

                default:
                    throw new Exception($"Unexpected unary operator: {node.Op.Kind}");
            }
        }

        private IrVirtualRegister EmitBinaryExpression(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;

            if (op == BoundBinaryOperatorKind.Addition && node.Left.Type == TypeSymbol.String)
            {
                var concatLeft = EmitExpression(node.Left);
                var concatRight = EmitExpression(node.Right);
                if (node.Right.Type == TypeSymbol.Double)
                {
                    var text = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(concatRight)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                    concatRight = text;
                }

                var concatResult = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(concatLeft)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(concatRight)));
                Add(instructions, new IrInstruction(IrOpCode.Call, concatResult, IrOperand.Runtime("Concat"), IrOperand.Constant(0)));
                return concatResult;
            }

            if ((op == BoundBinaryOperatorKind.Equals || op == BoundBinaryOperatorKind.NotEquals) &&
                node.Left.Type == TypeSymbol.String)
            {
                return EmitRuntimeBinary(node, "StrEquals", 4, invert: op == BoundBinaryOperatorKind.NotEquals);
            }

            if ((op == BoundBinaryOperatorKind.Equals || op == BoundBinaryOperatorKind.NotEquals) &&
                node.Left.Type == TypeSymbol.Any)
            {
                return EmitRuntimeBinary(node, "ObjectEquals", 4, invert: op == BoundBinaryOperatorKind.NotEquals);
            }

            if (node.Left.Type.IsFloat)
            {
                return EmitFloatBinary(node);
            }

            if (node.Left.Type == TypeSymbol.Int64 || node.Left.Type == TypeSymbol.UInt64)
            {
                return EmitLongBinary(node);
            }

            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(4);

            // 6e-M21 Phase 5：8/16/32 位整数统一在 32 位槽运算，无符号类型选择无符号语义指令
            var isUnsigned = node.Left.Type.IsInteger && !node.Left.Type.IsSigned;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    Add(instructions, new IrInstruction(IrOpCode.Add, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    // 逻辑与用位与（0/1 布尔语义 = && 结果；三后端一致：Evaluator/IL 均为急切求值）
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new IrInstruction(IrOpCode.Sub, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new IrInstruction(IrOpCode.Imul, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Udiv : IrOpCode.Idiv, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Urem : IrOpCode.Irem, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new IrInstruction(IrOpCode.Shl, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    // 无符号类型为逻辑右移（Shr），有符号为算术右移（Sar）
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Shr : IrOpCode.Sar, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.LogicalOr:
                    Add(instructions, new IrInstruction(IrOpCode.Or, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseXor:
                    Add(instructions, new IrInstruction(IrOpCode.Xor, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Equals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Equal)));
                    break;

                case BoundBinaryOperatorKind.NotEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.NotEqual)));
                    break;

                // 6e-M19 M2-c：类类型引用相等——M4 前 native 对象即指针，直接位比较
                case BoundBinaryOperatorKind.ReferenceEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.Equal)));
                    break;

                case BoundBinaryOperatorKind.ReferenceNotEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)IrCond.NotEqual)));
                    break;

                case BoundBinaryOperatorKind.Less:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.Below : IrCond.Less))));
                    break;

                case BoundBinaryOperatorKind.LessOrEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.BelowOrEqual : IrCond.LessOrEqual))));
                    break;

                case BoundBinaryOperatorKind.Greater:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.Above : IrCond.Greater))));
                    break;

                case BoundBinaryOperatorKind.GreaterOrEquals:
                    Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(left), IrOperand.Reg(right)));
                    Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)(isUnsigned ? IrCond.AboveOrEqual : IrCond.GreaterOrEqual))));
                    break;

                default:
                    throw new Exception($"Unexpected binary operator: {op}");
            }

            return result;
        }

        /// <summary>long/u64 二元运算（6e-M19 M1）：算术/位/移位/比较走 64 位 IR 指令；u64 无符号语义（Phase 5）。</summary>
        private IrVirtualRegister EmitLongBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);
            var result = AllocateRegister(8);

            // 6e-M21 Phase 5：u64 走无符号语义（Udiv64/Urem64、Shr64 逻辑右移、无符号比较）
            var isUnsigned = node.Left.Type == TypeSymbol.UInt64;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                    Add(instructions, new IrInstruction(IrOpCode.Add64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Subtraction:
                    Add(instructions, new IrInstruction(IrOpCode.Sub64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Multiplication:
                    Add(instructions, new IrInstruction(IrOpCode.Imul64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Division:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Udiv64 : IrOpCode.Idiv64, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Modulo:
                    Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(left)));
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Urem64 : IrOpCode.Irem64, result, IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.LogicalAnd:
                    Add(instructions, new IrInstruction(IrOpCode.And64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.LogicalOr:
                    Add(instructions, new IrInstruction(IrOpCode.Or64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.BitwiseXor:
                    Add(instructions, new IrInstruction(IrOpCode.Xor64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftLeft:
                    Add(instructions, new IrInstruction(IrOpCode.Shl64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.ShiftRight:
                    // u64 为逻辑右移（Shr64），i64 为算术右移（Sar64）
                    Add(instructions, new IrInstruction(isUnsigned ? IrOpCode.Shr64 : IrOpCode.Sar64, result, IrOperand.Reg(left), IrOperand.Reg(right)));
                    break;

                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.NotEquals:
                case BoundBinaryOperatorKind.Less:
                case BoundBinaryOperatorKind.LessOrEquals:
                case BoundBinaryOperatorKind.Greater:
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    {
                        var boolResult = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Cmp64, IrOperand.Reg(left), IrOperand.Reg(right)));

                        IrCond cond = op switch
                        {
                            BoundBinaryOperatorKind.Equals => IrCond.Equal,
                            BoundBinaryOperatorKind.NotEquals => IrCond.NotEqual,
                            BoundBinaryOperatorKind.Less => isUnsigned ? IrCond.Below : IrCond.Less,
                            BoundBinaryOperatorKind.LessOrEquals => isUnsigned ? IrCond.BelowOrEqual : IrCond.LessOrEqual,
                            BoundBinaryOperatorKind.Greater => isUnsigned ? IrCond.Above : IrCond.Greater,
                            _ => isUnsigned ? IrCond.AboveOrEqual : IrCond.GreaterOrEqual,
                        };
                        Add(instructions, new IrInstruction(IrOpCode.Setcc, boolResult, IrOperand.Constant((int)cond)));
                        return boolResult;
                    }

                default:
                    throw new Exception($"Unexpected long binary operator: {op}");
            }

            return result;
        }

        private IrVirtualRegister EmitFloatBinary(BoundBinaryExpression node)
        {
            var op = node.Op.Kind;
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            // 6e-M21 Phase 5b：f32 走真正单精度 SSE（ss 族），f64 保持双精度
            var single = node.Left.Type == TypeSymbol.Float;
            var resultSize = single ? 4 : 8;

            switch (op)
            {
                case BoundBinaryOperatorKind.Addition:
                case BoundBinaryOperatorKind.Subtraction:
                case BoundBinaryOperatorKind.Multiplication:
                case BoundBinaryOperatorKind.Division:
                    {
                        var result = AllocateRegister(resultSize);
                        var fOp = op switch
                        {
                            BoundBinaryOperatorKind.Addition => IrOpCode.FAdd,
                            BoundBinaryOperatorKind.Subtraction => IrOpCode.FSub,
                            BoundBinaryOperatorKind.Multiplication => IrOpCode.FMul,
                            _ => IrOpCode.FDiv,
                        };
                        Add(instructions, new IrInstruction(fOp, result, IrOperand.Reg(left), IrOperand.Reg(right), 0, 0, single));
                        return result;
                    }

                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.NotEquals:
                case BoundBinaryOperatorKind.Less:
                case BoundBinaryOperatorKind.LessOrEquals:
                case BoundBinaryOperatorKind.Greater:
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    {
                        var result = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.FCmp, null, IrOperand.Reg(left), IrOperand.Reg(right), 0, 0, single));

                        // ucomisd 在 unordered（NaN 参与）时置 ZF=PF=CF=1；
                        // 全部 6 个比较条件对 NaN 一律 false、!= 对 NaN 为 true（IEEE-754 语义）。
                        var (main, fixup) = op switch
                        {
                            BoundBinaryOperatorKind.Equals => (IrCond.Equal, IrCond.NoParity),
                            BoundBinaryOperatorKind.NotEquals => (IrCond.NotEqual, IrCond.Parity),
                            BoundBinaryOperatorKind.Less => (IrCond.Below, IrCond.NoParity),
                            BoundBinaryOperatorKind.LessOrEquals => (IrCond.BelowOrEqual, IrCond.NoParity),
                            BoundBinaryOperatorKind.Greater => (IrCond.Above, IrCond.NoParity),
                            _ => (IrCond.AboveOrEqual, IrCond.NoParity),
                        };

                        Add(instructions, new IrInstruction(IrOpCode.Setcc, result, IrOperand.Constant((int)main)));
                        if (fixup == IrCond.NoParity)
                        {
                            var clear = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Setcc, clear, IrOperand.Constant((int)IrCond.NoParity)));
                            Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(result), IrOperand.Reg(clear)));
                        }
                        else
                        {
                            var mark = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Setcc, mark, IrOperand.Constant((int)IrCond.Parity)));
                            Add(instructions, new IrInstruction(IrOpCode.Or, result, IrOperand.Reg(result), IrOperand.Reg(mark)));
                        }

                        return result;
                    }

                default:
                    throw new Exception($"Unexpected float binary operator: {op}");
            }
        }

        private IrVirtualRegister EmitConditionalExpression(BoundConditionalExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var result = AllocateRegister(ReturnSize(node.Type));
            var elseLabel = AllocLabel();
            var endLabel = AllocLabel();

            var condition = EmitExpression(node.Condition);
            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(elseLabel)));

            var whenTrue = EmitExpression(node.WhenTrue);
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(whenTrue)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(endLabel)));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(elseLabel)));
            var whenFalse = EmitExpression(node.WhenFalse);
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(whenFalse)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(endLabel)));

            return result;
        }

        /// <summary>6e-M19 M5-b：is 动态判定——[obj] vtable 与目标祖先链 vtable 指针逐一比对（仅严格基类接收者到达）。</summary>
        private IrVirtualRegister EmitIsExpression(BoundIsExpression node)
        {
            var result = AllocateRegister(4);
            var obj = EmitExpression(node.Expression);
            EmitTypeChainCompare(obj, node.TargetType, out var found, out var notFound, out var done);

            var instructions = _currentFunction.Instructions;
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(found)));
            var one = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, one, IrOperand.Constant(1)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(one)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(done)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(notFound)));
            var zero = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.Const, zero, IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(zero)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(done)));

            return result;
        }

        /// <summary>6e-M19 M5-b：as 动态转换——同一链比对，命中返回原引用、失败得 null（0）。</summary>
        private IrVirtualRegister EmitAsExpression(BoundAsExpression node)
        {
            var result = AllocateRegister(8);
            var obj = EmitExpression(node.Expression);
            EmitTypeChainCompare(obj, node.TargetType, out var found, out var notFound, out var done);

            var instructions = _currentFunction.Instructions;
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(found)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(obj)));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(done)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(notFound)));
            var nullReg = AllocateRegister(8);
            Add(instructions, new IrInstruction(IrOpCode.Const, nullReg, IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(nullReg)));
            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(done)));

            return result;
        }

        /// <summary>发射 obj（可空）对目标类的类型链比较：null 短路未命中；命中/未命中/汇合三标签交调用方回填结果。</summary>
        private void EmitTypeChainCompare(IrVirtualRegister obj, TypeSymbol targetType, out int found, out int notFound, out int done)
        {
            var instructions = _currentFunction.Instructions;
            var ps = _isX64 ? 8 : 4;
            found = AllocLabel();
            notFound = AllocLabel();
            done = AllocLabel();

            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(obj), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(notFound)));

            var curVt = AllocateRegister(ps);
            Add(instructions, new IrInstruction(IrOpCode.Load, curVt, IrOperand.Reg(obj), IrOperand.None, 0, ps));

            var candidate = AllocateRegister(ps);
            foreach (var key in EnumerateDescendantVTableKeys((NamedTypeSymbol)targetType))
            {
                Add(instructions, new IrInstruction(IrOpCode.LeaData, candidate, IrOperand.Data(key)));
                Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(curVt), IrOperand.Reg(candidate)));
                Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(found)));
            }

            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(notFound)));
        }

        /// <summary>
        /// 6e-M19 M5-b：x is/as T 的运行时命中集 = 存活类中 T 的自身与全部后代（vtable 一一比对）。
        /// 对象头只存自身 vtable 地址、无向下类型信息，故以编译期存活类闭包枚举后代；
        /// 抽象/接口/根不实例化（不在 _liveClasses），行序取 Ordinal 保证确定性。
        /// </summary>
        private IEnumerable<string> EnumerateDescendantVTableKeys(NamedTypeSymbol targetClass)
        {
            return _liveClasses
                .Where(c => c == targetClass || targetClass.IsBaseOf(c))
                .OrderBy(c => c.FullName, System.StringComparer.Ordinal)
                .Select(NativeObjectModel.VTableKey);
        }

        private IrVirtualRegister EmitRuntimeBinary(BoundBinaryExpression node, string runtimeName, int resultSize, bool invert = false)
        {
            var instructions = _currentFunction.Instructions;
            var left = EmitExpression(node.Left);
            var right = EmitExpression(node.Right);

            IrVirtualRegister result;
            if (runtimeName == "ObjectEquals")
            {
                // M4：ObjectEquals 为栈 ABI（与 vtable 槽共享实现）
                result = EmitStackRuntimeCall(runtimeName, resultSize, WidenTo8(left), WidenTo8(right));
            }
            else
            {
                result = AllocateRegister(resultSize);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(left)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(1), IrOperand.Reg(right)));
                Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime(runtimeName), IrOperand.Constant(0)));
            }

            if (invert)
            {
                Add(instructions, new IrInstruction(IrOpCode.Xor, result, IrOperand.Reg(result), IrOperand.Constant(1)));
            }

            return result;
        }

        /// <summary>
        /// M4：栈 ABI 运行时调用。ObjectToString/ObjectGetHashCode/ObjectGetType/ObjectEquals 四个运行时
        /// 函数同时作为 vtable 固定槽默认实现（槽内容可能是用户 override，callreg 无法区分 ABI），
        /// 故统一采用与用户函数一致的 ReserveArgs/StoreArg 栈传参约定；参数一律 8 字节槽。
        /// </summary>
        private IrVirtualRegister EmitStackRuntimeCall(string name, int resultSize, params IrVirtualRegister[] args)
        {
            var instructions = _currentFunction.Instructions;
            var totalBytes = 8 * args.Length;

            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(totalBytes)));
            for (var i = args.Length - 1; i >= 0; i--)
            {
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(8 * i), IrOperand.Reg(args[i])));
            }

            var result = AllocateRegister(resultSize);
            Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime(name), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(totalBytes)));
            return result;
        }

        // ------------------------------------------------------------------
        // 函数调用
        // ------------------------------------------------------------------

        private IrVirtualRegister _voidResult = null!;

        private IrVirtualRegister EmitCallExpression(BoundCallExpression node)
        {
            if (node.Function.BuiltinKind != null)
            {
                return EmitBuiltinCall(node.Function, node.Arguments);
            }

            if (node.Function.IsExtern)
            {
                return EmitExternCall(node);
            }

            return EmitUserCall(node);
        }

        private IrVirtualRegister EmitExternCall(BoundCallExpression node)
        {
            return EmitExternCall(node.Function, node.Arguments);
        }

        private IrVirtualRegister EmitExternCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            var instructions = _currentFunction.Instructions;
            var count = arguments.Length;

            // 平台化 SysCall：x64 寄存器 + 第 5 参槽 / x86 栈传递；当前上限 5 参（与运行时所一致）
            if (count > 5)
            {
                throw new Exception($"Extern function '{function.Name}' has {count} parameters; native backend supports at most 5");
            }

            for (var i = 0; i < count; i++)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(i), IrOperand.Reg(value)));
            }

            var import = new IrImport(function.DllName!, function.EntryPoint ?? function.Name, function.CallingConvention == CallingConvention.Cdecl);
            if (!_irProgram.Imports.Contains(import))
            {
                _irProgram.Imports.Add(import);
            }

            var result = function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(function.ReturnType));
            Add(instructions, new IrInstruction(IrOpCode.SysCall, result, IrOperand.Import(import), IrOperand.Constant(count)));
            return result ?? VoidResult();
        }

        private IrVirtualRegister VoidResult()
        {
            if (_voidResult == null)
            {
                _voidResult = AllocateRegister(4);
            }

            return _voidResult;
        }

        private void EmitPrintArguments(BoundExpression argument) => EmitWriteArguments(argument, newline: true);

        /// <summary>输出参数（newline=false 走 Write* 运行时函数不换行，true 走 Print* 带换行）。</summary>
        private void EmitWriteArguments(BoundExpression argument, bool newline)
        {
            var instructions = _currentFunction.Instructions;
            var type = argument.Type;

            if (type == TypeSymbol.Any && argument is BoundConversionExpression conversion)
            {
                type = conversion.Expression.Type;
            }

            var value = EmitExpression(argument);
            var stringFn = newline ? "PrintString" : "WriteString";
            var intFn = newline ? "PrintInt" : "WriteInt";

            if (type == TypeSymbol.String)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Int32 || type is NamedTypeSymbol { TypeKind: TypeKind.Enum } || type == TypeSymbol.UInt8 ||
                     type == TypeSymbol.Int8 || type == TypeSymbol.Int16 || type == TypeSymbol.UInt16)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(intFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Boolean)
            {
                var text = EmitSelectString("True", "False", value);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Char)
            {
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Float)
            {
                // 6e-M21 Phase 5b：float 打印经单→双精度中转复用 DoubleToString
                var asDouble = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, asDouble, IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(asDouble)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Double)
            {
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.UInt32)
            {
                // u32 零扩展进 8 字节寄存器后按无符号 64 位打印（值域非负，符号解释正确）
                var widened = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Movzx64, widened, IrOperand.Reg(value)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(widened)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.UInt64)
            {
                // u64 打印：UInt64ToString（无符号十进制，支持 >2^63 大值）→ PrintString/WriteString
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("UInt64ToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else if (type == TypeSymbol.Int64)
            {
                // long 打印：Int64ToString（x64 单 64 位参；x86 拆 low/high 两寄存器）→ PrintString/WriteString
                Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                var text = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(text)));
                Add(instructions, new IrInstruction(IrOpCode.Call, null, IrOperand.Runtime(stringFn), IrOperand.Constant(0)));
            }
            else
            {
                throw new Exception($"Native code generation does not support printing values of type '{type}'");
            }
        }

        private IrVirtualRegister EmitSelectString(string trueText, string falseText, IrVirtualRegister condition)
        {
            var instructions = _currentFunction.Instructions;
            var falseLabel = AllocLabel();
            var doneLabel = AllocLabel();
            var result = AllocateRegister(8);

            Add(instructions, new IrInstruction(IrOpCode.Cmp, IrOperand.Reg(condition), IrOperand.Constant(0)));
            Add(instructions, new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), IrOperand.Label(falseLabel)));

            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(EmitStringLiteral(trueText))));
            Add(instructions, new IrInstruction(IrOpCode.Jmp, IrOperand.Label(doneLabel)));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(falseLabel)));
            Add(instructions, new IrInstruction(IrOpCode.Mov, result, IrOperand.Reg(EmitStringLiteral(falseText))));

            Add(instructions, new IrInstruction(IrOpCode.Label, IrOperand.Label(doneLabel)));
            return result;
        }

        private IrVirtualRegister EmitUserCall(BoundCallExpression node)
        {
            return EmitFunctionCall(node.Function, node.Arguments);
        }

        /// <summary>用户函数调用（栈 ABI）：ReserveArgs/StoreArg/Call/FreeArgs（6e-M18 起亦服务静态容器类方法调用）。</summary>
        private IrVirtualRegister EmitFunctionCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
            => EmitInvoke(function, null, arguments);

        /// <summary>
        /// M4：统一调用发射。receiver != null → 实例调用（this 为隐藏 arg0，参数区前置 8 字节）；
        /// indirectFunction != null → CallReg 虚分派（vtable 槽指针）。实参右→左求值（与既有顺序一致）。
        /// </summary>
        private IrVirtualRegister? EmitInvoke(
            FunctionSymbol function,
            IrVirtualRegister? receiver,
            ImmutableArray<BoundExpression> arguments,
            IrVirtualRegister? indirectFunction = null)
        {
            var instructions = _currentFunction.Instructions;
            var hasThis = receiver != null;
            var count = arguments.Length;

            var totalBytes = ParamsTotalBytes(function, count);
            Add(instructions, new IrInstruction(IrOpCode.ReserveArgs, IrOperand.Constant(totalBytes)));

            if (hasThis)
            {
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(0), IrOperand.Reg(receiver!)));
            }

            // 6e-M23 R7：实参改为源顺序（左→右）求值——对齐 C#/Evaluator/IL；out 实参依赖同调用内先写后读的顺序语义
            for (var i = 0; i < count; i++)
            {
                var value = EmitExpression(arguments[i]);
                Add(instructions, new IrInstruction(IrOpCode.StoreArg, IrOperand.Constant(ParamByteOffset(function, i, count)), IrOperand.Reg(value)));
            }

            var result = function.ReturnType == TypeSymbol.Void ? null : AllocateRegister(ReturnSize(function.ReturnType));
            if (indirectFunction != null)
            {
                Add(instructions, new IrInstruction(IrOpCode.CallReg, result, IrOperand.None, IrOperand.Reg(indirectFunction)));
            }
            else
            {
                var irFunction = _functionMap[function];
                Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Func(irFunction), IrOperand.Constant(0)));
            }

            Add(instructions, new IrInstruction(IrOpCode.FreeArgs, IrOperand.Constant(totalBytes)));
            return result ?? VoidResult();
        }

        /// <summary>
        /// 6e-M21 Phase 5：系统化整数转换发射。
        /// 槽内规范表示：无符号窄整型=掩码零扩展值；有符号窄整型=符号扩展后的 32 位值（shl+sar）；
        /// ≤32 位来源转 i32/u32 位模式不变；64 位来源先 Trunc64；
        /// →64 位按源符号性选 Movsx64/Movzx64（char 零扩展、enum 符号扩展，与既有路径一致）。
        /// </summary>
        private bool TryEmitIntegerConversion(BoundConversionExpression node, IrVirtualRegister value, out IrVirtualRegister result)
        {
            result = value;
            var from = node.Expression.Type;
            var to = node.Type;

            if (from.IsPlaceholder128 || to.IsPlaceholder128)
            {
                return false;
            }

            if (!to.IsInteger || to == TypeSymbol.Boolean)
            {
                return false;
            }

            var fromIsIntLike = (from.IsInteger && from != TypeSymbol.Boolean) ||
                                from == TypeSymbol.Char ||
                                from is NamedTypeSymbol { TypeKind: TypeKind.Enum };
            if (!fromIsIntLike || from == TypeSymbol.String)
            {
                return false;
            }

            var instructions = _currentFunction.Instructions;
            var v = value;
            var fromIs64 = from == TypeSymbol.Int64 || from == TypeSymbol.UInt64;

            if (to == TypeSymbol.Int8 || to == TypeSymbol.UInt8 ||
                to == TypeSymbol.Int16 || to == TypeSymbol.UInt16)
            {
                var source = v;
                if (fromIs64)
                {
                    var truncated = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, truncated, IrOperand.Reg(v)));
                    source = truncated;
                }

                switch (to.Name)
                {
                    case "byte":
                    {
                        var r = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(source), IrOperand.Constant(0xFF)));
                        result = r;
                        break;
                    }
                    case "ushort":
                    {
                        var r = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(source), IrOperand.Constant(0xFFFF)));
                        result = r;
                        break;
                    }
                    case "sbyte":
                    {
                        var shifted = AllocateRegister(4);
                        var r = AllocateRegister(4);
                        var count24 = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Const, count24, IrOperand.Constant(24)));
                        Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(source), IrOperand.Reg(count24)));
                        Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(count24)));
                        result = r;
                        break;
                    }
                    default: // short
                    {
                        var shifted = AllocateRegister(4);
                        var r = AllocateRegister(4);
                        var count16 = AllocateRegister(4);
                        Add(instructions, new IrInstruction(IrOpCode.Const, count16, IrOperand.Constant(16)));
                        Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(source), IrOperand.Reg(count16)));
                        Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(count16)));
                        result = r;
                        break;
                    }
                }

                return true;
            }

            if (to == TypeSymbol.Int32 || to == TypeSymbol.UInt32)
            {
                if (fromIs64)
                {
                    var r = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, r, IrOperand.Reg(v)));
                    result = r;
                }

                // ≤32 位来源：位模式即结果
                return true;
            }

            if (to == TypeSymbol.Int64 || to == TypeSymbol.UInt64)
            {
                // 64 位 ↔ 64 位：位模式即结果，免指令
                if (fromIs64)
                {
                    return true;
                }

                // char 无符号零扩展；enum 底层 int 符号扩展（与既有路径一致）
                var zeroExtend = (from.IsInteger && !from.IsSigned) || from == TypeSymbol.Char;
                var r = AllocateRegister(8);
                Add(instructions, new IrInstruction(
                    zeroExtend ? IrOpCode.Movzx64 : IrOpCode.Movsx64,
                    r, IrOperand.Reg(v)));
                result = r;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 6e-M21 Phase 5b：涉及浮点的系统化转换。
        /// 无符号 ≤32 位整数经 Movzx64 零扩展后按 long 转换（值非负语义正确）；
        /// float↔double 用 FCvtSSD/FCvtDS；f32 目标/源全部带 single 标志走 ss 族指令。
        /// </summary>
        private bool TryEmitFloatConversion(BoundConversionExpression node, IrVirtualRegister value, out IrVirtualRegister result)
        {
            result = value;
            var from = node.Expression.Type;
            var to = node.Type;

            if (from.IsPlaceholder128 || to.IsPlaceholder128)
            {
                return false;
            }

            var toIsFloat = to == TypeSymbol.Float || to == TypeSymbol.Double;
            var fromIsFloatType = from == TypeSymbol.Float;
            if (!toIsFloat && !fromIsFloatType)
            {
                return false;
            }

            var singleTarget = to == TypeSymbol.Float;
            if (!(from.IsNumeric && !from.IsPlaceholder128) && from != TypeSymbol.Char && !(from is NamedTypeSymbol { TypeKind: TypeKind.Enum }))
            {
                return false; // 字符串等走既有专用路径
            }

            var instructions = _currentFunction.Instructions;

            // 6e-M21 Phase 5b：float → 整数（cvttss2si 截断；宽整型经 double 中转的 64 位路径）
            if (from == TypeSymbol.Float)
            {
                if (to == TypeSymbol.Double)
                {
                    var widened = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, widened, IrOperand.Reg(value)));
                    result = widened;
                    return true;
                }

                if (to == TypeSymbol.Int32 || to == TypeSymbol.UInt32)
                {
                    var r32 = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD, r32, IrOperand.Reg(value), IrOperand.None, 0, 0, true));
                    result = r32;
                    return true;
                }

                if (to == TypeSymbol.Int64 || to == TypeSymbol.UInt64)
                {
                    var r64 = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD64, r64, IrOperand.Reg(value), IrOperand.None, 0, 0, true));
                    result = r64;
                    return true;
                }

                // 窄整型：先截断到 int32，再按槽内规范表示收窄
                if (to == TypeSymbol.Int8 || to == TypeSymbol.Int16 ||
                    to == TypeSymbol.UInt8 || to == TypeSymbol.UInt16)
                {
                    var truncated = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD, truncated, IrOperand.Reg(value), IrOperand.None, 0, 0, true));

                    switch (to.Name)
                    {
                        case "byte":
                        {
                            var r = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(truncated), IrOperand.Constant(0xFF)));
                            result = r;
                            break;
                        }
                        case "ushort":
                        {
                            var r = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.And, r, IrOperand.Reg(truncated), IrOperand.Constant(0xFFFF)));
                            result = r;
                            break;
                        }
                        case "sbyte":
                        {
                            var shifted = AllocateRegister(4);
                            var r = AllocateRegister(4);
                            var c24 = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Const, c24, IrOperand.Constant(24)));
                            Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(truncated), IrOperand.Reg(c24)));
                            Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(c24)));
                            result = r;
                            break;
                        }
                        default: // short
                        {
                            var shifted = AllocateRegister(4);
                            var r = AllocateRegister(4);
                            var c16 = AllocateRegister(4);
                            Add(instructions, new IrInstruction(IrOpCode.Const, c16, IrOperand.Constant(16)));
                            Add(instructions, new IrInstruction(IrOpCode.Shl, shifted, IrOperand.Reg(truncated), IrOperand.Reg(c16)));
                            Add(instructions, new IrInstruction(IrOpCode.Sar, r, IrOperand.Reg(shifted), IrOperand.Reg(c16)));
                            result = r;
                            break;
                        }
                    }

                    return true;
                }

                return false;
            }

            if (to == TypeSymbol.Double)
            {
                if (from == TypeSymbol.Float)
                {
                    var r = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSSD, r, IrOperand.Reg(value)));
                    result = r;
                    return true;
                }

                if (from == TypeSymbol.Int64 || from == TypeSymbol.UInt64)
                {
                    var r = AllocateRegister(8);
                    if (from == TypeSymbol.UInt64)
                    {
                        // 6e-M21 Phase 7：无符号精确转换（清 MSB + 补偿 2^63），支持 >2^63 大值
                        Add(instructions, new IrInstruction(IrOpCode.FCvtSI64U, r, IrOperand.Reg(value)));
                    }
                    else
                    {
                        Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, r, IrOperand.Reg(value)));
                    }

                    result = r;
                    return true;
                }

                if (from.IsInteger && !from.IsSigned || from == TypeSymbol.Char)
                {
                    // 无符号零扩展后按 long 转（u32 最大值在 double 精度内精确）
                    var wide = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, wide, IrOperand.Reg(value)));
                    var r = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, r, IrOperand.Reg(wide)));
                    result = r;
                    return true;
                }

                // 有符号整数/enum → double
                var signedResult = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI, signedResult, IrOperand.Reg(value)));
                result = signedResult;
                return true;
            }

            // to == Float
            if (from == TypeSymbol.Double)
            {
                var r4 = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.FCvtDS, r4, IrOperand.Reg(value)));
                result = r4;
                return true;
            }

            if (from.IsInteger && !from.IsSigned || from == TypeSymbol.Char)
            {
                var wide = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.Movzx64, wide, IrOperand.Reg(value)));
                if (to == TypeSymbol.Float)
                {
                    // u32 值域非负：零扩展后按无符号 long 路径精确转换到 f32
                    var r4 = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSI64U, r4, IrOperand.Reg(wide), IrOperand.None, 0, 0, true));
                    result = r4;
                    return true;
                }

                var r = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, r, IrOperand.Reg(wide)));
                result = r;
                return true;
            }

            // 有符号整数/enum → float
            var fResult = AllocateRegister(4);
            Add(instructions, new IrInstruction(IrOpCode.FCvtSI, fResult, IrOperand.Reg(value), IrOperand.None, 0, 0, true));
            result = fResult;
            return true;
        }

        private IrVirtualRegister EmitConversionExpression(BoundConversionExpression node)
        {
            var instructions = _currentFunction.Instructions;
            var value = EmitExpression(node.Expression);
            var from = node.Expression.Type;
            var to = node.Type;

            // 6e-M19 M5-a：null 字面量 → 引用型——Const 0 即空引用，直通
            if (from == TypeSymbol.Null)
            {
                return value;
            }

            if (from == TypeSymbol.Any || to == TypeSymbol.Any)
            {
                return value;
            }

            // M4：类/接口引用转换——同一指针表示，上转/下转均为直通（运行时不做类型检查）
            if (from is NamedTypeSymbol { IsValueType: false } && to is NamedTypeSymbol { IsValueType: false })
            {
                return value;
            }

            // 6e-M21 Phase 5：数值↔数值系统化整数转换（命中即返回）
            if (TryEmitIntegerConversion(node, value, out var integerResult))
            {
                return integerResult;
            }

            // 6e-M21 Phase 5b：涉及 float/double 的系统化转换（命中即返回）
            if (TryEmitFloatConversion(node, value, out var floatResult))
            {
                return floatResult;
            }

            if (from == TypeSymbol.Char && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to == TypeSymbol.Char ||
                from is NamedTypeSymbol { TypeKind: TypeKind.Enum } && to == TypeSymbol.Int32 ||
                from == TypeSymbol.Int32 && to is NamedTypeSymbol { TypeKind: TypeKind.Enum } ||
                from == TypeSymbol.UInt8 && to == TypeSymbol.Int32)
            {
                // 同为 4 字节值，无需指令
                return value;
            }

            if (from == TypeSymbol.Double && to == TypeSymbol.Int32)
            {
                var result = AllocateRegister(4);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSD, result, IrOperand.Reg(value)));
                return result;
            }

            if (from == TypeSymbol.Double && to == TypeSymbol.Int64)
            {
                // 截断取整（与 C# 一致）；LeaSlot 保证 x86 帧底缓冲（EmitFCvtSD64 的控制字区）
                var scratch = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.LeaSlot, scratch, IrOperand.Reg(scratch)));
                var result = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSD64, result, IrOperand.Reg(value)));
                return result;
            }

            if (to == TypeSymbol.Int64)
            {
                if (from == TypeSymbol.Int32 || from is NamedTypeSymbol { TypeKind: TypeKind.Enum })
                {
                    // 符号扩展
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movsx64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (from == TypeSymbol.UInt8)
                {
                    // 零扩展（byte 无符号）
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (from == TypeSymbol.Char)
                {
                    // 零扩展（char 无符号，槽内已是零扩展的 32 位值）
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.Movzx64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (from == TypeSymbol.String)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseInt64"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (from == TypeSymbol.Int64)
            {
                if (to == TypeSymbol.Int32)
                {
                    // 低 32 位截断
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (to == TypeSymbol.UInt8)
                {
                    var truncatedLong = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, truncatedLong, IrOperand.Reg(value)));
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(truncatedLong), IrOperand.Constant(0xFF)));
                    return result;
                }

                if (to == TypeSymbol.Char)
                {
                    var truncatedLong = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.Trunc64, truncatedLong, IrOperand.Reg(value)));
                    return truncatedLong;
                }

                if (to == TypeSymbol.Double)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSI64, result, IrOperand.Reg(value)));
                    return result;
                }

                if (to == TypeSymbol.String)
                {
                    var text = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, text, IrOperand.Runtime("Int64ToString"), IrOperand.Constant(0)));
                    return text;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (from == TypeSymbol.Int32 && to == TypeSymbol.Double ||
                from == TypeSymbol.UInt8 && to == TypeSymbol.Double)
            {
                var result = AllocateRegister(8);
                Add(instructions, new IrInstruction(IrOpCode.FCvtSI, result, IrOperand.Reg(value)));
                return result;
            }

            if (to == TypeSymbol.UInt8)
            {
                if (from == TypeSymbol.Double)
                {
                    // 与 C# 语义一致：(byte) 3.9 == 3（先截断到 int 再取低 8 位）
                    var truncated = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.FCvtSD, truncated, IrOperand.Reg(value)));
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(truncated), IrOperand.Constant(0xFF)));
                    return result;
                }

                if (from == TypeSymbol.Int32)
                {
                    // 无符号字节截断，与 C# (byte)300 == 44 语义一致
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.And, result, IrOperand.Reg(value), IrOperand.Constant(0xFF)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (to == TypeSymbol.String)
            {
                if (from == TypeSymbol.Double)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg64, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("DoubleToString"), IrOperand.Constant(0)));
                    return result;
                }

                if (from == TypeSymbol.Int32)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("IntToString"), IrOperand.Constant(0)));
                    return result;
                }

                if (from == TypeSymbol.Char)
                {
                    var result = AllocateRegister(8);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("CharToString"), IrOperand.Constant(0)));
                    return result;
                }

                if (from == TypeSymbol.Boolean)
                {
                    return EmitSelectString("True", "False", value);
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (to == TypeSymbol.Int32)
            {
                if (from == TypeSymbol.String)
                {
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseInt"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            if (to == TypeSymbol.Boolean)
            {
                if (from == TypeSymbol.String)
                {
                    var result = AllocateRegister(4);
                    Add(instructions, new IrInstruction(IrOpCode.SetArg, IrOperand.Constant(0), IrOperand.Reg(value)));
                    Add(instructions, new IrInstruction(IrOpCode.Call, result, IrOperand.Runtime("ParseBool"), IrOperand.Constant(0)));
                    return result;
                }

                throw new Exception($"Unexpected conversion from {from} to {to}");
            }

            throw new Exception($"Unexpected conversion from {from} to {to}");
        }

        // ------------------------------------------------------------------
        // 变量/标签
        // ------------------------------------------------------------------

        private IrVirtualRegister GetVariable(VariableSymbol variable)
        {
            if (_variables.TryGetValue(variable, out var register))
            {
                return register;
            }

            return AllocateRegister(variable, ReturnSize(variable.Type));
        }

        private int GetLabel(BoundLabel label)
        {
            if (!_labels.TryGetValue(label, out var result))
            {
                result = AllocLabel();
                _labels.Add(label, result);
            }

            return result;
        }

        private int AllocLabel() => _nextLabelId++;
    }
}