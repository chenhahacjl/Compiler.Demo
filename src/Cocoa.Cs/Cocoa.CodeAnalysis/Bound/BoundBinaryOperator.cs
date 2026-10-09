using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定二元操作符（HIR 净化）：运算符对象只携带语义 <see cref="BoundBinaryOperatorKind"/>，
    /// 不携带 <see cref="SyntaxKind"/>。前端（Binder / BoundNodeFactory / 插值拼接 / for 增量桥）经
    /// <see cref="Bind(SyntaxKind, TypeSymbol, TypeSymbol)"/> 兼容门把词法 token 翻译为语义 kind。
    /// </summary>
    public sealed class BoundBinaryOperator
    {
        private BoundBinaryOperator(BoundBinaryOperatorKind kind, TypeSymbol type)
            : this(kind, type, type, type)
        {
        }

        private BoundBinaryOperator(BoundBinaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType)
            : this(kind, operandType, operandType, resultType)
        {
        }

        private BoundBinaryOperator(BoundBinaryOperatorKind kind, TypeSymbol leftType, TypeSymbol rightType, TypeSymbol resultType)
        {
            Kind = kind;
            LeftType = leftType;
            RightType = rightType;
            ResultType = resultType;
        }

        public BoundBinaryOperatorKind Kind { get; }
        public TypeSymbol LeftType { get; }
        public TypeSymbol RightType { get; }
        public TypeSymbol ResultType { get; }

        /// <summary>用户定义运算符方法（`function operator +` 的 <c>op_Addition</c>）——内建命中时为 null。
        /// 非空时发射层须改走静态调用（参数 = 左后右），不产内建算术指令。</summary>
        public FunctionSymbol? UserDefinedMethod { get; private init; }

        /// <summary>是否走用户定义运算符路径（发射层分派判据，等价于 <see cref="UserDefinedMethod"/> 非空）。</summary>
        public bool IsUserDefined => UserDefinedMethod != null;

        /// <summary>
        /// 内建运算符语义符号（对齐 Roslyn <c>CommonCreateBuiltinOperator</c>，语义视图）：
        /// 经 <see cref="Binding.BuiltInOperators"/> 按签名合成 `op_*` 方法符号，供反射/诊断/重载解析
        /// 候选比较；无用户对应运算符的 kind（引用相等/?? 等）返回 null。发射仍按 <see cref="Kind"/> 直分派。
        /// </summary>
        public FunctionSymbol? BuiltinOperatorSymbol =>
            UserDefinedMethod == null
                ? Binding.BuiltInOperators.CreateOperatorSymbol(
                    new Binding.BinaryOperatorSignature(Kind, LeftType, RightType, ResultType))
                : null;

        /// <summary>
        /// 构造用户定义运算符绑定：<paramref name="kind"/> 取词法 token 翻译出的内建语义 kind，
        /// 仅供诊断/打印保留运算符字面；发射层以 <see cref="UserDefinedMethod"/> 为准。
        /// </summary>
        public static BoundBinaryOperator ForUserDefined(
            BoundBinaryOperatorKind kind,
            TypeSymbol leftType,
            TypeSymbol rightType,
            TypeSymbol resultType,
            FunctionSymbol method)
        {
            return new BoundBinaryOperator(kind, leftType, rightType, resultType) { UserDefinedMethod = method };
        }

        /// <summary>查询内建二元签名（委托 <see cref="BuiltInOperators"/> 规则表），命中构造对应对象返回。</summary>
        private static BoundBinaryOperator? LookupBuiltIn(BoundBinaryOperatorKind kind, TypeSymbol leftType, TypeSymbol rightType)
        {
            var signature = Binding.BuiltInOperators.GetSignature(kind, leftType, rightType);
            if (signature == null)
            {
                return null;
            }

            return new BoundBinaryOperator(signature.Value.Kind, signature.Value.LeftType, signature.Value.RightType, signature.Value.ResultType);
        }

        /// <summary>6e-M19 M5-a：可空引用型（类/接口/string/数组/any）——null 比较与引用转换的合法目标。
        /// 6f：函数值类型同属引用语义（事件后备/委托字段 == / != null）。</summary>
        private static bool IsNullableReference(TypeSymbol type)
        {
            return !type.IsValueType && (type is NamedTypeSymbol || type is FunctionTypeSymbol ||
                type == TypeSymbol.String || type == TypeSymbol.Any || type.ElementType != null);
        }

        /// <summary>兼容词法门（HIR 净化）：token → 语义 kind 翻译后委托语义入口。</summary>
        public static BoundBinaryOperator? Bind(SyntaxKind syntaxKind, TypeSymbol leftType, TypeSymbol rightType)
        {
            return Bind(Translate(syntaxKind), leftType, rightType);
        }

        /// <summary>语义主入口：按 <see cref="BoundBinaryOperatorKind"/> 绑定，动态合成分支按 Equals/NotEquals 判定。</summary>
        public static BoundBinaryOperator? Bind(BoundBinaryOperatorKind kind, TypeSymbol leftType, TypeSymbol rightType)
        {
            if (leftType is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && leftType == rightType)
            {
                if (kind == BoundBinaryOperatorKind.Equals)
                    return new BoundBinaryOperator(BoundBinaryOperatorKind.Equals, enumType, TypeSymbol.Boolean);
                if (kind == BoundBinaryOperatorKind.NotEquals)
                    return new BoundBinaryOperator(BoundBinaryOperatorKind.NotEquals, enumType, TypeSymbol.Boolean);
            }

            // 6e-M22 委托真实类型化：具名 delegate 二元运算——
            // `+`/`-` = Delegate.Combine/Remove（调用列表组合，结果同委托类型）；`==`/`!=` = 调用列表相等。
            if (leftType is NamedTypeSymbol { TypeKind: TypeKind.Delegate } leftDelegate &&
                rightType is NamedTypeSymbol { TypeKind: TypeKind.Delegate } rightDelegate &&
                (leftDelegate == rightDelegate || leftDelegate.FullName == rightDelegate.FullName))
            {
                if (kind == BoundBinaryOperatorKind.Addition || kind == BoundBinaryOperatorKind.Subtraction)
                {
                    return new BoundBinaryOperator(kind, leftDelegate, rightDelegate, leftDelegate);
                }

                if (kind == BoundBinaryOperatorKind.Equals)
                    return new BoundBinaryOperator(BoundBinaryOperatorKind.ReferenceEquals, leftDelegate, rightDelegate, TypeSymbol.Boolean);
                if (kind == BoundBinaryOperatorKind.NotEquals)
                    return new BoundBinaryOperator(BoundBinaryOperatorKind.ReferenceNotEquals, leftDelegate, rightDelegate, TypeSymbol.Boolean);
            }

            // 6e-M19 M2-c：类类型 == / != → 引用相等（动态合成，仿 enum 先例）。
            // 条件：双侧均为类（含 System.Object/接口/外部类），且存在继承关系（同型或一侧可隐式转换到另一侧）。
            // string/值类型/any 走既有值比较表，不受影响。
            if (leftType is NamedTypeSymbol { IsValueType: false } leftClass && rightType is NamedTypeSymbol { IsValueType: false } rightClass &&
                leftType != TypeSymbol.String && rightType != TypeSymbol.String &&
                (leftClass == rightClass || leftClass.IsBaseOf(rightClass) || rightClass.IsBaseOf(leftClass)))
            {
                var referenceKind = kind switch
                {
                    BoundBinaryOperatorKind.Equals => BoundBinaryOperatorKind.ReferenceEquals,
                    BoundBinaryOperatorKind.NotEquals => BoundBinaryOperatorKind.ReferenceNotEquals,
                    _ => (BoundBinaryOperatorKind?)null,
                };

                if (referenceKind != null)
                {
                    return new BoundBinaryOperator(referenceKind.Value, leftClass, rightClass, TypeSymbol.Boolean);
                }
            }

            // 6e-M22 C5+ 多播事件：函数值 == / != → 引用相等（-= 按引用移除首个匹配订阅者）。
            // FunctionTypeSymbol 工厂缓存 ⇒ 结构同形即同一实例，符号引用比较即可判同形；发射层复用既有 ReferenceEquals 三后端路径。
            if (leftType is FunctionTypeSymbol leftFn && rightType is FunctionTypeSymbol rightFn && leftFn == rightFn)
            {
                var functionValueKind = kind switch
                {
                    BoundBinaryOperatorKind.Equals => BoundBinaryOperatorKind.ReferenceEquals,
                    BoundBinaryOperatorKind.NotEquals => BoundBinaryOperatorKind.ReferenceNotEquals,
                    _ => (BoundBinaryOperatorKind?)null,
                };

                if (functionValueKind != null)
                {
                    return new BoundBinaryOperator(functionValueKind.Value, leftFn, rightFn, TypeSymbol.Boolean);
                }
            }

            // 6e-M19 M5-a：null 字面量与可空引用型（类/接口/string/数组/any）== / != → 引用相等。
            // 不经值语义路径（string 值比较/native StrEquals 对单侧 null 会解引用崩溃），指针比较三后端天然一致。
            if (kind == BoundBinaryOperatorKind.Equals || kind == BoundBinaryOperatorKind.NotEquals)
            {
                var referenceKind = kind == BoundBinaryOperatorKind.Equals
                    ? BoundBinaryOperatorKind.ReferenceEquals
                    : BoundBinaryOperatorKind.ReferenceNotEquals;

                if (leftType == TypeSymbol.Null && IsNullableReference(rightType))
                {
                    return new BoundBinaryOperator(referenceKind, leftType, rightType, TypeSymbol.Boolean);
                }

                if (rightType == TypeSymbol.Null && IsNullableReference(leftType))
                {
                    return new BoundBinaryOperator(referenceKind, leftType, rightType, TypeSymbol.Boolean);
                }
            }

            // ?? null 合并：左操作数必须为引用类型，右操作数类型与左相同，结果类型 = 左类型
            if (kind == BoundBinaryOperatorKind.NullCoalescing)
            {
                if (IsNullableReference(leftType) && leftType == rightType)
                {
                    return new BoundBinaryOperator(BoundBinaryOperatorKind.NullCoalescing, leftType, rightType, leftType);
                }
            }

            return LookupBuiltIn(kind, leftType, rightType);
        }

        /// <summary>词法 token → 语义二元 kind（HIR 净化翻译门，供 <see cref="Bind(SyntaxKind, TypeSymbol, TypeSymbol)"/>；
        /// 用户定义运算符回落亦复用它保留运算符字面）。</summary>
        public static BoundBinaryOperatorKind Translate(SyntaxKind syntaxKind)
        {
            return syntaxKind switch
            {
                SyntaxKind.AmpersandToken => BoundBinaryOperatorKind.BitwiseAnd,
                SyntaxKind.AmpersandAmpersandToken => BoundBinaryOperatorKind.LogicalAnd,
                SyntaxKind.PipeToken => BoundBinaryOperatorKind.BitwiseOr,
                SyntaxKind.PipePipeToken => BoundBinaryOperatorKind.LogicalOr,
                SyntaxKind.QuestionQuestionToken => BoundBinaryOperatorKind.NullCoalescing,
                SyntaxKind.HatToken => BoundBinaryOperatorKind.BitwiseXor,
                SyntaxKind.EqualsEqualsToken => BoundBinaryOperatorKind.Equals,
                SyntaxKind.BangEqualsToken => BoundBinaryOperatorKind.NotEquals,
                SyntaxKind.PlusToken => BoundBinaryOperatorKind.Addition,
                SyntaxKind.MinusToken => BoundBinaryOperatorKind.Subtraction,
                SyntaxKind.StarToken => BoundBinaryOperatorKind.Multiplication,
                SyntaxKind.SlashToken => BoundBinaryOperatorKind.Division,
                SyntaxKind.PercentToken => BoundBinaryOperatorKind.Modulo,
                SyntaxKind.ShiftLeftToken => BoundBinaryOperatorKind.ShiftLeft,
                SyntaxKind.ShiftRightToken => BoundBinaryOperatorKind.ShiftRight,
                SyntaxKind.LessToken => BoundBinaryOperatorKind.Less,
                SyntaxKind.LessOrEqualsToken => BoundBinaryOperatorKind.LessOrEquals,
                SyntaxKind.GreaterToken => BoundBinaryOperatorKind.Greater,
                SyntaxKind.GreaterOrEqualsToken => BoundBinaryOperatorKind.GreaterOrEquals,
                _ => throw new NotSupportedException($"Unsupported binary operator token '{syntaxKind}'"),
            };
        }
    }
}