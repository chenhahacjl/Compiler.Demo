using Cocoa.CodeAnalysis;
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
        [InlineData("false == false", true)]
        [InlineData("true == false", false)]
        [InlineData("false != false", false)]
        [InlineData("true != false", true)]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("!true", false)]
        [InlineData("!false", true)]
        [InlineData("{ var a = 0 (a = 10) * a }", 100)]
        public void SyntacFact_GetText_RoundTrips(string text, object expectedResult)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = new Compilation(syntaxTree);
            var variable = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variable);

            Assert.Empty(result.Diagnostics);
            Assert.Equal(expectedResult, result.Value);
        }
    }
}
