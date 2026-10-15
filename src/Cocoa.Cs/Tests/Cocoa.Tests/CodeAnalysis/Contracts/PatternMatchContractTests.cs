using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Contracts;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Contracts
{
    /// <summary>
    /// 模式匹配语义契约（3.1）：is/as/声明模式的静态折叠与动态判定规则。
    /// 契约断言：<see cref="PatternMatchContract"/> 的装箱形态/运行时类型信息/可折叠规则；
    /// 行为断言：值类型目标经装箱形态判定（Evaluator boxed 判定 / IL isinst+unbox.any 对齐）。
    /// </summary>
    public class PatternMatchContractTests
    {
        [Fact]
        public void RequiresBoxedForm_TrueForValueTypes()
        {
            Assert.True(PatternMatchContract.RequiresBoxedForm(TypeSymbol.Int32));
            Assert.True(PatternMatchContract.RequiresBoxedForm(TypeSymbol.Boolean));
            Assert.False(PatternMatchContract.RequiresBoxedForm(TypeSymbol.String));
            Assert.False(PatternMatchContract.RequiresBoxedForm(NamedTypeSymbol.SystemObject));
        }

        [Fact]
        public void NeedsRuntimeTypeInfo_TrueForValueTypesAndString()
        {
            Assert.True(PatternMatchContract.NeedsRuntimeTypeInfo(TypeSymbol.Int32));
            Assert.True(PatternMatchContract.NeedsRuntimeTypeInfo(TypeSymbol.String));
            Assert.False(PatternMatchContract.NeedsRuntimeTypeInfo(NamedTypeSymbol.SystemObject));
        }

        [Fact]
        public void CanFold_TrueWhenConstantValuePresent()
        {
            var literal = new BoundLiteralExpression(SyntaxTree.Parse("42").Root, 42, TypeSymbol.Int32);
            Assert.True(PatternMatchContract.CanFold(literal));

            var variable = new BoundVariableExpression(
                SyntaxTree.Parse("x").Root,
                new ParameterSymbol("x", TypeSymbol.Int32, 0));
            Assert.False(PatternMatchContract.CanFold(variable));
        }
    }
}
