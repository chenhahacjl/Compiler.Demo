using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 内建运算符符号化语义视图（阶段 2，对齐 Roslyn CommonCreateBuiltinOperator）：
    /// BuiltInOperators 合成 `op_*` 方法符号（CLR 命名），BoundBinaryOperator.BuiltinOperatorSymbol 提供视图；
    /// 发射仍走 kind 直分派（三后端零改动）。
    /// </summary>
    public class BuiltinOperatorSymbolTests
    {
        [Fact]
        public void Binary_Add_I32_OperatorSymbol_UsesClrName()
        {
            var op = BoundBinaryOperator.Bind(BoundBinaryOperatorKind.Addition, TypeSymbol.Int32, TypeSymbol.Int32);
            Assert.NotNull(op);

            var symbol = op!.BuiltinOperatorSymbol;
            Assert.NotNull(symbol);
            Assert.Equal("op_Addition", symbol!.Name);
            Assert.Equal(2, symbol.Parameters.Length);
            Assert.Equal(TypeSymbol.Int32, symbol.Parameters[0].Type);
            Assert.Equal(TypeSymbol.Int32, symbol.Parameters[1].Type);
            Assert.Equal(TypeSymbol.Int32, symbol.ReturnType);
        }

        [Fact]
        public void Binary_Equality_String_OperatorSymbol_ReturnsBoolean()
        {
            var op = BoundBinaryOperator.Bind(BoundBinaryOperatorKind.Equals, TypeSymbol.String, TypeSymbol.String);
            Assert.NotNull(op);

            var symbol = op!.BuiltinOperatorSymbol;
            Assert.NotNull(symbol);
            Assert.Equal("op_Equality", symbol!.Name);
            Assert.Equal(TypeSymbol.Boolean, symbol.ReturnType);
        }

        [Fact]
        public void Unary_Negation_Double_OperatorSymbol_UnaryPlus_Negation()
        {
            var op = BoundUnaryOperator.Bind(BoundUnaryOperatorKind.Negation, TypeSymbol.Double);
            Assert.NotNull(op);

            var symbol = op!.BuiltinOperatorSymbol;
            Assert.NotNull(symbol);
            Assert.Equal("op_UnaryNegation", symbol!.Name);
            Assert.Single(symbol.Parameters);
            Assert.Equal(TypeSymbol.Double, symbol.Parameters[0].Type);
            Assert.Equal(TypeSymbol.Double, symbol.ReturnType);
        }

        [Fact]
        public void ReferenceEquals_HasNoUserDeclaredOperator_ReturnsNull()
        {
            // ReferenceEquals 无用户可声明对应运算符 → 符号视图为 null（合成工厂返回 null）
            var sig = new BinaryOperatorSignature(BoundBinaryOperatorKind.ReferenceEquals, TypeSymbol.String, TypeSymbol.String, TypeSymbol.Boolean);
            Assert.Null(BuiltInOperators.CreateOperatorSymbol(sig));
        }

        [Fact]
        public void CachedSymbols_AreSharedPerSignature()
        {
            var sig = new BinaryOperatorSignature(BoundBinaryOperatorKind.Addition, TypeSymbol.Int32, TypeSymbol.Int32, TypeSymbol.Int32);
            var a = BuiltInOperators.CreateOperatorSymbol(sig);
            var b = BuiltInOperators.CreateOperatorSymbol(sig);
            Assert.Same(a, b);
        }

        [Fact]
        public void GetSimpleBuiltInOperators_KindsAdvertiseCandidates()
        {
            // 重载解析候选集源：Addition 应至少覆盖 int32/double/string/decimal
            var candidates = BuiltInOperators.GetSimpleBuiltInOperators(BoundBinaryOperatorKind.Addition);
            Assert.Contains(candidates, s => s.LeftType == TypeSymbol.Int32 && s.RightType == TypeSymbol.Int32);
            Assert.Contains(candidates, s => s.LeftType == TypeSymbol.Double && s.RightType == TypeSymbol.Double);
            Assert.Contains(candidates, s => s.LeftType == TypeSymbol.String && s.RightType == TypeSymbol.String);
            Assert.Contains(candidates, s => s.LeftType == TypeSymbol.Decimal && s.RightType == TypeSymbol.Decimal);
        }

        [Fact]
        public void GetSignature_MatchesBindResolvedType()
        {
            var sig = BuiltInOperators.GetSignature(BoundBinaryOperatorKind.Less, TypeSymbol.Int32, TypeSymbol.Int32);
            Assert.NotNull(sig);
            Assert.Equal(TypeSymbol.Boolean, sig!.Value.ResultType);
        }
    }
}