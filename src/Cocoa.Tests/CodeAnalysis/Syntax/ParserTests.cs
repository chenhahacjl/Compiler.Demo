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
