using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Cocoa.Syntax;
using CSyntax = global::Cocoa.CodeAnalysis.CSharp.Syntax;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    /// <summary>
    /// out/ref 参数语法层测试（6e-M23 R1）：双方言声明拼写、调用点 byref 实参、lambda 拒绝、无修饰符回归。
    /// </summary>
    public class ParameterSyntaxTests
    {
        // ------------------------------------------------------------------
        // 声明位（.co）
        // ------------------------------------------------------------------

        [Fact]
        public void Co_Parameter_OutModifier_Parses()
        {
            var tree = SyntaxTree.Parse("function TryParse(s: string, out value: i32): bool { return true }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)tree.Root).Members));
            Assert.Null(function.Parameters[0].Modifier);
            Assert.False(function.Parameters[0].IsByRef);

            var modified = function.Parameters[1];
            Assert.NotNull(modified.Modifier);
            Assert.Equal(SyntaxKind.OutKeyword, modified.Modifier!.Kind);
            Assert.True(modified.IsByRef);
            Assert.Equal("value", modified.Identifier.Text);
            Assert.Equal("i32", modified.Type.Identifier.Text);
        }

        [Fact]
        public void Co_Parameter_RefModifier_Parses()
        {
            var tree = SyntaxTree.Parse("function Swap(ref a: i32, ref b: i32): void { }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)tree.Root).Members));
            Assert.All(function.Parameters, p =>
            {
                Assert.NotNull(p.Modifier);
                Assert.Equal(SyntaxKind.RefKeyword, p.Modifier!.Kind);
                Assert.True(p.IsByRef);
            });
        }

        [Fact]
        public void Co_Parameter_Plain_StaysUnmodified()
        {
            var tree = SyntaxTree.Parse("function Add(a: int, b: int): int { return a + b }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)tree.Root).Members));
            Assert.All(function.Parameters, p => Assert.Null(p.Modifier));
        }

        [Fact]
        public void Co_CSharpStyleForm_StillRejected()
        {
            var tree = SyntaxTree.Parse("function F(int x) { }");
            Assert.Contains(tree.Diagnostics, d => d.IsError);
        }

        // ------------------------------------------------------------------
        // 声明位（.cs）
        // ------------------------------------------------------------------

        [Fact]
        public void Cs_Parameter_OutModifier_Parses()
        {
            var tree = SyntaxTree.ParseCs("bool TryParse(string s, out int value) { return true; }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<CSyntax.FunctionDeclarationSyntax>(Assert.Single(((CSyntax.CompilationUnitSyntax)tree.Root).Members));
            var modified = function.Parameters[1];
            Assert.NotNull(modified.Modifier);
            Assert.Equal(SyntaxKind.OutKeyword, modified.Modifier!.Kind);
            Assert.True(modified.IsByRef);
            Assert.Equal("value", modified.Identifier.Text);
        }

        [Fact]
        public void Cs_Parameter_RefModifier_Parses()
        {
            var tree = SyntaxTree.ParseCs("void Swap(ref int[] a) { }");            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var function = Assert.IsType<CSyntax.FunctionDeclarationSyntax>(Assert.Single(((CSyntax.CompilationUnitSyntax)tree.Root).Members));
            Assert.NotNull(function.Parameters[0].Modifier);
            Assert.Equal(SyntaxKind.RefKeyword, function.Parameters[0].Modifier!.Kind);
        }

        // ------------------------------------------------------------------
        // 调用点 byref 实参（out n / ref arr[i]）
        // ------------------------------------------------------------------

        [Fact]
        public void Co_CallSite_OutArgument_Parses()
        {
            var tree = SyntaxTree.Parse("let ok = Int32.TryParse(\"42\", out n)");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var byRef = FindFirst<ByRefArgumentExpressionSyntax>(tree.Root)!;
            Assert.Equal(SyntaxKind.OutKeyword, byRef.Keyword.Kind);
            Assert.False(byRef.IsRef);
            Assert.Equal(CocoaSyntaxKind.NameExpression, byRef.Expression.Kind);
        }

        [Fact]
        public void Cs_CallSite_RefElementArgument_Parses()
        {
            var tree = SyntaxTree.ParseCs("void M(int[] a) { N(ref a[0]); }");
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));

            var byRef = FindFirst<CSyntax.ByRefArgumentExpressionSyntax>(tree.Root)!;
            Assert.Equal(SyntaxKind.RefKeyword, byRef.Keyword.Kind);
            Assert.True(byRef.IsRef);
            Assert.Equal(CSharpSyntaxKind.ElementAccessExpression, byRef.Expression.Kind);
        }

        // ------------------------------------------------------------------
        // 边界
        // ------------------------------------------------------------------

        [Fact]
        public void LambdaParameter_Modifier_Rejected()
        {
            var tree = SyntaxTree.Parse("let f = (out x: i32) => x");
            Assert.Contains(tree.Diagnostics, d => d.IsError && d.Message.Contains("out/ref"));
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static T? FindFirst<T>(SyntaxNode node) where T : SyntaxNode
        {
            if (node is T match)
            {
                return match;
            }

            foreach (var child in node.GetChildren())
            {
                var found = FindFirst<T>(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
