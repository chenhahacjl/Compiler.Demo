using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// StringBuilder 源码集成冒烟（6e-G7 ③a）：
    /// Runtime.StringFromChars syscall + O(n) ToString + 扩容。
    /// </summary>
    public class StringBuilderTests
    {
        private const string StringBuilderSource = @"
namespace System.Text
{
    public class StringBuilder
    {
        private _chars: char[]
        private _count: i32

        public constructor()
        {
            _chars = new char[16]
            _count = 0
        }

        public function Length(): i32
        {
            return _count
        }

        public function Append(s: string): StringBuilder
        {
            var i = 0
            while i < s.Length
            {
                EnsureCapacity(_count + 1)
                _chars[_count] = s[i]
                _count = _count + 1
                i = i + 1
            }

            return this
        }

        public function Clear(): void
        {
            _count = 0
        }

        public function ToString(): string
        {
            var chars = new char[_count]
            var i = 0
            while i < _count
            {
                chars[i] = _chars[i]
                i = i + 1
            }

            return Runtime.StringFromChars(chars)
        }

        private function EnsureCapacity(required: i32): void
        {
            if required <= _chars.Length
            {
                return
            }

            var newLen = _chars.Length * 2
            while newLen < required
            {
                newLen = newLen * 2
            }

            var grown = new char[newLen]
            var i = 0
            while i < _count
            {
                grown[i] = _chars[i]
                i = i + 1
            }

            _chars = grown
        }
    }
}
";

        [Fact]
        public void Evaluator_Append_And_Length()
        {
            var libTree = SyntaxTree.Parse(StringBuilderSource);
            var appTree = SyntaxTree.Parse("var sb = new StringBuilder() sb.Append(\"hello\") var len: i32 = sb.Length()");
            var compilation = Compilation.Create(libTree, appTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Evaluator_Append_Multiple()
        {
            var libTree = SyntaxTree.Parse(StringBuilderSource);
            var appTree = SyntaxTree.Parse(@"
var sb = new StringBuilder()
var i = 0
while i < 100
{
    sb.Append(""x"")
    i = i + 1
}
");
            var compilation = Compilation.Create(libTree, appTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }
    }
}
