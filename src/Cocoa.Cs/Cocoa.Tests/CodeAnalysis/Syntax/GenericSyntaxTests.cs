using Cocoa.CodeAnalysis.Cocoa.Syntax;
using CSyntax = global::Cocoa.CodeAnalysis.CSharp.Syntax;
using Cocoa.CodeAnalysis.Syntax;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    /// <summary>
    /// 泛型语法解析测试（6e-M20 G0）：类型参数表 / where 约束 / 泛型类型子句 / 显式类型实参调用。
    /// </summary>
    public class GenericSyntaxTests
    {
        [Fact]
        public void Parser_GenericClassDeclaration_ParsesSingleTypeParameter()
        {
            var syntaxTree = SyntaxTree.Parse("public class Box<T> { }");
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.NotNull(classDeclaration.TypeParameters);
            var parameter = Assert.Single(classDeclaration!.TypeParameters!.Parameters);
            Assert.Equal("T", parameter.Text);
        }

        [Fact]
        public void Parser_GenericClassDeclaration_ParsesMultipleTypeParameters()
        {
            var syntaxTree = SyntaxTree.Parse("public class Dict<K, V> extends Object { }");
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.NotNull(classDeclaration.TypeParameters);
            Assert.Equal(2, classDeclaration!.TypeParameters!.Parameters.Length);
            Assert.Equal("K", classDeclaration.TypeParameters.Parameters[0].Text);
            Assert.Equal("V", classDeclaration.TypeParameters.Parameters[1].Text);
        }

        [Fact]
        public void Parser_GenericClassDeclaration_ParsesWhereClauses()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public class SortedList<T> where T: IComparable<T>, new()
{
}");
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            var whereClause = Assert.Single(classDeclaration.WhereClauses);
            Assert.Equal(SyntaxKind.WhereKeyword, whereClause.WhereKeyword.Kind);
            Assert.Equal("T", whereClause.Identifier.Text);
            Assert.Equal(2, whereClause.ConstraintTypes.Length);

            // 接口约束带递归泛型实参（where T: IComparable<T>）
            var interfaceConstraint = Assert.IsType<GenericTypeClauseSyntax>(whereClause.ConstraintTypes[0]);
            Assert.Equal("IComparable", interfaceConstraint.Identifier.Text);
            Assert.Equal("T", Assert.Single(interfaceConstraint.TypeArguments).Identifier.Text);

            // new() 约束合成为 new() 文本标识符
            Assert.Equal("new()", whereClause.ConstraintTypes[1].Identifier.Text);
        }

        [Fact]
        public void Parser_GenericClassDeclaration_ParsesBaseThenWhere()
        {
            // C# 顺序：类型参数 → 基类 → where 子句
            var syntaxTree = SyntaxTree.Parse(@"
public class MyList<T> extends List<T> where T: class
{
}");
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.Single(classDeclaration.BaseTypes);
            var baseType = Assert.IsType<GenericTypeClauseSyntax>(classDeclaration.BaseTypes[0]);
            Assert.Equal("List", baseType.Identifier.Text);
            var whereClause = Assert.Single(classDeclaration.WhereClauses);
            Assert.Equal("class", whereClause.ConstraintTypes[0].Identifier.Text);
        }

        [Fact]
        public void Parser_GenericInterfaceDeclaration_ParsesTypeParameters()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public interface IEnumerable<T>
{
    function GetEnumerator(): IEnumerator<T>
}");
            var interfaceDeclaration = Assert.IsType<InterfaceDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.NotNull(interfaceDeclaration.TypeParameters);
            Assert.Equal("T", Assert.Single(interfaceDeclaration!.TypeParameters!.Parameters).Text);

            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(interfaceDeclaration.Members));
            var returnType = Assert.IsType<GenericTypeClauseSyntax>(method.Type!);
            Assert.Equal("IEnumerator", returnType.Identifier.Text);
            Assert.Equal("T", Assert.Single(returnType.TypeArguments).Identifier.Text);
        }

        [Fact]
        public void Parser_GenericClassField_ParsesGenericTypeClause()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public class Box
{
    private _items: List<int>
}");
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));
            var field = Assert.IsType<ClassFieldDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            var typeClause = Assert.IsType<GenericTypeClauseSyntax>(field.Type);
            Assert.Equal("List", typeClause.Identifier.Text);
            var argument = Assert.Single(typeClause.TypeArguments);
            Assert.Equal("int", argument.Identifier.Text);
            Assert.Equal("List<int>", typeClause.DisplayName);
        }

        [Fact]
        public void Parser_NestedGeneric_ParsesShiftRightSplit()
        {
            // `>>` 词法为单 token：嵌套泛型收尾须拆分为两个 GreaterToken
            var syntaxTree = SyntaxTree.Parse(@"var grid: List<List<int>> = null");
            var statement = Assert.IsType<VariableDeclarationSyntax>(Assert.IsType<GlobalStatementSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members)).Statement);

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            var typeClause = Assert.IsType<GenericTypeClauseSyntax>(statement.TypeClause!);
            var inner = Assert.IsType<GenericTypeClauseSyntax>(Assert.Single(typeClause.TypeArguments));
            Assert.Equal("List", inner.Identifier.Text);
            Assert.Equal("int", Assert.Single(inner.TypeArguments).Identifier.Text);
            Assert.Equal("List<List<int>>", typeClause.DisplayName);
        }

        [Fact]
        public void Parser_ArrayOfGeneric_ParsesSuffixAfterArguments()
        {
            var syntaxTree = SyntaxTree.Parse(@"var lists: List<int>[] = null");
            var statement = Assert.IsType<VariableDeclarationSyntax>(Assert.IsType<GlobalStatementSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members)).Statement);

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            var arrayType = Assert.IsType<ArrayTypeClauseSyntax>(statement.TypeClause!);
            var elementType = Assert.IsType<GenericTypeClauseSyntax>(arrayType.ElementType);
            Assert.Equal("List", elementType.Identifier.Text);
        }

        [Fact]
        public void Parser_ObjectCreation_WithExplicitTypeArguments()
        {
            var expression = ParseExpression("new List<int>(10)");
            var creation = Assert.IsType<ObjectCreationExpressionSyntax>(expression);

            Assert.NotNull(creation.TypeArguments);
            var argument = Assert.Single(creation!.TypeArguments!.Arguments);
            Assert.Equal("int", argument.Identifier.Text);
            Assert.Equal(10, ((LiteralExpressionSyntax)Assert.Single(creation.Arguments)).Value);
        }

        [Fact]
        public void Parser_CallExpression_WithExplicitTypeArguments()
        {
            var expression = ParseExpression("Swap<int>(a, b)");
            var call = Assert.IsType<CallExpressionSyntax>(expression);

            Assert.Equal("Swap", call.Identifier.Text);
            Assert.NotNull(call.TypeArguments);
            Assert.Equal("int", Assert.Single(call!.TypeArguments!.Arguments).Identifier.Text);
            Assert.Equal(2, call.Arguments.Count());
        }

        [Fact]
        public void Parser_MemberCallExpression_WithExplicitTypeArguments()
        {
            var expression = ParseExpression("list.Map<int>(f)");
            var call = Assert.IsType<MemberCallExpressionSyntax>(expression);

            Assert.Equal("Map", call.IdentifierToken.Text);
            Assert.NotNull(call.TypeArguments);
            Assert.Equal("int", Assert.Single(call!.TypeArguments!.Arguments).Identifier.Text);
        }

        [Fact]
        public void Parser_ComparisonExpressions_NotConfusedWithGenerics()
        {
            // 比较表达式不受泛型前瞻影响：闭合角后非 `(` / 非法内容均回退普通解析
            var expression = ParseExpression("a < b && c > d");
            var binary = Assert.IsType<BinaryExpressionSyntax>(expression);
            Assert.Equal(SyntaxKind.AmpersandAmpersandToken, binary.OperatorToken.Kind);

            var expression2 = ParseExpression("x < y");
            var comparison = Assert.IsType<BinaryExpressionSyntax>(expression2);
            Assert.Equal(SyntaxKind.LessToken, comparison.OperatorToken.Kind);
        }

        [Fact]
        public void Parser_GenericClassMethod_ParsesTypeParametersAndWhere()
        {
            var syntaxTree = SyntaxTree.Parse(@"
function Max<T>(a: T, b: T): T where T: IComparable<T>
{
    return a
}");
            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.NotNull(function.TypeParameters);
            Assert.Equal("T", Assert.Single(function!.TypeParameters!.Parameters).Text);
            var parameterType = Assert.IsType<TypeClauseSyntax>(function.Parameters[0].Type);
            Assert.Equal("T", parameterType.Identifier.Text);
            var whereClause = Assert.Single(function.WhereClauses);
            var constraint = Assert.IsType<GenericTypeClauseSyntax>(whereClause.ConstraintTypes[0]);
            Assert.Equal("IComparable", constraint.Identifier.Text);
            Assert.Equal("T", Assert.Single(constraint.TypeArguments).Identifier.Text);
        }

        [Fact]
        public void Parser_CSharpStyle_GenericClassMethod_ParsesTypeParameters()
        {
            var syntaxTree = SyntaxTree.ParseCs(@"
public static T Max<T>(T a, T b) where T : IComparable<T>
{
    return a;
}");
            var function = Assert.IsType<CSyntax.FunctionDeclarationSyntax>(Assert.Single(((CSyntax.CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.NotNull(function.TypeParameters);
            Assert.Equal("Max", function.Identifier.Text);
            Assert.Equal("T", Assert.Single(function!.TypeParameters!.Parameters).Text);
            Assert.Single(function.WhereClauses);
        }

        [Fact]
        public void Parser_CSharpStyle_TopLevelFunctionReturningGeneric()
        {
            var syntaxTree = SyntaxTree.ParseCs(@"
List<int> MakeList(int capacity)
{
    return null;
}");
            var function = Assert.IsType<CSyntax.FunctionDeclarationSyntax>(Assert.Single(((CSyntax.CompilationUnitSyntax)syntaxTree.Root).Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            Assert.Equal("MakeList", function.Identifier.Text);
            var returnType = Assert.IsType<CSyntax.GenericTypeClauseSyntax>(function.Type!);
            Assert.Equal("List", returnType.Identifier.Text);
        }

        [Fact]
        public void Parser_CSharpStyle_GenericClassField_ParsesPrefixGenericType()
        {
            var syntaxTree = SyntaxTree.ParseCs(@"
class Box
{
    private List<int> _items;
}");
            var classDeclaration = Assert.IsType<CSyntax.ClassDeclarationSyntax>(Assert.Single(((CSyntax.CompilationUnitSyntax)syntaxTree.Root).Members));
            var field = Assert.IsType<CSyntax.ClassFieldDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Empty(syntaxTree.Diagnostics.Where(d => d.IsError));
            var typeClause = Assert.IsType<CSyntax.GenericTypeClauseSyntax>(field.Type);
            Assert.Equal("List", typeClause.Identifier.Text);
        }

        private static ExpressionSyntax ParseExpression(string text)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var member = Assert.Single(((CompilationUnitSyntax)syntaxTree.Root).Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);

            return Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression;
        }
    }
}
