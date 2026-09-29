using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    /// <summary>
    /// 运算符重载 / 转换运算符语法层测试：
    /// `operator +` / `implicit operator T` / `explicit operator T` 的声明形态、
    /// 关键字与运算符 token 落位、方言拒绝。
    /// </summary>
    public class OperatorSyntaxTests
    {
        private static FunctionDeclarationSyntax ParseSingleFunction(string source)
        {
            var tree = SyntaxTree.Parse(source);
            Assert.Empty(tree.Diagnostics.Where(d => d.IsError));
            return Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)tree.Root).Members));
        }

        // ------------------------------------------------------------------
        // 二元运算符声明
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("+", SyntaxKind.PlusToken)]
        [InlineData("-", SyntaxKind.MinusToken)]
        [InlineData("*", SyntaxKind.StarToken)]
        [InlineData("/", SyntaxKind.SlashToken)]
        [InlineData("%", SyntaxKind.PercentToken)]
        [InlineData("&", SyntaxKind.AmpersandToken)]
        [InlineData("|", SyntaxKind.PipeToken)]
        [InlineData("^", SyntaxKind.HatToken)]
        [InlineData("==", SyntaxKind.EqualsEqualsToken)]
        [InlineData("!=", SyntaxKind.BangEqualsToken)]
        [InlineData("<", SyntaxKind.LessToken)]
        [InlineData("<=", SyntaxKind.LessOrEqualsToken)]
        [InlineData(">", SyntaxKind.GreaterToken)]
        [InlineData(">=", SyntaxKind.GreaterOrEqualsToken)]
        [InlineData("<<", SyntaxKind.ShiftLeftToken)]
        [InlineData(">>", SyntaxKind.ShiftRightToken)]
        public void BinaryOperator_ParsesWithModifiers(string text, SyntaxKind expected)
        {
            var function = ParseSingleFunction(
                $"public static function operator {text}(a: Point, b: Point): Point {{ return a }}");

            Assert.True(function.IsOperatorDeclaration);
            Assert.False(function.IsConversionOperator);
            Assert.NotNull(function.OperatorToken);
            Assert.Equal(expected, function.OperatorToken!.Kind);
            Assert.Equal(text, function.OperatorToken!.Text);
            Assert.Equal(SyntaxKind.OperatorKeyword, function.FunctionKeyword!.Kind);
            Assert.Equal(2, function.Parameters.Count);
        }

        [Theory]
        [InlineData("-", SyntaxKind.MinusToken)]
        [InlineData("!", SyntaxKind.BangToken)]
        [InlineData("~", SyntaxKind.TildeToken)]
        public void UnaryOperator_ParsesWithSingleParameter(string text, SyntaxKind expected)
        {
            var function = ParseSingleFunction($"public static function operator {text}(a: Point): Point {{ return a }}");

            Assert.True(function.IsOperatorDeclaration);
            Assert.Equal(expected, function.OperatorToken!.Kind);
            Assert.Single(function.Parameters);
        }

        [Fact]
        public void Operator_ModifiersPreserved()
        {
            var function = ParseSingleFunction("public static function operator +(a: Point, b: Point): Point { return a }");

            Assert.Contains(function.Modifiers, m => m.Kind == SyntaxKind.PublicKeyword);
            Assert.Contains(function.Modifiers, m => m.Kind == SyntaxKind.StaticKeyword);
        }

        // ------------------------------------------------------------------
        // 转换运算符声明
        // ------------------------------------------------------------------

        [Fact]
        public void ImplicitConversion_Parses()
        {
            var function = ParseSingleFunction("public static function implicit operator int(v: Point): int { return 0 }");

            Assert.True(function.IsOperatorDeclaration);
            Assert.True(function.IsImplicitConversion);
            Assert.False(function.IsExplicitConversion);
            Assert.True(function.IsConversionOperator);
            Assert.Null(function.OperatorToken);
            Assert.Single(function.Parameters);
            Assert.Equal("int", function.Type!.Identifier.Text);
        }

        [Fact]
        public void ExplicitConversion_Parses()
        {
            var function = ParseSingleFunction("public static function explicit operator string(v: int): string { return \"\" }");

            Assert.True(function.IsExplicitConversion);
            Assert.False(function.IsImplicitConversion);
            Assert.Equal("string", function.Type!.Identifier.Text);
        }

        [Fact]
        public void ConversionOperator_QualifiedTargetType_Parses()
        {
            var function = ParseSingleFunction("public static function implicit operator Foo.Bar(v: int): void { }");

            Assert.True(function.IsImplicitConversion);
        }

        // ------------------------------------------------------------------
        // 负例
        // ------------------------------------------------------------------

        [Fact]
        public void Operator_TernaryNotOverloadable_ReportsError()
        {
            var tree = SyntaxTree.Parse("public static function operator ?(a: Point, b: Point): Point { return a }");

            Assert.NotEmpty(tree.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void RegularFunction_NotOperatorDeclaration()
        {
            var function = ParseSingleFunction("function Add(a: int, b: int): int { return a }");

            Assert.False(function.IsOperatorDeclaration);
            Assert.False(function.IsConversionOperator);
            Assert.Null(function.OperatorToken);
        }

        [Fact]
        public void OperatorKeyword_NotUsableAsPlainIdentifier()
        {
            // `operator` 成为关键字后不能再当普通函数名
            var tree = SyntaxTree.Parse("function operator(a: int): int { return a }");

            Assert.NotEmpty(tree.Diagnostics.Where(d => d.IsError));
        }
    }
}
