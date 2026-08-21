using Cocoa.CodeAnalysis.Syntax;
using System.Diagnostics;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    public class ParserTests
    {
        [Theory]
        [MemberData(nameof(GetBinaryOperatorPairsData))]
        public void Parser_BinaryExpression_HonorsPrecedences(SyntaxKind op1, SyntaxKind op2)
        {
            var op1Precedence = SyntaxFacts.GetBinaryOperatorPrecedence(op1);
            var op2Precedence = SyntaxFacts.GetBinaryOperatorPrecedence(op2);
            var op1Text = SyntaxFacts.GetText(op1);
            var op2Text = SyntaxFacts.GetText(op2);
            var text = $"a {op1Text} b {op2Text} c";
            var expression = ParseExpression(text);

            Debug.Assert(op1Text != null);
            Debug.Assert(op2Text != null);

            if (op1Precedence >= op2Precedence)
            {
                //     op2
                //    /   \
                //   op1   c
                //  /   \
                // a     b

                using (var e = new AssertingEnumerator(expression))
                {
                    e.AssertNode(SyntaxKind.BinaryExpression);
                    e.AssertNode(SyntaxKind.BinaryExpression);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "a");
                    e.AssertToken(op1, op1Text);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "b");
                    e.AssertToken(op2, op2Text);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "c");
                }
            }
            else
            {
                //     op1
                //    /   \
                //   a    op2
                //       /   \
                //      b     c

                using (var e = new AssertingEnumerator(expression))
                {
                    e.AssertNode(SyntaxKind.BinaryExpression);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "a");
                    e.AssertToken(op1, op1Text);
                    e.AssertNode(SyntaxKind.BinaryExpression);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "b");
                    e.AssertToken(op2, op2Text);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "c");
                }
            }
        }

        [Theory]
        [MemberData(nameof(GetUnaryOperatorPairsData))]
        public void Parser_UnaryExpression_HonorsPrecedences(SyntaxKind unaryKind, SyntaxKind binaryKind)
        {
            var unaryPrecedence = SyntaxFacts.GetUnaryOperatorPrecedence(unaryKind);
            var binaryPrecedence = SyntaxFacts.GetBinaryOperatorPrecedence(binaryKind);
            var unaryText = SyntaxFacts.GetText(unaryKind);
            var binaryText = SyntaxFacts.GetText(binaryKind);
            var text = $"{unaryText} a {binaryText} b";
            var expression = ParseExpression(text);

            Debug.Assert(unaryText != null);
            Debug.Assert(binaryText != null);

            if (unaryPrecedence >= binaryPrecedence)
            {
                //   binary
                //   /    \
                // unary   b
                //   |
                //   a

                using (var e = new AssertingEnumerator(expression))
                {
                    e.AssertNode(SyntaxKind.BinaryExpression);
                    e.AssertNode(SyntaxKind.UnaryExpression);
                    e.AssertToken(unaryKind, unaryText);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "a");
                    e.AssertToken(binaryKind, binaryText);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "b");
                }
            }
            else
            {
                //  unary
                //    |
                //  binary
                //  /   \
                // a     b

                using (var e = new AssertingEnumerator(expression))
                {
                    e.AssertNode(SyntaxKind.UnaryExpression);
                    e.AssertToken(unaryKind, unaryText);
                    e.AssertNode(SyntaxKind.BinaryExpression);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "a");
                    e.AssertToken(binaryKind, binaryText);
                    e.AssertNode(SyntaxKind.NameExpression);
                    e.AssertToken(SyntaxKind.IdentifierToken, "b");
                }
            }
        }

        [Fact]
        public void Parser_ImportClause_ParsesDottedName()
        {
            var syntaxTree = SyntaxTree.Parse("import kernel32.dll");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var importClause = Assert.IsType<ImportClauseSyntax>(member);

            Assert.Equal("kernel32.dll", importClause.DllName);

            using (var e = new AssertingEnumerator(member))
            {
                e.AssertNode(SyntaxKind.ImportClause);
                e.AssertToken(SyntaxKind.ImportKeyword, "import");
                e.AssertToken(SyntaxKind.IdentifierToken, "kernel32");
                e.AssertToken(SyntaxKind.DotToken, ".");
                e.AssertToken(SyntaxKind.IdentifierToken, "dll");
            }
        }

        [Fact]
        public void Parser_StdcallExternDeclaration_ParsesNoBody()
        {
            var syntaxTree = SyntaxTree.Parse("stdcall function GetTickCount(): int");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Equal(SyntaxKind.StdcallKeyword, Assert.Single(function.Modifiers).Kind);
            Assert.Null(function.Body);

            using (var e = new AssertingEnumerator(member))
            {
                e.AssertNode(SyntaxKind.FunctionDeclaration);
                e.AssertToken(SyntaxKind.StdcallKeyword, "stdcall");
                e.AssertToken(SyntaxKind.FunctionKeyword, "function");
                e.AssertToken(SyntaxKind.IdentifierToken, "GetTickCount");
                e.AssertToken(SyntaxKind.OpenParenthesisToken, "(");
                e.AssertToken(SyntaxKind.CloseParenthesisToken, ")");
                e.AssertNode(SyntaxKind.TypeClause);
                e.AssertToken(SyntaxKind.ColonToken, ":");
                e.AssertToken(SyntaxKind.IdentifierToken, "int");
            }
        }

        [Fact]
        public void Parser_CdeclExternDeclaration_ParsesBodyWhenPresent()
        {
            var syntaxTree = SyntaxTree.Parse(@"
cdecl function double(x: int): int
{
    return x * 2
}");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Equal(SyntaxKind.CdeclKeyword, Assert.Single(function.Modifiers).Kind);
            Assert.NotNull(function.Body);
        }

        [Fact]
        public void Parser_ClassDeclaration_ParsesMembers()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public class Point
{
    private _x: int
    private _y: int

    public constructor(x: int, y: int)
    {
        _x = x
        _y = y
    }

    public function Area(): int
    {
        return _x * _y
    }
}");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);

            Assert.Equal("Point", classDeclaration.Identifier.Text);
            Assert.Equal(4, classDeclaration.Members.Length);
            Assert.IsType<ClassFieldDeclarationSyntax>(classDeclaration.Members[0]);
            Assert.IsType<ClassFieldDeclarationSyntax>(classDeclaration.Members[1]);
            Assert.IsType<ConstructorDeclarationSyntax>(classDeclaration.Members[2]);
            Assert.IsType<FunctionDeclarationSyntax>(classDeclaration.Members[3]);

            var field = Assert.IsType<ClassFieldDeclarationSyntax>(classDeclaration.Members[0]);
            Assert.Equal("_x", field.Identifier.Text);
            Assert.Equal(SyntaxKind.PrivateKeyword, Assert.Single(field.Modifiers).Kind);

            var constructor = Assert.IsType<ConstructorDeclarationSyntax>(classDeclaration.Members[2]);
            Assert.Equal(2, constructor.Parameters.GetWithSeparators().Count(s => s is ParameterSyntax));
        }

        [Fact]
        public void Parser_ClassDeclaration_Traversal()
        {
            var syntaxTree = SyntaxTree.Parse("public class Foo { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);

            using (var e = new AssertingEnumerator(member))
            {
                e.AssertNode(SyntaxKind.ClassDeclaration);
                e.AssertToken(SyntaxKind.PublicKeyword, "public");
                e.AssertToken(SyntaxKind.ClassKeyword, "class");
                e.AssertToken(SyntaxKind.IdentifierToken, "Foo");
                e.AssertToken(SyntaxKind.OpenBraceToken, "{");
                e.AssertToken(SyntaxKind.CloseBraceToken, "}");
            }
        }

        [Fact]
        public void Parser_Parses_ConstVariableDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("const x: int = 5");
            var root = Assert.IsType<CompilationUnitSyntax>(syntaxTree.Root);
            var member = Assert.IsType<GlobalStatementSyntax>(Assert.Single(root.Members));
            var statement = Assert.IsType<VariableDeclarationSyntax>(member.Statement);

            Assert.Equal(SyntaxKind.ConstKeyword, statement.Keyword.Kind);
            Assert.Equal("x", statement.Identifier.Text);
            Assert.NotNull(statement.TypeClause);
            Assert.NotNull(statement.EqualsToken);
            Assert.NotNull(statement.Initializer);
        }

        [Fact]
        public void Parser_Parses_VariableDeclaration_WithoutInitializer()
        {
            var syntaxTree = SyntaxTree.Parse("var a: int");
            var root = Assert.IsType<CompilationUnitSyntax>(syntaxTree.Root);
            var member = Assert.IsType<GlobalStatementSyntax>(Assert.Single(root.Members));
            var statement = Assert.IsType<VariableDeclarationSyntax>(member.Statement);

            Assert.Equal(SyntaxKind.VarKeyword, statement.Keyword.Kind);
            Assert.Equal("a", statement.Identifier.Text);
            Assert.NotNull(statement.TypeClause);
            Assert.Null(statement.EqualsToken);
            Assert.Null(statement.Initializer);
        }

        [Fact]
        public void Parser_Parses_CStyleForStatement()
        {
            var syntaxTree = SyntaxTree.Parse(@"
for (var i = 0; i < 10; i++)
{
    print(i)
}");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<CStyleForStatementSyntax>(globalStatement.Statement);

            Assert.Equal("for", statement.Keyword.Text);
            Assert.Equal("(", statement.OpenParenToken.Text);
            Assert.IsType<VariableDeclarationSyntax>(statement.Init);
            Assert.NotNull(statement.SemicolonToken1);
            Assert.Equal(SyntaxKind.BinaryExpression, statement.Condition!.Kind);
            Assert.NotNull(statement.SemicolonToken2);
            Assert.Equal(SyntaxKind.PostfixIncrementExpression, statement.Update!.Kind);
            Assert.Equal(")", statement.CloseParenToken.Text);
            Assert.IsType<BlockStatementSyntax>(statement.Body);

            using (var e = new AssertingEnumerator(statement))
            {
                e.AssertNode(SyntaxKind.CStyleForStatement);
                e.AssertToken(SyntaxKind.ForKeyword, "for");
                e.AssertToken(SyntaxKind.OpenParenthesisToken, "(");
                e.AssertNode(SyntaxKind.VariableDeclaration);
                e.AssertToken(SyntaxKind.VarKeyword, "var");
                e.AssertToken(SyntaxKind.IdentifierToken, "i");
                e.AssertToken(SyntaxKind.EqualsToken, "=");
                e.AssertNode(SyntaxKind.LiteralExpression);
                e.AssertToken(SyntaxKind.NumberToken, "0");
                e.AssertToken(SyntaxKind.SemicolonToken, ";");
                e.AssertNode(SyntaxKind.BinaryExpression);
                e.AssertNode(SyntaxKind.NameExpression);
                e.AssertToken(SyntaxKind.IdentifierToken, "i");
                e.AssertToken(SyntaxKind.LessToken, "<");
                e.AssertNode(SyntaxKind.LiteralExpression);
                e.AssertToken(SyntaxKind.NumberToken, "10");
                e.AssertToken(SyntaxKind.SemicolonToken, ";");
                e.AssertNode(SyntaxKind.PostfixIncrementExpression);
                e.AssertNode(SyntaxKind.NameExpression);
                e.AssertToken(SyntaxKind.IdentifierToken, "i");
                e.AssertToken(SyntaxKind.PlusPlusToken, "++");
                e.AssertToken(SyntaxKind.CloseParenthesisToken, ")");
                e.AssertNode(SyntaxKind.BlockStatement);
                e.AssertToken(SyntaxKind.OpenBraceToken, "{");
                e.AssertNode(SyntaxKind.ExpressionStatement);
                e.AssertNode(SyntaxKind.CallExpression);
                e.AssertToken(SyntaxKind.IdentifierToken, "print");
                e.AssertToken(SyntaxKind.OpenParenthesisToken, "(");
                e.AssertNode(SyntaxKind.NameExpression);
                e.AssertToken(SyntaxKind.IdentifierToken, "i");
                e.AssertToken(SyntaxKind.CloseParenthesisToken, ")");
                e.AssertToken(SyntaxKind.CloseBraceToken, "}");
            }
        }

        [Fact]
        public void Parser_Parses_CStyleForStatement_EmptyParts()
        {
            var syntaxTree = SyntaxTree.Parse("for (;;) { break }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<CStyleForStatementSyntax>(globalStatement.Statement);

            Assert.Null(statement.Init);
            Assert.NotNull(statement.SemicolonToken1);
            Assert.Null(statement.Condition);
            Assert.NotNull(statement.SemicolonToken2);
            Assert.Null(statement.Update);
        }

        [Fact]
        public void Parser_Parses_CStyleForStatement_MissingParts()
        {
            var syntaxTree = SyntaxTree.Parse("for (; i < 10;) { i = i + 1 }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<CStyleForStatementSyntax>(globalStatement.Statement);

            Assert.Null(statement.Init);
            Assert.NotNull(statement.SemicolonToken1);
            Assert.NotNull(statement.Condition);
            Assert.NotNull(statement.SemicolonToken2);
            Assert.Null(statement.Update);
        }

        [Fact]
        public void Parser_Parses_Class_ExtendsKeyword()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo extends Bar { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);

            Assert.Equal("Foo", classDeclaration.Identifier.Text);
            var baseType = Assert.Single(classDeclaration.BaseTypes);
            Assert.Equal(SyntaxKind.ExtendsKeyword, baseType.ColonToken!.Kind);
            Assert.Equal("Bar", baseType.Identifier.Text);
        }

        [Fact]
        public void Parser_Parses_Interface_ExtendsKeyword()
        {
            var syntaxTree = SyntaxTree.Parse("interface IFoo extends IBar, IBaz { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var interfaceDeclaration = Assert.IsType<InterfaceDeclarationSyntax>(member);

            Assert.Equal("IFoo", interfaceDeclaration.Identifier.Text);
            Assert.Equal(2, interfaceDeclaration.BaseTypes.Length);
            Assert.Equal(SyntaxKind.ExtendsKeyword, interfaceDeclaration.BaseTypes[0].ColonToken!.Kind);
            Assert.Equal("IBar", interfaceDeclaration.BaseTypes[0].Identifier.Text);
            Assert.Equal("IBaz", interfaceDeclaration.BaseTypes[1].Identifier.Text);
        }

        [Fact]
        public void Parser_Parses_PostfixIncrement()
        {
            var expression = ParseExpression("i++");

            var postfix = Assert.IsType<PostfixIncrementExpressionSyntax>(expression);
            Assert.IsType<NameExpressionSyntax>(postfix.Operand);
            Assert.Equal(SyntaxKind.PlusPlusToken, postfix.OperatorToken.Kind);
        }

        [Fact]
        public void Parser_Parses_RangeFor_ParenthesizedVar()
        {
            var syntaxTree = SyntaxTree.Parse("for (var i = 1 to 10) { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForStatementSyntax>(globalStatement.Statement);

            Assert.Equal(SyntaxKind.OpenParenthesisToken, statement.OpenParenToken!.Kind);
            Assert.Equal(SyntaxKind.VarKeyword, statement.VarKeyword!.Kind);
            Assert.Equal("i", statement.Identifier!.Text);
            Assert.Equal(SyntaxKind.EqualsToken, statement.EqualsToken!.Kind);
            Assert.Equal(SyntaxKind.CloseParenthesisToken, statement.CloseParenToken!.Kind);
        }

        [Fact]
        public void Parser_Parses_RangeFor_NoParens()
        {
            var syntaxTree = SyntaxTree.Parse("for var i = 1 to 10 { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForStatementSyntax>(globalStatement.Statement);

            Assert.Null(statement.OpenParenToken);
            Assert.Equal(SyntaxKind.VarKeyword, statement.VarKeyword!.Kind);
            Assert.Equal("i", statement.Identifier!.Text);
            Assert.Null(statement.CloseParenToken);
        }

        [Fact]
        public void Parser_Parses_RangeFor_ReuseExistingVariable()
        {
            var syntaxTree = SyntaxTree.Parse("for (i = 1 to 10) { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForStatementSyntax>(globalStatement.Statement);

            Assert.Equal(SyntaxKind.OpenParenthesisToken, statement.OpenParenToken!.Kind);
            Assert.Null(statement.VarKeyword);
            Assert.Equal("i", statement.Identifier!.Text);
            Assert.Equal(SyntaxKind.CloseParenthesisToken, statement.CloseParenToken!.Kind);
        }

        [Fact]
        public void Parser_Parses_RangeFor_CountOnly()
        {
            var syntaxTree = SyntaxTree.Parse("for (1 to 10) { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForStatementSyntax>(globalStatement.Statement);

            Assert.Null(statement.VarKeyword);
            Assert.Null(statement.Identifier);
            Assert.Null(statement.EqualsToken);
            Assert.Equal(SyntaxKind.LiteralExpression, statement.LowerBound.Kind);
            Assert.Equal(SyntaxKind.LiteralExpression, statement.UpperBound.Kind);
        }

        [Fact]
        public void Parser_Parses_RangeFor_CountOnly_NoParens()
        {
            var syntaxTree = SyntaxTree.Parse("for 1 to 5 { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForStatementSyntax>(globalStatement.Statement);

            Assert.Null(statement.Identifier);
        }

        [Fact]
        public void Parser_RangeFor_LetKeyword_ReportsError()
        {
            var syntaxTree = SyntaxTree.Parse("for let i = 1 to 10 { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForStatementSyntax>(globalStatement.Statement);

            Assert.Equal(SyntaxKind.LetKeyword, statement.VarKeyword!.Kind);
            var diagnostic = Assert.Single(syntaxTree.Diagnostics);
            Assert.Contains("只能用 var", diagnostic.Message);
        }

        [Fact]
        public void Parser_CStyleFor_NotConfusedWithRangeFor()
        {
            var syntaxTree = SyntaxTree.Parse("for (var i = 0; i < 10; i++) { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);

            Assert.IsType<CStyleForStatementSyntax>(globalStatement.Statement);
        }

        [Fact]
        public void Parser_Foreach_CocoaStyle_Parses()
        {
            var syntaxTree = SyntaxTree.Parse("foreach (x in arr) { print(x) }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForeachStatementSyntax>(globalStatement.Statement);

            Assert.Null(statement.VarKeyword);
            Assert.Equal("x", statement.Identifier.Text);
            Assert.Equal(SyntaxKind.InKeyword, statement.InKeyword.Kind);
            Assert.Equal(SyntaxKind.NameExpression, statement.Collection.Kind);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_Foreach_CSharpVarStyle_Parses()
        {
            var syntaxTree = SyntaxTree.Parse("foreach (var x in arr) { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForeachStatementSyntax>(globalStatement.Statement);

            Assert.Equal(SyntaxKind.VarKeyword, statement.VarKeyword!.Kind);
            Assert.Equal("x", statement.Identifier.Text);
            Assert.Equal(SyntaxKind.NameExpression, statement.Collection.Kind);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_Foreach_LetKeyword_ReportsError()
        {
            var syntaxTree = SyntaxTree.Parse("foreach let x in arr { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ForeachStatementSyntax>(globalStatement.Statement);

            Assert.Equal(SyntaxKind.LetKeyword, statement.VarKeyword!.Kind);
            var diagnostic = Assert.Single(syntaxTree.Diagnostics);
            Assert.Contains("只能用 var", diagnostic.Message);
        }

        [Fact]
        public void Parser_SwitchStatement_Parses()
        {
            var syntaxTree = SyntaxTree.Parse(@"
switch (x)
{
    case 1:
    {
        print(""one"")
        break
    }
    case 2 when x > 0:
    {
        print(""two"")
        break
    }
    default:
    {
        print(""other"")
        break
    }
}");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<SwitchStatementSyntax>(globalStatement.Statement);

            Assert.Equal(3, statement.Sections.Length);
            var case1 = Assert.IsType<CaseClauseSyntax>(statement.Sections[0]);
            Assert.Single(case1.Values);
            Assert.Null(case1.WhenCondition);
            var case2 = Assert.IsType<CaseClauseSyntax>(statement.Sections[1]);
            Assert.NotNull(case2.WhenKeyword);
            Assert.NotNull(case2.WhenCondition);
            Assert.IsType<DefaultClauseSyntax>(statement.Sections[2]);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_SwitchStatement_StackedCases_Parse()
        {
            var syntaxTree = SyntaxTree.Parse(@"
switch (x)
{
    case 1:
    case 2:
    {
        print(""low"")
        break
    }
}");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<SwitchStatementSyntax>(globalStatement.Statement);

            Assert.Equal(2, statement.Sections.Length);
            var case1 = Assert.IsType<CaseClauseSyntax>(statement.Sections[0]);
            var emptyBody = Assert.IsType<BlockStatementSyntax>(case1.Body);
            Assert.Empty(emptyBody.Statements);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_SwitchStatement_MultipleValues_Parse()
        {
            var syntaxTree = SyntaxTree.Parse(@"
switch (x)
{
    case 1, 2, 3:
    {
        print(""low"")
        break
    }
}");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<SwitchStatementSyntax>(globalStatement.Statement);

            var case1 = Assert.IsType<CaseClauseSyntax>(Assert.Single(statement.Sections));
            Assert.Equal(3, case1.Values.Count);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_Constructor_ExtendsBaseKeyword()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { constructor(x: int) extends base(x) { } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var constructor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal(SyntaxKind.BaseKeyword, constructor.InitializerKeyword!.Kind);
            Assert.Equal(1, constructor.InitializerArguments.Count);
        }

        [Fact]
        public void Parser_Constructor_ExtendsThisKeyword()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { constructor(x: int) extends this(x) { } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var constructor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal(SyntaxKind.ThisKeyword, constructor.InitializerKeyword!.Kind);
        }

        [Fact]
        public void Parser_Constructor_ExtendsInvalidInitializer_ReportsError()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { constructor() extends foo() { } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var constructor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Null(constructor.InitializerKeyword);
            Assert.Contains(syntaxTree.Diagnostics, d => d.Message.Contains("expected <BaseKeyword>"));
        }

        [Fact]
        public void Parser_CSharpStyleField_BindsToClassField()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { private int _x; }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var field = Assert.IsType<ClassFieldDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal("_x", field.Identifier.Text);
            Assert.Equal("int", field.Type.Identifier.Text);
            Assert.False(field.HasInitializer);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleFieldWithInitializer_BindsToClassField()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { private int _x = 5; }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var field = Assert.IsType<ClassFieldDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal("_x", field.Identifier.Text);
            Assert.True(field.HasInitializer);
            Assert.IsType<LiteralExpressionSyntax>(field.Initializer);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CocoaStyleFieldWithInitializer_BindsToClassField()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { private _x: int = 5 }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var field = Assert.IsType<ClassFieldDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal("_x", field.Identifier.Text);
            Assert.True(field.HasInitializer);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleMethod_BindsToFunctionDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public int Area() { return 1; } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal("Area", method.Identifier.Text);
            Assert.Null(method.FunctionKeyword);
            Assert.Equal("int", method.Type!.Identifier.Text);
            Assert.NotNull(method.Body);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleConstructor_BindsToConstructorDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public Foo(int x, int y) { } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var constructor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Null(constructor.ConstructorKeyword);
            Assert.Equal(2, constructor.Parameters.Count);
            Assert.Equal("x", constructor.Parameters[0].Identifier.Text);
            Assert.Equal("int", constructor.Parameters[0].Type.Identifier.Text);
            Assert.Equal("y", constructor.Parameters[1].Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleConstructor_BaseChain()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo: Bar { public Foo(int x) : base(x) { } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var constructor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Equal(SyntaxKind.BaseKeyword, constructor.InitializerKeyword!.Kind);
            Assert.Equal(1, constructor.InitializerArguments.Count);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleAutoProperty_BindsToPropertyDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public string Name { get; set; } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var property = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Null(property.PropertyKeyword);
            Assert.Equal("Name", property.Identifier.Text);
            Assert.Equal("string", property.Type.Identifier.Text);
            Assert.True(property.IsAuto);
            Assert.Equal(SyntaxKind.GetKeyword, property.Getter!.Keyword.Kind);
            Assert.Equal(SyntaxKind.SetKeyword, property.Setter!.Keyword.Kind);
            Assert.False(property.HasInitializer);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleAutoPropertyWithInitializer_BindsToPropertyDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public int X { get; set; } = 42; }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var property = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.True(property.IsAuto);
            Assert.True(property.HasInitializer);
            Assert.IsType<LiteralExpressionSyntax>(property.Initializer);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleArrayParameter_BindsToParameter()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public int Sum(int[] values) { return 0; } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(classDeclaration.Members));
            var parameter = Assert.Single(method.Parameters);

            Assert.Equal("values", parameter.Identifier.Text);
            Assert.IsType<ArrayTypeClauseSyntax>(parameter.Type);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleExpressionBodiedMethod_SynthesizesReturnBlock()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public int Area() => _x * _y; }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            var block = method.Body!;
            var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(block.Statements));
            Assert.IsType<BinaryExpressionSyntax>(statement.Expression);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CocoaStyleExpressionBodiedFunction_SynthesizesReturnBlock()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public function Area(): int => _x * _y; }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            var block = method.Body!;
            var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(block.Statements));
            Assert.IsType<BinaryExpressionSyntax>(statement.Expression);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleExpressionBodiedProperty_SynthesizesGetter()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public int X => _x; }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var property = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.NotNull(property.Getter);
            Assert.Null(property.Setter);
            Assert.False(property.IsAuto);
            var body = property.Getter!.Body!;
            var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(body.Statements));
            Assert.IsType<NameExpressionSyntax>(statement.Expression);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CocoaStyleExpressionBodiedProperty_SynthesizesGetter()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public property X: int => _x; }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var property = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.NotNull(property.Getter);
            Assert.Null(property.Setter);
            Assert.True(property.PropertyKeyword != null);
            var body = property.Getter!.Body!;
            var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(body.Statements));
            Assert.IsType<NameExpressionSyntax>(statement.Expression);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleAccessorModifier_BindsToPropertyAccessor()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public int X { get; private set; } }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var property = Assert.IsType<PropertyDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Empty(property.Getter!.Modifiers);
            var setterModifier = Assert.Single(property.Setter!.Modifiers);
            Assert.Equal(SyntaxKind.PrivateKeyword, setterModifier.Kind);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_ExpressionBodiedTopLevelFunction_SynthesizesReturnBlock()
        {
            var syntaxTree = SyntaxTree.Parse("public static int Add(int a, int b) => a + b;");
            var root = syntaxTree.Root;
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(root.Members));

            var block = method.Body!;
            var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(block.Statements));
            Assert.IsType<BinaryExpressionSyntax>(statement.Expression);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_VerbatimStringLiteral_Parses()
        {
            var syntaxTree = SyntaxTree.Parse(@"@""hi""");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var literal = Assert.IsType<LiteralExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression);
            Assert.Equal(SyntaxKind.VerbatimStringToken, literal.LiteralToken.Kind);
            Assert.Equal("hi", literal.Value);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_RawStringLiteral_Parses()
        {
            var syntaxTree = SyntaxTree.Parse("function Main() { print(\"\"\"hi\"\"\") }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<FunctionDeclarationSyntax>(member);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_InterpolatedString_Parses()
        {
            var syntaxTree = SyntaxTree.Parse("$\"a{1}b\"");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression);

            Assert.Equal(3, interpolated.Contents.Length);
            Assert.IsType<InterpolatedStringTextSyntax>(interpolated.Contents[0]);
            var hole = Assert.IsType<InterpolationSyntax>(interpolated.Contents[1]);
            Assert.IsType<LiteralExpressionSyntax>(hole.Expression);
            Assert.IsType<InterpolatedStringTextSyntax>(interpolated.Contents[2]);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_InterpolatedString_EscapedBraces()
        {
            var syntaxTree = SyntaxTree.Parse("$\"a{{b}}c\"");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression);

            var text = Assert.IsType<InterpolatedStringTextSyntax>(Assert.Single(interpolated.Contents));
            Assert.Equal("a{b}c", text.TextToken.Value);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Theory]
        [InlineData("$\"x{1}\"")]
        [InlineData("$@\"x{1}\"")]
        [InlineData("@$\"x{1}\"")]
        public void Parser_InterpolatedString_CSharpPrefixes_Parse(string source)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression);
            Assert.Equal(2, interpolated.Contents.Length);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_InterpolatedString_HoleDiagnostics_AreLocated()
        {
            var syntaxTree = SyntaxTree.Parse("$\"x{1 +}y\"");
            Assert.Contains(syntaxTree.Diagnostics, d => d.Message.Contains("Unexpected token"));
        }

        [Theory]
        [InlineData("$\"{1.5:F2}\"", "F2")]
        [InlineData("$\"{1.0:E}\"", "E")]
        [InlineData("$\"{1e22:G}\"", "G")]
        [InlineData("$\"{255:D}\"", "D")]
        [InlineData("$\"{42,5:X2}\"", "X2")]
        public void Parser_InterpolatedString_FormatSpecifier_ExcludesClosingBrace(string source, string expectedFormat)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression);
            var hole = Assert.IsType<InterpolationSyntax>(Assert.Single(interpolated.Contents));
            Assert.Equal(expectedFormat, hole.FormatToken!.Value);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleLocalVariable_BindsToVariableDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public void Bar() { int x = 10; print(x); } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(classDeclaration.Members));
            var block = method.Body!;
            var declaration = Assert.IsType<VariableDeclarationSyntax>(block.Statements[0]);

            Assert.Null(declaration.Keyword);
            Assert.Equal("x", declaration.Identifier.Text);
            Assert.Equal("int", declaration.TypeClause!.Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleInterfaceMembers_BindToMembers()
        {
            var syntaxTree = SyntaxTree.Parse("interface IFoo { int Area(); string Name { get; } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var interfaceDeclaration = Assert.IsType<InterfaceDeclarationSyntax>(member);

            Assert.Equal(2, interfaceDeclaration.Members.Length);
            var method = Assert.IsType<FunctionDeclarationSyntax>(interfaceDeclaration.Members[0]);
            Assert.Equal("Area", method.Identifier.Text);
            Assert.Null(method.Body);
            var property = Assert.IsType<PropertyDeclarationSyntax>(interfaceDeclaration.Members[1]);
            Assert.Equal("Name", property.Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleTopLevelFunction_BindsToFunctionDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("public static void Main() { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Equal(2, function.Modifiers.Length);
            Assert.Equal(SyntaxKind.PublicKeyword, function.Modifiers[0].Kind);
            Assert.Equal(SyntaxKind.StaticKeyword, function.Modifiers[1].Kind);
            Assert.Null(function.FunctionKeyword);
            Assert.Equal("Main", function.Identifier.Text);
            Assert.Equal("void", function.Type!.Identifier.Text);
            Assert.NotNull(function.Body);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleTopLevelFunction_WithParamsAndReturnType()
        {
            var syntaxTree = SyntaxTree.Parse("public int Add(int x, int y) { return x + y; }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Equal("Add", function.Identifier.Text);
            Assert.Equal("int", function.Type!.Identifier.Text);
            Assert.Equal(2, function.Parameters.Count);
            Assert.Equal("x", function.Parameters[0].Identifier.Text);
            Assert.Equal("int", function.Parameters[0].Type.Identifier.Text);
            Assert.Equal("y", function.Parameters[1].Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleTopLevelFunction_ArrayReturnType()
        {
            var syntaxTree = SyntaxTree.Parse("int[] GetNums() { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.IsType<ArrayTypeClauseSyntax>(function.Type);
            Assert.Equal("GetNums", function.Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleTopLevelFunction_SemicolonNoBody()
        {
            var syntaxTree = SyntaxTree.Parse("public void Setup();");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Null(function.Body);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_NoKeywordTopLevelFunction_WithReturnType()
        {
            var syntaxTree = SyntaxTree.Parse("Main(): void { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Null(function.FunctionKeyword);
            Assert.Equal("Main", function.Identifier.Text);
            Assert.Equal("void", function.Type!.Identifier.Text);
            Assert.NotNull(function.Body);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_NoKeywordTopLevelFunction_WithoutReturnType()
        {
            var syntaxTree = SyntaxTree.Parse("Main() { print(1) }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Null(function.FunctionKeyword);
            Assert.Equal("Main", function.Identifier.Text);
            Assert.Null(function.Type);
            Assert.NotNull(function.Body);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_NoKeywordTopLevelFunction_WithModifiers()
        {
            var syntaxTree = SyntaxTree.Parse("public Main(): void { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var function = Assert.IsType<FunctionDeclarationSyntax>(member);

            Assert.Equal(SyntaxKind.PublicKeyword, Assert.Single(function.Modifiers).Kind);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_GlobalCallStatement_NotConfusedWithFunction()
        {
            var syntaxTree = SyntaxTree.Parse("print(\"hi\")");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            var statement = Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement);
            Assert.IsType<CallExpressionSyntax>(statement.Expression);
        }

        [Fact]
        public void Parser_GlobalStatement_NestedParens_NotConfusedWithFunction()
        {
            var syntaxTree = SyntaxTree.Parse("print(Foo(x))");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_UsingDirective_WithSemicolon_NoDiagnostics()
        {
            var syntaxTree = SyntaxTree.Parse("using Foo.Bar;");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var usingDirective = Assert.IsType<UsingDirectiveSyntax>(member);

            Assert.Equal("Foo.Bar", usingDirective.Name);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_UsingDirective_WithoutSemicolon_NoDiagnostics()
        {
            var syntaxTree = SyntaxTree.Parse("using Foo.Bar");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var usingDirective = Assert.IsType<UsingDirectiveSyntax>(member);

            Assert.Equal("Foo.Bar", usingDirective.Name);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_TopLevelStatement_WithTrailingSemicolon_NoDiagnostics()
        {
            var syntaxTree = SyntaxTree.Parse("print(1);");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);
            Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_CSharpStyleConstLocal_BindsToVariableDeclaration()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { public void Bar() { const int x = 10; print(x); } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);
            var method = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(classDeclaration.Members));
            var block = method.Body!;
            var declaration = Assert.IsType<VariableDeclarationSyntax>(block.Statements[0]);

            Assert.Equal(SyntaxKind.ConstKeyword, declaration.Keyword!.Kind);
            Assert.Equal("x", declaration.Identifier.Text);
            Assert.Equal("int", declaration.TypeClause!.Identifier.Text);
            Assert.NotNull(declaration.EqualsToken);
            Assert.IsType<LiteralExpressionSyntax>(declaration.Initializer);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_ClassDeclaration_MultipleBaseTypes()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo: Bar, IA, IB { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);

            Assert.Equal(3, classDeclaration.BaseTypes.Length);
            Assert.Equal("Bar", classDeclaration.BaseTypes[0].Identifier.Text);
            Assert.Equal("IA", classDeclaration.BaseTypes[1].Identifier.Text);
            Assert.Equal("IB", classDeclaration.BaseTypes[2].Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_ClassDeclaration_Extends_MultipleBaseTypes()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo extends Bar, IA { }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);

            Assert.Equal(2, classDeclaration.BaseTypes.Length);
            Assert.Equal(SyntaxKind.ExtendsKeyword, classDeclaration.BaseTypes[0].ColonToken!.Kind);
            Assert.Equal("IA", classDeclaration.BaseTypes[1].Identifier.Text);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_MixedCocoaAndCSharpStyleMembers_InOneClass()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { private _x: int public int Y { get; set; } public function Get(): int { return _x; } }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(member);

            Assert.Equal(3, classDeclaration.Members.Length);
            Assert.IsType<ClassFieldDeclarationSyntax>(classDeclaration.Members[0]);
            Assert.IsType<PropertyDeclarationSyntax>(classDeclaration.Members[1]);
            Assert.IsType<FunctionDeclarationSyntax>(classDeclaration.Members[2]);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_StaticConstructor_CSharpStyle_Parses()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { static Foo() { x = 1; } }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var ctor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Contains(ctor.Modifiers, m => m.Kind == SyntaxKind.StaticKeyword);
            Assert.Null(ctor.ConstructorKeyword);
            Assert.Empty(ctor.Parameters);
            Assert.Null(ctor.InitializerKeyword);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        [Fact]
        public void Parser_StaticConstructor_CocoaStyle_Parses()
        {
            var syntaxTree = SyntaxTree.Parse("class Foo { static constructor() { x = 1; } }");
            var root = syntaxTree.Root;
            var classDeclaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(root.Members));
            var ctor = Assert.IsType<ConstructorDeclarationSyntax>(Assert.Single(classDeclaration.Members));

            Assert.Contains(ctor.Modifiers, m => m.Kind == SyntaxKind.StaticKeyword);
            Assert.Equal(SyntaxKind.ConstructorKeyword, ctor.ConstructorKeyword!.Kind);
            Assert.Empty(ctor.Parameters);
            Assert.Null(ctor.InitializerKeyword);
            Assert.Empty(syntaxTree.Diagnostics);
        }

        private static ExpressionSyntax ParseExpression(string text)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var globalStatement = Assert.IsType<GlobalStatementSyntax>(member);

            return Assert.IsType<ExpressionStatementSyntax>(globalStatement.Statement).Expression;
        }

        public static IEnumerable<object[]> GetBinaryOperatorPairsData()
        {
            foreach (var op1 in SyntaxFacts.GetBinaryOperatorKinds())
            {
                foreach (var op2 in SyntaxFacts.GetBinaryOperatorKinds())
                {
                    yield return new object[] { op1, op2 };
                }
            }
        }

        public static IEnumerable<object[]> GetUnaryOperatorPairsData()
        {
            foreach (var unary in SyntaxFacts.GetUnaryOperatorKinds())
            {
                foreach (var binary in SyntaxFacts.GetBinaryOperatorKinds())
                {
                    yield return new object[] { unary, binary };
                }
            }
        }
    }
}
