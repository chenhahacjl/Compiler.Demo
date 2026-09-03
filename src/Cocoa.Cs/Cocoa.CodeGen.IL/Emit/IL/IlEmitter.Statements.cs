using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.IL
{
    /// <summary>
    /// IL 路径发射器：绑定树 → 自研 IL 组件（IlAssembler/MetadataBuilder/ManagedPEWriter）。
    /// 发射语义与原 Mono.Cecil 实现一致（表达式/语句 → IL 指令序列）。
    /// </summary>
    internal sealed partial class IlEmitter
    {
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

            // 6e-M22 C5-c：捕获变量声明 → 初始化值写入环境字段（目标先入栈，对齐 stfld [obj, value] 语义；
            // 原实现"值在目标之前"→ 栈为 [value, obj]，CLR 误把 env 当值、int 当对象 → NRE）
            if (node.Variable.IsCaptured && _closureEnvLocalIndex.HasValue)
            {
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                EmitExpression(il, node.Initializer);
                il.Emit(IlOpCodeTable.Get("Stfld"), _closureFieldDefs![node.Variable.Name]);
                return;
            }

            EmitExpression(il, node.Initializer);

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
                NamedTypeSymbol { TypeKind: TypeKind.Delegate } dc => dc.DelegateSignature()!,
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
        /// 数组元素 → 数组 + 索引 + ldelema（CLR 自带越界检查）。字符串元素不可作 byref 目标（绑定层拒绝非数组元素访问）。
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

            // 6e-M22 C5-c：捕获变量写环境字段（目标先入栈 + 值 = [env, v]，与 stfld 语义一致；
            // 用临时局部保表达式结果——原实现缺值入栈致 [env] 欠栈 InvalidProgram/NRE）
            if (node.Variable.IsCaptured && _closureEnvLocalIndex.HasValue)
            {
                var temporaryLocal = AllocateTemporaryLocal(node);
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)_closureEnvLocalIndex.Value);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                il.Emit(IlOpCodeTable.Get("Stfld"), _closureFieldDefs![node.Variable.Name]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
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
                throw new System.Exception($"Unexpected unary operator {BoundOperatorText.UnaryGlyph(node.Op.Kind)}({node.Operand.Type})");
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
                    throw new System.Exception($"Unexpected binary operator {BoundOperatorText.BinaryGlyph(node.Op.Kind)}({node.Left.Type}, {node.Right.Type})");
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

    }
}
