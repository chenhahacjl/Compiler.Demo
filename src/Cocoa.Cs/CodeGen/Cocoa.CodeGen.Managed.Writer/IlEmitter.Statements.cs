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


namespace Cocoa.CodeGen.Managed.Writer
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
                case BoundNodeKind.CheckedStatement:
                    EmitCheckedStatement(il, (BoundCheckedStatement)node);
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

            // script 顶层变量声明 → 静态字段（IlEmit 全局变量持久）
            if (_globalVariableFields.TryGetValue(node.Variable, out var globalField))
            {
                EmitExpression(il, node.Initializer);
                il.Emit(IlOpCodeTable.Get("Stsfld"), globalField);
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
            if (_protectedBlockDepth > 0)
            {
                // 保护区内 return：值存暂存局部 → leave 方法出口（CLR 经 finally 链），出口再 ldloc+ret。
                // 直接 ret 会绕过 finally 且违反 EH 校验（InvalidProgramException）。
                if (node.Expression != null)
                {
                    EmitExpression(il, node.Expression);
                    var boxedName = _currentMethodReturnType == TypeSymbol.Any ? BoxedTypeName(node.Expression.Type) : null;
                    if (boxedName != null)
                    {
                        il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType(boxedName));
                    }

                    if (!_returnExitHasValue)
                    {
                        _returnExitHasValue = true;
                        _returnExitTemp = AllocateSyntheticTemporaryLocal("return-exit", node.Expression.Type);
                        _returnExitTarget = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
                    }

                    il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)_returnExitTemp);
                }
                else if (_returnExitTarget == null)
                {
                    _returnExitTarget = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
                }

                il.Emit(IlOpCodeTable.Get("Leave"), _returnExitTarget);
                return;
            }

            if (node.Expression != null)
            {
                EmitExpression(il, node.Expression);

                // any/object 返回方法：值类型返回显式 box（$eval 等 script 顶层表达式；
                // Binder 把顶层表达式转 ReturnStatement 时表达类型保持 int，但方法签名返回 any）
                if (_currentMethodReturnType == TypeSymbol.Any &&
                    IsValueTypeSymbol(node.Expression.Type) &&
                    BoxedTypeName(node.Expression.Type) != null)
                {
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType(BoxedTypeName(node.Expression.Type)!));
                }
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

            _protectedBlockDepth++;
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
            _protectedBlockDepth--;
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

        /// <summary>6e-M22 委托真实类型化：delegate 目标位函数值构造——`newobj &lt;delegateType&gt;::.ctor(object, IntPtr)`。</summary>
        private void EmitDelegateValueConstruction(IlAssembler il, BoundFunctionValueExpression node, NamedTypeSymbol delegateClass)
        {
            if (node.EnvironmentClass != null)
            {
                // 捕获闭包：target = 当前环境对象
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
            il.Emit(IlOpCodeTable.Get("Newobj"), _delegateCtors[delegateClass]);
        }

        /// <summary>间接调用（6e-M22 C4-b/D-B）：callee + args → callvirt Func\`N/委托类::Invoke。</summary>
        private void EmitInvocationExpression(IlAssembler il, BoundInvocationExpression node)
        {
            // 6e-M22 委托真实类型化：callee 为具名 delegate 类型 → callvirt 该类型 TypeDef::Invoke
            if (node.Callee.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateClass &&
                _delegateInvokes.TryGetValue(delegateClass, out var delegateInvoke))
            {
                EmitExpression(il, node.Callee);
                foreach (var argument in node.Arguments)
                {
                    EmitExpression(il, argument);
                }

                il.Emit(IlOpCodeTable.Get("Callvirt"), delegateInvoke);
                return;
            }

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
            // 常量局部（let 常量传播）的 VariableExpression 带 ConstantValue，但应发射局部读取
            //（local 已存储常量值），而非按常量表达式发射——排除后走 EmitVariableExpression。
            if (node.ConstantValue != null && node.Kind != BoundNodeKind.VariableExpression)
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
                case BoundNodeKind.ConditionalAccessExpression:
                    EmitConditionalAccessExpression(il, (BoundConditionalAccessExpression)node);
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
                case BoundNodeKind.TypeOperatorExpression:
                    EmitTypeOperatorExpression(il, (BoundTypeOperatorExpression)node);
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
                case BoundNodeKind.RelationalPattern:
                    EmitRelationalPattern(il, (BoundRelationalPattern)node);
                    break;
                case BoundNodeKind.LogicalPattern:
                    EmitLogicalPattern(il, (BoundLogicalPattern)node);
                    break;
                case BoundNodeKind.PropertyPattern:
                    EmitPropertyPattern(il, (BoundPropertyPattern)node);
                    break;
                case BoundNodeKind.DeclarationPattern:
                    EmitDeclarationPattern(il, (BoundDeclarationPattern)node);
                    break;
                default:
                    throw new System.Exception($"Unexpected node kind {node.Kind}");
            }
        }

        /// <summary>
        /// 声明模式 <c>e is T v</c>（C# 规范模式）：<c>isinst T</c> 后把结果存入模式变量，
        /// 再以「非 null」为测试结果——
        /// <code>
        ///   isinst T; stloc v; ldloc v; brtrue L_true; ldc.i4.0; br L_end; L_true: ldc.i4.1; L_end:
        /// </code>
        /// 赋值与测试合为一体，故测试通过时 v 必然已赋值，then 分支读取安全。
        ///
        /// **仅支持引用型目标**：值型目标需 <c>unbox.any</c> 且要求元数据层登记装箱类型 TypeRef，
        /// 而 isinst 对基元类型本就非法（isinst 只接受 class/valuetype 令牌）。
        /// 该形态当前报明确诊断，不静默错编。
        /// </summary>
        /// <summary>
        /// 值类型在 <c>isinst</c> / <c>unbox.any</c> 处需要的**装箱形态**类型操作数。
        ///
        /// 关键点：<c>ToIlType(Int32)</c> 返回的是基元 <see cref="IlType"/>（<c>IlType.Int32</c>），
        /// 它不是 TypeRef，<c>PatchTokens</c> 查不到对应 token 会抛 KeyNotFoundException。
        /// 而 <c>isinst</c>/<c>unbox.any</c> 的 InlineType 操作数必须是 TypeRef/TypeDef/TypeSpec，
        /// 所以这里要像装箱那样走 <c>_framework.RequireType("System.Int32")</c> 拿到装箱类型的 TypeRef。
        ///
        /// 参照装箱路径 <c>IlEmitter.Expressions.cs</c> 的同名映射（System.Int64 / System.SByte …）。
        /// </summary>
        private IlType ToBoxedIlType(TypeSymbol type)
        {
            if (!type.IsValueType)
            {
                return ToIlType(type);
            }

            var name = type == TypeSymbol.Boolean ? "System.Boolean"
                : type == TypeSymbol.Int8 ? "System.SByte"
                : type == TypeSymbol.Int16 ? "System.Int16"
                : type == TypeSymbol.Int32 ? "System.Int32"
                : type == TypeSymbol.Int64 ? "System.Int64"
                : type == TypeSymbol.UInt8 ? "System.Byte"
                : type == TypeSymbol.UInt16 ? "System.UInt16"
                : type == TypeSymbol.UInt32 ? "System.UInt32"
                : type == TypeSymbol.UInt64 ? "System.UInt64"
                : type == TypeSymbol.Char ? "System.Char"
                : type == TypeSymbol.Float ? "System.Single"
                : type == TypeSymbol.Double ? "System.Double"
                : null;
            if (name != null)
            {
                return IlType.Class(_framework.RequireType(name));
            }

            // 用户自定义的 struct（值类型 class）：走与普通命名类型相同的解析。
            if (type is NamedTypeSymbol named)
            {
                return IlType.Class(ResolveExternalTypeRef(named));
            }

            throw new System.Exception(
                $"声明模式的值类型目标 '{type.Name}' 没有对应的装箱类型名，无法为 isinst/unbox.any 生成 TypeRef。");
        }

        private void EmitDeclarationPattern(IlAssembler il, BoundDeclarationPattern node)
        {
            if (!_locals.TryGetValue(node.Variable, out var slot))
            {
                throw new System.Exception(
                    $"声明模式变量 '{node.Variable.Name}' 未登记局部槽位（CollectPatternLocals 未覆盖该表达式位置）。");
            }

            if (node.TargetType.IsValueType)
            {
                // 值类型目标：`o is int n` 不能走 isinst+castclass（castclass 对值类型非法）。
                // 正确形态（与 Roslyn 一致，isinst 对值类型**是**合法的，返回装箱实例或 null）：
                //   <operand>            -> object
                //   isinst  T             -> 装箱 T 或 null
                //   brfalse.s notMatch
                //   <operand>            -> 重新压入原对象
                //   unbox.any T          -> 拆箱出值类型本体
                //   stloc  slot
                //   ldc.i4.1 / br end / notMatch: ldc.i4.0 / end:
                // 必须重载原对象再 unbox：isinst 留下的是**装箱引用**，直接 unbox.any 它
                // 得到的是装箱后的值而非拆箱结果。
                var notMatch = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
                var endValue = new IlInstruction(IlOpCodeTable.Get("Nop"), null);

                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Isinst"), ToBoxedIlType(node.TargetType));
                il.Emit(IlOpCodeTable.Get("Brfalse"), notMatch);

                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Unbox_Any"), ToBoxedIlType(node.TargetType));
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)slot);

                il.Emit(IlOpCodeTable.Get("Ldc_I4_1"));
                il.Emit(IlOpCodeTable.Get("Br"), endValue);
                il.Emit(notMatch);
                il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                il.Emit(endValue);
                return;
            }

            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Isinst"), ToIlType(node.TargetType));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)slot);

            var elseLabel = new IlInstruction(IlOpCodeTable.Get("Nop"), null);
            var endLabel = new IlInstruction(IlOpCodeTable.Get("Nop"), null);

            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)slot);
            il.Emit(IlOpCodeTable.Get("Brtrue"), elseLabel);
            il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
            il.Emit(IlOpCodeTable.Get("Br"), endLabel);
            il.Emit(elseLabel);
            il.Emit(IlOpCodeTable.Get("Ldc_I4_1"));
            il.Emit(endLabel);
        }

        /// <summary>
        /// <c>checked { … }</c> / <c>unchecked { … }</c>：块体本身照常发射，只切换整数算术的溢出模式。
        /// 保存并恢复外层状态，故 <c>checked { unchecked { … } }</c> 的内层回绕语义正确。
        /// </summary>
        private void EmitCheckedStatement(IlAssembler il, BoundCheckedStatement node)
        {
            var previous = _checkedArithmetic;
            _checkedArithmetic = node.IsChecked;
            try
            {
                EmitStatement(il, node.Body);
            }
            finally
            {
                _checkedArithmetic = previous;
            }
        }

        /// <summary>
        /// 关系模式 <c>e is &gt; 0</c>：求值两侧后按 <see cref="BoundRelationalPattern.OperatorKind"/> 发比较指令。
        /// 语义与 Evaluator 的 <c>CompareValues</c> 对齐（数值按值比较，null 侧为 false）。
        /// </summary>
        private void EmitRelationalPattern(IlAssembler il, BoundRelationalPattern node)
        {
            EmitExpression(il, node.Expression);
            EmitExpression(il, node.Value);

            // 严格比较直接发指令；含等比较走「取反」：a >= b ≡ !(a < b)，a <= b ≡ !(a > b)
            // （Cgt/Clt 只给 0/1，含等需在两者之间取补，CEq 直解需复制双侧，栈序列易错）
            var strict = node.OperatorKind switch
            {
                BoundBinaryOperatorKind.Greater => "Cgt",
                BoundBinaryOperatorKind.Less => "Clt",
                BoundBinaryOperatorKind.GreaterOrEquals => "Clt",
                BoundBinaryOperatorKind.LessOrEquals => "Cgt",
                _ => "Cgt",
            };

            if (node.OperatorKind is BoundBinaryOperatorKind.Greater or BoundBinaryOperatorKind.Less)
            {
                il.Emit(IlOpCodeTable.Get(strict));
                return;
            }

            // 两侧入临时局部，再取反
            var right = AllocateTemporaryLocal(node, TypeSymbol.Int32);
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)right);

            var left = AllocateTemporaryLocal(node, TypeSymbol.Int32);
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)left);

            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)left);
            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)right);
            il.Emit(IlOpCodeTable.Get(strict));
            il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
            il.Emit(IlOpCodeTable.Get("Ceq"));
        }

        /// <summary>
        /// 逻辑模式 <c>and</c>/<c>or</c>/<c>not</c>：转成等价布尔表达式后复用既有短路发射路径
        /// （与 Evaluator 的非短路求值结果一致，短路仅为可观察行为上的严格改进）。
        /// </summary>
        private void EmitLogicalPattern(IlAssembler il, BoundLogicalPattern node)
        {
            if (node.IsUnary)
            {
                EmitExpression(il, node.Operand!);
                il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                il.Emit(IlOpCodeTable.Get("Ceq"));
                return;
            }

            var kind = node.OperatorKind == BoundLogicalPatternKind.Or
                ? BoundBinaryOperatorKind.LogicalOr
                : BoundBinaryOperatorKind.LogicalAnd;

            var op = BoundBinaryOperator.Bind(kind, TypeSymbol.Boolean, TypeSymbol.Boolean)
                     ?? throw new System.Exception($"内部错误：{kind} 的 bool×bool 运算未注册");

            EmitBinaryExpression(il, new BoundBinaryExpression(node.Syntax, node.Left!, op, node.Right!));
        }

        /// <summary>
        /// 属性模式 <c>e is { X: &gt; 0 }</c>：按接收者**静态类型**解析成员（字段优先，其次属性 getter），
        /// 逐子模式求值后以短路 <c>&amp;&amp;</c> 合取。接收者为 <c>any</c> 时无静态成员信息——
        /// 动态成员查找目前仅 Evaluator 后端支持（见 <c>EvaluatePropertyPattern</c>）。
        /// </summary>
        private void EmitPropertyPattern(IlAssembler il, BoundPropertyPattern node)
        {
            if (node.Subpatterns.Length == 0)
            {
                il.Emit(IlOpCodeTable.Get("Ldc_I4_1"));
                return;
            }

            var andOp = BoundBinaryOperator.Bind(BoundBinaryOperatorKind.LogicalAnd, TypeSymbol.Boolean, TypeSymbol.Boolean)
                        ?? throw new System.Exception("内部错误：LogicalAnd 的 bool×bool 运算未注册");

            BoundExpression? combined = null;

            foreach (var sub in node.Subpatterns)
            {
                var memberType = ResolvePropertyPatternMemberType(node.Expression.Type, sub.PropertyName);
                if (memberType == null)
                {
                    throw new System.Exception(
                        $"属性模式：类型 '{node.Expression.Type.Name}' 无成员 '{sub.PropertyName}'（或接收者为 any，动态成员查找仅 Evaluator 后端支持）。");
                }

                // 子模式以「成员访问」为操作数求值：常量子模式退化为等值比较
                var test = sub.Pattern switch
                {
                    BoundRelationalPattern relational => BuildPatternAgainstMember(node, relational, memberType),
                    BoundBinaryExpression binary => new BoundBinaryExpression(
                        node.Syntax,
                        BuildMemberAccess(node, sub.PropertyName, memberType),
                        binary.Op,
                        binary.Right),
                    _ => new BoundBinaryExpression(
                        node.Syntax,
                        BuildMemberAccess(node, sub.PropertyName, memberType),
                        BoundBinaryOperator.Bind(BoundBinaryOperatorKind.Equals, memberType, sub.Pattern.Type) ??
                            throw new System.Exception($"属性模式子模式：{sub.Pattern.Type.Name} 无等值运算"),
                        sub.Pattern),
                };

                combined = combined == null
                    ? test
                    : new BoundBinaryExpression(node.Syntax, combined, andOp, test);
            }

            EmitExpression(il, combined!);
        }

        private BoundExpression BuildPatternAgainstMember(BoundPropertyPattern node, BoundRelationalPattern relational, TypeSymbol memberType)
        {
            var op = BoundBinaryOperator.Bind(relational.OperatorKind, memberType, relational.Value.Type)
                     ?? throw new System.Exception(
                         $"属性模式子模式：{memberType.Name} 不支持 {relational.OperatorKind}");

            return new BoundBinaryExpression(
                node.Syntax,
                BuildMemberAccess(node, relational.Syntax != null ? node.Subpatterns[0].PropertyName : node.Subpatterns[0].PropertyName, memberType),
                op,
                relational.Value);
        }

        private BoundExpression BuildMemberAccess(BoundPropertyPattern node, string name, TypeSymbol memberType)
        {
            return new BoundMemberAccessExpression(
                node.Syntax!, memberType, node.Expression, name,
                (node.Expression.Type as NamedTypeSymbol)?.GetField(name));
        }

        private static TypeSymbol? ResolvePropertyPatternMemberType(TypeSymbol receiver, string name)
        {
            if (receiver is not NamedTypeSymbol named)
            {
                return null;
            }

            return named.GetField(name)?.Type ?? named.GetProperty(name)?.Type;
        }

        /// <summary>6e-M19 M5-b：is → isinst + ldnull + cgt.un（C# 规范模式：非 null 引用 &gt; null）。</summary>
        private void EmitIsExpression(IlAssembler il, BoundIsExpression node)
        {
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Isinst"), IsInstTypeToken(node.TargetType));
            il.Emit(IlOpCodeTable.Get("Ldnull"));
            il.Emit(IlOpCodeTable.Get("Cgt_Un"));
        }

        /// <summary>6e-M19 M5-b：as → isinst（失败栈上即 null，与 C# 语义一致）。</summary>
        /// <summary>
        /// <c>typeof(T)</c> → <c>ldtoken T; call System.Type::GetTypeFromHandle(RuntimeTypeHandle)</c>。
        /// <c>sizeof(T)</c> → <c>sizeof T</c>（基元/枚举已在绑定期折叠为常量，走不到这里）。
        /// </summary>
        private void EmitTypeOperatorExpression(IlAssembler il, BoundTypeOperatorExpression node)
        {
            if (!node.IsTypeOf)
            {
                il.Emit(IlOpCodeTable.Get("Sizeof"), ToIlType(node.TypeArgument));
                return;
            }

            // ldtoken 的操作数是**元数据 token**：基元/字符串没有 TypeDef，须借框架 TypeRef
            // （与 IsInstTypeToken 同一处理——ldtoken 的操作数语义与 isinst 一致，都是类型 token）
            il.Emit(IlOpCodeTable.Get("Ldtoken"), IsInstTypeToken(node.TypeArgument));
            il.Emit(IlOpCodeTable.Get("Call"), _framework.TypeGetTypeFromHandle);
        }

        private void EmitAsExpression(IlAssembler il, BoundAsExpression node)
        {
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Isinst"), IsInstTypeToken(node.TargetType));
        }

        /// <summary>isinst 目标 token：值类型/string 用 corlib 装箱 TypeRef（System.Int32/System.String 等，
        /// 非内联元素类型）；其余引用类型用 ToIlType。</summary>
        private object IsInstTypeToken(TypeSymbol type)
        {
            if (type == TypeSymbol.String)
            {
                return _framework.StringType;
            }

            var boxed = BoxedTypeName(type);
            return boxed != null ? _framework.RequireType(boxed) : ToIlType(type);
        }

        /// <summary>
        /// decimal/half（非 IL 基元）转换：经 System.Decimal/System.Half 静态 op_* 调用（C# 语义：
        /// decimal→整数 截断取整、decimal→浮点 保精度；op_Explicit 重载按返回类型消歧）。
        /// </summary>
        private void EmitDecimalHalfConversion(IlAssembler il, TypeSymbol from, TypeSymbol to)
        {
            if (to == TypeSymbol.Decimal)
            {
                // 整数/char → decimal：op_Implicit(源类型)；float/double → decimal：op_Explicit(源类型)
                var sourceName = BoxedTypeName(from) ?? throw new System.Exception($"Unexpected decimal source '{from}'");
                var opName = from.IsFloat ? "op_Explicit" : "op_Implicit";
                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Decimal", opName, new[] { sourceName }));
                return;
            }

            if (from == TypeSymbol.Decimal)
            {
                if (to == TypeSymbol.String)
                {
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Decimal"));
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    return;
                }

                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Decimal", "op_Explicit", new[] { "System.Decimal" }, returnTypeName: BoxedTypeName(to)));
                return;
            }

            // half：float/double → half = op_Explicit(源)；half → float/double = op_Explicit(Half) 按返回消歧；half → string = box + Convert.ToString
            if (from == TypeSymbol.Float16 || to == TypeSymbol.Float16)
            {
                EmitHalfConversion(il, from, to);
                return;
            }

            throw new System.Exception($"Unexpected decimal/half conversion ({from} → {to})");
        }

        /// <summary>half 转换：System.Half::op_Explicit 双向（float/double→half 按源参数唯一；half→float/double 按返回类型消歧）。</summary>
        private void EmitHalfConversion(IlAssembler il, TypeSymbol from, TypeSymbol to)
        {
            if (from == TypeSymbol.Float16)
            {
                if (to == TypeSymbol.Float || to == TypeSymbol.Double)
                {
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Half", "op_Explicit", new[] { "System.Half" }, returnTypeName: to == TypeSymbol.Float ? "System.Single" : "System.Double"));
                    return;
                }

                if (to == TypeSymbol.String)
                {
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Half"));
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    return;
                }

                throw new System.Exception($"Unexpected half conversion target '{to}'");
            }

            if (to == TypeSymbol.Float16 && (from == TypeSymbol.Float || from == TypeSymbol.Double))
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Half", "op_Explicit", new[] { from == TypeSymbol.Float ? "System.Single" : "System.Double" }));
                return;
            }

            throw new System.Exception($"Unexpected half conversion ({from} → {to})");
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
            else if (node.Type == TypeSymbol.Decimal)
            {
                // decimal 无 IL 直接量：按 (lo, mid, hi, isNegative, scale) 五元组 newobj
                var bits = decimal.GetBits((decimal)node.ConstantValue.Value);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), bits[0]);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), bits[1]);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), bits[2]);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), (bits[3] & int.MinValue) != 0 ? 1 : 0);
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), (bits[3] >> 16) & 0xFF);
                il.Emit(IlOpCodeTable.Get("Newobj"), _framework.RequireMethod("System.Decimal", ".ctor", new[] { "System.Int32", "System.Int32", "System.Int32", "System.Boolean", "System.Byte" }));
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
            else if (_globalVariableFields.TryGetValue(node.Variable, out var globalField))
            {
                // script 顶层变量读静态字段（IlEmit 全局变量持久）
                il.Emit(IlOpCodeTable.Get("Ldsfld"), globalField);
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
                    else if (_globalVariableFields.TryGetValue(variable.Variable, out var globalField))
                    {
                        // script 顶层变量 byref 取址 → ldsflda
                        il.Emit(IlOpCodeTable.Get("Ldsflda"), globalField);
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
                _ when type == TypeSymbol.NativeInt32 || type == TypeSymbol.NativeUInt32 => "Ldind_I",
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
                _ when type == TypeSymbol.NativeInt32 || type == TypeSymbol.NativeUInt32 => "Stind_I",
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

            // 普通（非 byref）形参赋值：形参不在 _locals 里，需发 starg。
            // 此前无此分支，会落到末尾的 _locals[node.Variable] 抛 KeyNotFoundException。
            // 真实触发例：src/Cocoa.Co/.../CodeGen/IlDriver.co 的 `usTexts = ug2`
            // （形参重绑定，C# 语义合法且常见）。
            if (node.Variable is ParameterSymbol plainParameter)
            {
                var parameterIndex = plainParameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                EmitExpression(il, node.Expression);
                // 赋值表达式要留下值（与末尾局部变量路径的 Dup + Stloc 语义一致），
                // 否则语句层的 pop 会把 starg 已弹掉的值再弹一次 → 栈下溢 → InvalidProgram。
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Starg"), (ushort)parameterIndex);
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

            // script 顶层变量写静态字段（IlEmit 全局变量持久）
            if (_globalVariableFields.TryGetValue(node.Variable, out var globalField))
            {
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Stsfld"), globalField);
                return;
            }

            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)_locals[node.Variable]);
        }

        private void EmitUnaryExpression(IlAssembler il, BoundUnaryExpression node)
        {
            // 用户定义一元运算符重载（`function operator -` / `operator !` / `operator ~`）：静态调用
            if (node.Op.IsUserDefined)
            {
                EmitUserDefinedOperatorCall(il, node.Op.UserDefinedMethod!, node.Operand);
                return;
            }

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

        /// <summary>
        /// 用户定义运算符方法的 IL 发射：按参数顺序压栈后 <c>call</c>。
        /// 目标优先本编译单元 <see cref="_methods"/> 的 MethodDef，回退 cod 库的 MemberRef。
        /// 运算符方法恒 static，故无 this/接收者与 struct 取址处理。
        /// </summary>
        private void EmitUserDefinedOperatorCall(IlAssembler il, FunctionSymbol method, params BoundExpression[] arguments)
        {
            foreach (var argument in arguments)
            {
                EmitExpression(il, argument);
            }

            if (_methods.TryGetValue(method, out var methodDefinition))
            {
                il.Emit(IlOpCodeTable.Get("Call"), methodDefinition);
                return;
            }

            if (_codAssemblies.TryGetValue(method, out var codAssembly))
            {
                il.Emit(IlOpCodeTable.Get("Call"), CodMethodRef(method, codAssembly));
                return;
            }

            throw new System.Exception($"运算符方法 '{method.ContainingClass?.Name}.{method.Name}' 无发射目标（未登记 MethodDef 且非 cod 库成员）。");
        }

        private void EmitBinaryExpression(IlAssembler il, BoundBinaryExpression node)
        {
            // 用户定义运算符重载（`function operator +`）：改走静态调用，不产内建算术指令
            if (node.Op.IsUserDefined)
            {
                EmitUserDefinedOperatorCall(il, node.Op.UserDefinedMethod!, node.Left, node.Right);
                return;
            }

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

            // 6e-M22 委托真实类型化：具名 delegate 二元运算——
            // `+` = Delegate.Combine(a,b) castclass Handler；`-` = Delegate.Remove(a,b) castclass；
            // `==`/`!=` = Object.Equals（virtual 分派至 Delegate.Equals override → 调用列表相等）
            if (node.Left.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } leftDel &&
                node.Right.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } && leftDel.FullName == ((NamedTypeSymbol)node.Right.Type).FullName)
            {
                switch (node.Op.Kind)
                {
                    case BoundBinaryOperatorKind.Addition:
                        il.Emit(IlOpCodeTable.Get("Call"), _framework.DelegateCombine);
                        il.Emit(IlOpCodeTable.Get("Castclass"), ToIlType(node.Type));
                        return;
                    case BoundBinaryOperatorKind.Subtraction:
                        il.Emit(IlOpCodeTable.Get("Call"), _framework.DelegateRemove);
                        il.Emit(IlOpCodeTable.Get("Castclass"), ToIlType(node.Type));
                        return;
                    case BoundBinaryOperatorKind.ReferenceEquals:
                        il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectEquals);
                        return;
                    case BoundBinaryOperatorKind.ReferenceNotEquals:
                        il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectEquals);
                        il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                        il.Emit(IlOpCodeTable.Get("Ceq"));
                        return;
                }
            }

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

            // decimal（128 位高精度）：运算/比较经 System.Decimal 静态 op_* 调用（IL 无 decimal 基元算术指令）
            if (node.Left.Type == TypeSymbol.Decimal && node.Right.Type == TypeSymbol.Decimal)
            {
                var opName = DecimalOperatorName(node.Op.Kind);
                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Decimal", opName, new[] { "System.Decimal", "System.Decimal" }));
                return;
            }

            // 6e-M21 Phase 4：无符号整数走 _un 变体（浮点保持有符号比较指令）
            var isUnsigned = node.Type.IsInteger && !node.Type.IsSigned && !node.Type.IsPlaceholder128;

            // checked 上下文：整数算术改用溢出检查变体（add.ovf/sub.ovf/mul.ovf，溢出抛 OverflowException）。
            // 浮点无 ovf 变体，仍用无检查指令。
            var useOvf = _checkedArithmetic && !node.Left.Type.IsFloat && !node.Right.Type.IsFloat;

            switch (node.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    il.Emit(IlOpCodeTable.Get(useOvf ? "Add_Ovf" : "Add"));
                    break;
                case BoundBinaryOperatorKind.Subtraction:
                    il.Emit(IlOpCodeTable.Get(useOvf ? "Sub_Ovf" : "Sub"));
                    break;
                case BoundBinaryOperatorKind.Multiplication:
                    il.Emit(IlOpCodeTable.Get(useOvf ? "Mul_Ovf" : "Mul"));
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

        /// <summary>decimal 静态运算符方法名（System.Decimal::op_*，C# decimal 语义签名固定 (Decimal, Decimal)）。</summary>
        private static string DecimalOperatorName(BoundBinaryOperatorKind kind) => kind switch
        {
            BoundBinaryOperatorKind.Addition => "op_Addition",
            BoundBinaryOperatorKind.Subtraction => "op_Subtraction",
            BoundBinaryOperatorKind.Multiplication => "op_Multiply",
            BoundBinaryOperatorKind.Division => "op_Division",
            BoundBinaryOperatorKind.Modulo => "op_Modulus",
            BoundBinaryOperatorKind.Equals => "op_Equality",
            BoundBinaryOperatorKind.NotEquals => "op_Inequality",
            BoundBinaryOperatorKind.Less => "op_LessThan",
            BoundBinaryOperatorKind.LessOrEquals => "op_LessThanOrEqual",
            BoundBinaryOperatorKind.Greater => "op_GreaterThan",
            BoundBinaryOperatorKind.GreaterOrEquals => "op_GreaterThanOrEqual",
            _ => throw new System.Exception($"Unexpected decimal operator {kind}"),
        };

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
