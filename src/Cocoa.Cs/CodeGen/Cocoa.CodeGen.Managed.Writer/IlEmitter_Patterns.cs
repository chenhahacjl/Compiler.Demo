using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.Metadata;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
namespace Cocoa.CodeGen.Managed.Writer
{
    /// <summary>
    /// 模式匹配/类型测试表达式 IL 发射（自 IlEmitter.Statements 拆出：is/as/declaration/relational/logical/property 模式）。
    /// </summary>
    internal sealed partial class IlEmitter
    {
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
                il.Emit(IlOpCodeTable.Get("Isinst"), IsInstTypeToken(node.TargetType));
                il.Emit(IlOpCodeTable.Get("Brfalse"), notMatch);

                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Unbox_Any"), IsInstTypeToken(node.TargetType));
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)slot);

                il.Emit(IlOpCodeTable.Get("Ldc_I4_1"));
                il.Emit(IlOpCodeTable.Get("Br"), endValue);
                il.Emit(notMatch);
                il.Emit(IlOpCodeTable.Get("Ldc_I4_0"));
                il.Emit(endValue);
                return;
            }

            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Isinst"), IsInstTypeToken(node.TargetType));
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
    }
}
