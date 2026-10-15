using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Contracts;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Contracts
{
    /// <summary>
    /// 函数值语义契约（3.1）：函数值表示、接收者、调用列表规则。
    /// 契约断言：<see cref="FunctionValueContract"/> 的环境承载/接收者语义/可调用判定。
    /// </summary>
    public class FunctionValueContractTests
    {
        [Fact]
        public void CarriesEnvironment_OnlyForLambdaWithEnvironment()
        {
            var lambdaWithEnv = new FunctionSymbol("lam", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Int32, null)
            {
                IsLambda = true,
                IsLambdaWithEnvironment = true,
            };
            Assert.True(FunctionValueContract.CarriesEnvironment(lambdaWithEnv));
            Assert.True(FunctionValueContract.ReceiverIsEnvironment(lambdaWithEnv));

            var plainFunction = new FunctionSymbol("f", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Int32, null);
            Assert.False(FunctionValueContract.CarriesEnvironment(plainFunction));
            Assert.False(FunctionValueContract.ReceiverIsEnvironment(plainFunction));
        }

        [Fact]
        public void IsCallable_TrueForAnyFunction()
        {
            var function = new FunctionSymbol("f", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Int32, null);
            Assert.True(FunctionValueContract.IsCallable(function));
        }
    }
}
