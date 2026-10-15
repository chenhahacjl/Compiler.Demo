using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Binding;
using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// 绑定一元操作符（HIR 净化）：运算符对象只携带语义 <see cref="BoundUnaryOperatorKind"/>，
    /// 不携带 <see cref="SyntaxKind"/>。前端（Binder / BoundNodeFactory / 插值拼接）经
    /// <see cref="Bind(SyntaxKind, TypeSymbol)"/> 兼容门把词法 token 翻译为语义 kind。
    /// </summary>
    public sealed class BoundUnaryOperator
    {
        private BoundUnaryOperator(BoundUnaryOperatorKind kind, TypeSymbol operandType)
            : this(kind, operandType, operandType)
        {
        }

        private BoundUnaryOperator(BoundUnaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType)
        {
            Kind = kind;
            OperandType = operandType;
            ResultType = resultType;
        }

        public BoundUnaryOperatorKind Kind { get; }
        public TypeSymbol OperandType { get; }
        public TypeSymbol ResultType { get; }

        /// <summary>用户定义运算符方法（`function operator -` 的 <c>op_UnaryNegation</c>）——内建命中时为 null。
        /// 非空时发射层须改走静态调用（单参 = 操作数），不产内建算术指令。</summary>
        public FunctionSymbol? UserDefinedMethod { get; private init; }

        /// <summary>是否走用户定义运算符路径（发射层分派判据）。</summary>
        public bool IsUserDefined => UserDefinedMethod != null;

        /// <summary>内建一元运算符语义符号（语义视图，供反射/诊断/重载解析；发射仍按 kind 直分派）。</summary>
        public FunctionSymbol? BuiltinOperatorSymbol =>
            UserDefinedMethod == null
                ? Binding.BuiltInOperators.CreateOperatorSymbol(
                    new Binding.UnaryOperatorSignature(Kind, OperandType, ResultType))
                : null;

        /// <summary>
        /// 构造用户定义运算符绑定：<paramref name="kind"/> 取词法 token 翻译出的内建语义 kind，
        /// 仅供诊断/打印保留运算符字面；发射层以 <see cref="UserDefinedMethod"/> 为准。
        /// </summary>
        public static BoundUnaryOperator ForUserDefined(
            BoundUnaryOperatorKind kind,
            TypeSymbol operandType,
            TypeSymbol resultType,
            FunctionSymbol method)
        {
            return new BoundUnaryOperator(kind, operandType, resultType) { UserDefinedMethod = method };
        }

        private static BoundUnaryOperator? LookupBuiltIn(BoundUnaryOperatorKind kind, TypeSymbol operandType)
        {
            var signature = Binding.BuiltInOperators.GetSignature(kind, operandType);
            if (signature == null)
            {
                return null;
            }

            return new BoundUnaryOperator(signature.Value.Kind, signature.Value.OperandType, signature.Value.ResultType);
        }

        /// <summary>兼容词法门（HIR 净化）：token → 语义 kind 翻译后委托语义入口。</summary>
        public static BoundUnaryOperator? Bind(SyntaxKind syntaxKind, TypeSymbol operandType)
        {
            return Bind(Translate(syntaxKind), operandType);
        }

        /// <summary>语义主入口：按 <see cref="BoundUnaryOperatorKind"/> 绑定。</summary>
        public static BoundUnaryOperator? Bind(BoundUnaryOperatorKind kind, TypeSymbol operandType)
        {
            return LookupBuiltIn(kind, operandType);
        }

        /// <summary>词法 token → 语义一元 kind（HIR 净化翻译门，供 <see cref="Bind(SyntaxKind, TypeSymbol)"/>；
        /// 用户定义运算符回落亦复用它保留运算符字面）。</summary>
        public static BoundUnaryOperatorKind Translate(SyntaxKind syntaxKind)
        {
            return syntaxKind switch
            {
                SyntaxKind.PlusToken => BoundUnaryOperatorKind.Identity,
                SyntaxKind.MinusToken => BoundUnaryOperatorKind.Negation,
                SyntaxKind.BangToken => BoundUnaryOperatorKind.LogicalNegation,
                SyntaxKind.TildeToken => BoundUnaryOperatorKind.OnesComplement,
                _ => throw new NotSupportedException($"Unsupported unary operator token '{syntaxKind}'"),
            };
        }
    }
}