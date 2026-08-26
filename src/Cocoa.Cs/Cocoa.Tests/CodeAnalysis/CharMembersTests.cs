using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Char.co 新增成员冒烟（6e-G7 ⑤b）：
    /// IsLetterOrDigit/IsControl/IsHexDigit/IsUpper/IsLower ×Evaluator。
    /// </summary>
    public class CharMembersTests
    {
        [Theory]
        [InlineData("Char.IsLetterOrDigit('a')", true)]
        [InlineData("Char.IsLetterOrDigit('5')", true)]
        [InlineData("Char.IsLetterOrDigit('!')", false)]
        [InlineData("Char.IsHexDigit('0')", true)]
        [InlineData("Char.IsHexDigit('F')", true)]
        [InlineData("Char.IsHexDigit('f')", true)]
        [InlineData("Char.IsHexDigit('G')", false)]
        [InlineData("Char.IsUpper('A')", true)]
        [InlineData("Char.IsUpper('a')", false)]
        [InlineData("Char.IsLower('z')", true)]
        [InlineData("Char.IsControl('\\n')", true)]
        [InlineData("Char.IsControl('A')", false)]
        public void Evaluator_CharMember(string expr, bool expected)
        {
            var tree = SyntaxTree.Parse($"System.Console.WriteLine({expr})");
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }
    }
}
