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
    internal sealed partial class BoundTreeToIr
    {
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
                    if (receiverType is NamedTypeSymbol { IsValueType: false } userClass && receiverType != TypeSymbol.String && !userClass.IsFacadeClass)
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

                    if (receiverType is NamedTypeSymbol { IsValueType: false } cls && receiverType != TypeSymbol.String && !cls.IsFacadeClass)
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

                    if (receiverType is NamedTypeSymbol { IsValueType: false } hcls && receiverType != TypeSymbol.String && !hcls.IsFacadeClass)
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

                    if (receiverType is NamedTypeSymbol { IsValueType: false } ecl && receiverType != TypeSymbol.String && !ecl.IsFacadeClass)
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

    }
}
