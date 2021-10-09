﻿using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    public class EvaluationTests
    {
        [Theory]
        [InlineData("1", 1)]
        [InlineData("+1", 1)]
        [InlineData("-1", -1)]
        [InlineData("12 + 34", 46)]
        [InlineData("12 - 3", 9)]
        [InlineData("4 * 2", 8)]
        [InlineData("9 / 3", 3)]
        [InlineData("(10)", 10)]
        [InlineData("12 == 11", false)]
        [InlineData("3 == 3", true)]
        [InlineData("12 != 11", true)]
        [InlineData("3 != 3", false)]
        [InlineData("3 < 4", true)]
        [InlineData("5 < 4", false)]
        [InlineData("4 <= 4", true)]
        [InlineData("4 <= 5", true)]
        [InlineData("5 <= 4", false)]
        [InlineData("4 > 3", true)]
        [InlineData("4 > 5", false)]
        [InlineData("4 >= 4", true)]
        [InlineData("5 >= 4", true)]
        [InlineData("4 >= 5", false)]
        [InlineData("false == false", true)]
        [InlineData("true == false", false)]
        [InlineData("false != false", false)]
        [InlineData("true != false", true)]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("!true", false)]
        [InlineData("!false", true)]
        [InlineData("{ var a = 0 (a = 10) * a }", 100)]
        [InlineData("{ var a = 0 if a == 0 a = 10 a }", 10)]
        [InlineData("{ var a = 0 if a == 4 a = 10 a }", 0)]
        [InlineData("{ var a = 0 if a == 0 a = 10 else a = 5 a }", 10)]
        [InlineData("{ var a = 0 if a == 4 a = 10 else a = 5 a }", 5)]
        public void Evaluator_Computes_CorrectValues(string text, object expectedResult)
        {
            AssertValue(text, expectedResult);
        }

        [Fact]
        public void Evaluator_VariableDeclaration_Reports_Redeclaration()
        {
            var text = @"
                {
                    var x = 10
                    var y = 100
                    {
                        var x = 10
                    }
                    var [x] = 5
                }
            ";

            var diagnostiscs = @"
                Variable 'x' is already declared.
            ";

            AssertHasDiagnostics(text, diagnostiscs);
        }

        [Fact]
        public void Evaluator_Name_Reports_Undefined()
        {
            var text = @"[x] * 10";

            var diagnostiscs = @"
                Variable 'x' doesn't exist.
            ";

            AssertHasDiagnostics(text, diagnostiscs);
        }

        [Fact]
        public void Evaluator_Assigned_Reports_CannotAssign()
        {
            var text = @"
                {
                    let x = 10
                    x [=] 0
                }
            ";

            var diagnostiscs = @"
                Variable 'x' is read-only and cannot be assigned to.
            ";

            AssertHasDiagnostics(text, diagnostiscs);
        }

        [Fact]
        public void Evaluator_Assigned_Reports_CannotConvert()
        {
            var text = @"
                {
                    var x = 10
                    x = [true]
                }
            ";

            var diagnostiscs = @"
                Cannot convert type 'System.Boolean' to 'System.Int32'.
            ";

            AssertHasDiagnostics(text, diagnostiscs);
        }

        [Fact]
        public void Evaluator_Unary_Reports_Undefined()
        {
            var text = @"[+]true";

            var diagnostiscs = @"
                Unary operator '+' is not defined for type 'System.Boolean'.
            ";

            AssertHasDiagnostics(text, diagnostiscs);
        }

        [Fact]
        public void Evaluator_Binary_Reports_Undefined()
        {
            var text = @"10 [*] false";

            var diagnostiscs = @"
                Binary operator '*' is not defined for type 'System.Int32' and 'System.Boolean'.
            ";

            AssertHasDiagnostics(text, diagnostiscs);
        }

        private static void AssertValue(string text, object expectedResult)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = new Compilation(syntaxTree);
            var variable = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variable);

            Assert.Empty(result.Diagnostics);
            Assert.Equal(expectedResult, result.Value);
        }

        private void AssertHasDiagnostics(string text, string diagnosticText)
        {
            var annotatedText = AnnotatedText.Parse(text);
            var syntaxTree = SyntaxTree.Parse(annotatedText.Text);
            var compilation = new Compilation(syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            var expectedDiagnostics = AnnotatedText.UnindentLines(diagnosticText);

            if (annotatedText.Spans.Length != expectedDiagnostics.Length)
            {
                throw new Exception("ERROR: Must mark as many spans as there are expected diagnostics");
            }

            Assert.Equal(annotatedText.Spans.Length, expectedDiagnostics.Length);

            for (int i = 0; i < expectedDiagnostics.Length; i++)
            {
                var expectedMessage = expectedDiagnostics[i];
                var actualMessage = result.Diagnostics[i].Message;
                Assert.Equal(expectedMessage, actualMessage);

                var expectedSpan = annotatedText.Spans[i];
                var actualSpan = result.Diagnostics[i].Span;
                Assert.Equal(expectedSpan, actualSpan);
            }
        }
    }
}
