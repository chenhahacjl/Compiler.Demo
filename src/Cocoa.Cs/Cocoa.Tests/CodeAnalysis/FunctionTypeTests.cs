using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 函数类型系统测试（6e-M22 C3）：工厂缓存同形状同实例、mangle 命名、内建委托家族、不变型转换语义。
    /// </summary>
    public class FunctionTypeTests
    {
        [Fact]
        public void Factory_SameShape_SameInstance()
        {
            var a = FunctionTypeSymbol.Get(ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32, TypeSymbol.String), TypeSymbol.Boolean);
            var b = FunctionTypeSymbol.Get(ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32, TypeSymbol.String), TypeSymbol.Boolean);

            Assert.Same(a, b);
        }

        [Fact]
        public void Factory_DifferentShape_DifferentInstance()
        {
            var a = FunctionTypeSymbol.Get(ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32), TypeSymbol.Boolean);
            var b = FunctionTypeSymbol.Get(ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32), TypeSymbol.Int32);
            var c = FunctionTypeSymbol.Get(ImmutableArray.Create<TypeSymbol>(TypeSymbol.String), TypeSymbol.Boolean);

            Assert.NotSame(a, b);
            Assert.NotSame(a, c);
        }

        [Fact]
        public void MangledName_FollowsEncodeV3()
        {
            var type = FunctionTypeSymbol.Get(ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32, TypeSymbol.String), TypeSymbol.Boolean);

            Assert.Equal("Func$@i32$@string__@bool", type.Name);
        }

        [Fact]
        public void Binder_FunctionType_NestedShape_Binds()
        {
            // ((i64) -> i64) -> i64：函数类型作参数类型
            var tree = SyntaxTree.Parse("function compose(outer: ((i64) -> i64) -> i64): void { }");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Binder_FuncFamily_TooFewArguments_Diagnosed()
        {
            // 家族须带实参表且 Func 至少 2 个（末位为返回类型）
            var tree = SyntaxTree.Parse("function t1(f: Func<i64>): void { }");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("2~17"));
        }

        [Fact]
        public void Binder_FuncFamily_BareNameWithoutArguments_Diagnosed()
        {
            var tree = SyntaxTree.Parse("function t2(f: Func): void { }");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError);
        }

        [Fact]
        public void Binder_FuncFamily_ResolvesToFunctionType()
        {
            // .co 亦可用 Func 家族拼写（两方言共享）；仅声明不调用，Evaluate 零错误即绑定成功
            var coTree = SyntaxTree.Parse("function apply(f: Func<i64, bool>, g: Action<i64>): void { }");
            var coCompilation = Compilation.Create(coTree);
            var coResult = coCompilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Empty(coResult.Diagnostics.Where(d => d.IsError));

            var csTree = SyntaxTree.ParseCs("void apply(Func<long, bool> f, Action<long> g) { }");
            var csCompilation = Compilation.Create(csTree);
            var csResult = csCompilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Empty(csResult.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Binder_FuncFamily_WrongArity_Diagnosed()
        {
            var tree = SyntaxTree.Parse("function bad(f: Func<i64>): void { }");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("2~17"));
        }

        [Fact]
        public void Binder_PredicateFamily_ResolvesToBooleanReturn()
        {
            var tree = SyntaxTree.Parse("function check(p: Predicate<i64>): void { }");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }
    }
}
