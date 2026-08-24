using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    /// <summary>
    /// Lambda / 函数类型语法层测试（6e-M22 C2）：双方言解析形态、括号歧义消解、方言拒绝、Binder 门禁诊断。
    /// </summary>
    public class LambdaSyntaxTests
    {
        // ------------------------------------------------------------------
        // 函数类型（仅 .co）
        // ------------------------------------------------------------------

        [Fact]
        public void Co_FunctionType_ParsesInParameter()
        {
            var tree = SyntaxTree.Parse("function apply(f: (i64) -> i64, x: int): int { return x }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
            var parameter = function.Parameters[0];
            var functionType = Assert.IsType<FunctionTypeSyntax>(parameter.Type);

            Assert.Single(functionType.ParameterTypes);
            Assert.Equal("i64", functionType.ReturnType.Identifier.Text);
        }

        [Fact]
        public void Co_FunctionType_ParsesMultipleParametersAndNesting()
        {
            var tree = SyntaxTree.Parse("function compose(f: ((i64) -> i64) -> int, g: (string, bool) -> f64): void { }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Co_FunctionType_ParsesAsReturnType()
        {
            var tree = SyntaxTree.Parse("function maker(): (int) -> string { }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
            Assert.IsType<FunctionTypeSyntax>(function.Type);
        }

        [Fact]
        public void Co_FunctionType_EmptyParameters_Parses()
        {
            var tree = SyntaxTree.Parse("function run(action: () -> void): void { }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Cs_FunctionTypeArrow_Rejected()
        {
            // .cs 无箭头函数类型（Func 家族 C3 接入）：`->` 拼写产生语法诊断
            var tree = SyntaxTree.ParseCs("void apply((int) -> int f) { }");
            Assert.Contains(tree.Diagnostics, d => d.IsError);
        }

        // ------------------------------------------------------------------
        // Lambda
        // ------------------------------------------------------------------

        [Fact]
        public void Co_Lambda_ExplicitParameter_Parses()
        {
            var tree = SyntaxTree.Parse("let add = (x: i64, y: i64) => x + y");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var lambda = FindLambda(tree.Root)!;
            Assert.Equal(2, lambda.Parameters.Count);
            Assert.True(lambda.HasExplicitParameterTypes);
            Assert.NotNull(lambda.OpenParenthesisToken);
            Assert.Equal(SyntaxKind.BinaryExpression, lambda.Body.Kind);
        }

        [Fact]
        public void Co_Lambda_NoParameters_Parses()
        {
            var tree = SyntaxTree.Parse("let log = () => 1");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var lambda = FindLambda(tree.Root)!;
            Assert.Empty(lambda.Parameters);
        }

        [Fact]
        public void Co_Lambda_BlockBody_Parses()
        {
            var tree = SyntaxTree.Parse("let square = (x: int) => { return x * x }");

            var lambda = FindLambda(tree.Root)!;
            Assert.Equal(SyntaxKind.BlockStatement, lambda.Body.Kind);
        }

        [Fact]
        public void Co_Lambda_ImplicitParameter_Diagnosed()
        {
            var tree = SyntaxTree.Parse("let f = (x) => x");
            Assert.Contains(tree.Diagnostics, d => d.IsError && d.Message.Contains("显式标注类型"));
        }

        [Fact]
        public void Cs_Lambda_Parenless_Parses()
        {
            var tree = SyntaxTree.ParseCs("var f = x => x + 1;");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var lambda = FindLambda(tree.Root)!;
            Assert.Null(lambda.OpenParenthesisToken);
            Assert.Single(lambda.Parameters);
        }

        [Fact]
        public void Cs_Lambda_ImplicitParameters_Parses()
        {
            var tree = SyntaxTree.ParseCs("var add = (x, y) => x + y;");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var lambda = FindLambda(tree.Root)!;
            Assert.Equal(2, lambda.Parameters.Count);
            Assert.False(lambda.HasExplicitParameterTypes);
        }

        [Fact]
        public void Cs_Lambda_MixedExplicitImplicit_Diagnosed()
        {
            var tree = SyntaxTree.ParseCs("var f = (int x, y) => x;");
            Assert.Contains(tree.Diagnostics, d => d.IsError && d.Message.Contains("不可混用"));
        }

        [Fact]
        public void ParenthesizedExpression_StillParses_AsBefore()
        {
            // 歧义消解回归：`(x)` / `(1 + 2)` 仍为括号表达式，不误判 lambda
            var tree = SyntaxTree.Parse("let y = (1 + 2) * 3");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));
            Assert.Null(FindLambda(tree.Root));
        }

        // ------------------------------------------------------------------
        // Binder 门禁（C3/C4 接入前的明确诊断）
        // ------------------------------------------------------------------

        [Fact]
        public void Binder_Lambda_BindsCleanly()
        {
            // C4：lambda 提升——仅赋值不调用，Evaluate 零诊断
            var tree = SyntaxTree.Parse("let f: (i64) -> i64 = (x: i64) => x + 1");
            var compilation = Compilation.CreateScript(null, tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Binder_FunctionType_BindsCleanly()
        {
            // C3：函数类型符号层接入——仅声明不调用，绑定零诊断
            var tree = SyntaxTree.Parse("function apply(f: (i64) -> i64): void { }");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static LambdaExpressionSyntax? FindLambda(SyntaxNode node)
        {
            if (node is LambdaExpressionSyntax lambda)
            {
                return lambda;
            }

            foreach (var child in node.GetChildren())
            {
                var found = FindLambda(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
