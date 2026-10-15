using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// System.IO.Path 语义黑盒对拍：body（Evaluator/native）必须逐项与 BCL System.IO.Path 一致
    /// ——因为 IL 端 facade 直链 BCL，三端附带"dotnet 源码即 spec"约束（Windows 分支）。
    /// </summary>
    public class PathParityTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string Program(string expr) => @"
using System
using System.IO
function Main(): i32
{
    System.Console.WriteLine(" + expr + @")
    return 0
}";

        private static string Q(string s) => "\"" + s.Replace("\\", "\\\\") + "\"";

        private static string Paste(string expr)
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program(expr)));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                return writer.ToString().Replace("\r\n", "").Replace("\n", "");
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static void AssertParity(string call, string? expected)
        {
            var exp = expected ?? "";
            var actual = Paste(call);
            Assert.True(actual == exp, $"{call} => '{actual}'（body），BCL => '{exp}'");
        }

        [Fact]
        public void Combine_Parity()
        {
            AssertParity("System.IO.Path.Combine(" + Q("a") + ", " + Q("b") + ")", Path.Combine("a", "b"));
            AssertParity("System.IO.Path.Combine(" + Q("a") + ", \"\")", Path.Combine("a", ""));
            AssertParity("System.IO.Path.Combine(\"\", " + Q("b") + ")", Path.Combine("", "b"));
            AssertParity("System.IO.Path.Combine(\"\", \"\")", Path.Combine("", ""));
            AssertParity("System.IO.Path.Combine(" + Q("a") + ", " + Q("/b") + ")", Path.Combine("a", "/b"));
            AssertParity("System.IO.Path.Combine(" + Q("a") + ", " + Q("\\b") + ")", Path.Combine("a", "\\b"));
            AssertParity("System.IO.Path.Combine(" + Q("a/") + ", " + Q("b") + ")", Path.Combine("a/", "b"));
            AssertParity("System.IO.Path.Combine(" + Q("a\\") + ", " + Q("b") + ")", Path.Combine("a\\", "b"));
            AssertParity("System.IO.Path.Combine(" + Q("a/b") + ", " + Q("c") + ")", Path.Combine("a/b", "c"));
        }

        [Fact]
        public void GetFileName_Parity()
        {
            AssertParity("System.IO.Path.GetFileName(" + Q("a\\b\\c") + ")", Path.GetFileName("a\\b\\c"));
            AssertParity("System.IO.Path.GetFileName(" + Q("a/b/c") + ")", Path.GetFileName("a/b/c"));
            AssertParity("System.IO.Path.GetFileName(" + Q("a/b") + ")", Path.GetFileName("a/b"));
            AssertParity("System.IO.Path.GetFileName(" + Q("a/") + ")", Path.GetFileName("a/"));
            AssertParity("System.IO.Path.GetFileName(" + Q("/") + ")", Path.GetFileName("/"));
            AssertParity("System.IO.Path.GetFileName(" + Q("a") + ")", Path.GetFileName("a"));
            AssertParity("System.IO.Path.GetFileName(" + Q("") + ")", Path.GetFileName(""));
        }

        [Fact]
        public void GetExtension_Parity()
        {
            AssertParity("System.IO.Path.GetExtension(" + Q("a.txt") + ")", Path.GetExtension("a.txt"));
            AssertParity("System.IO.Path.GetExtension(" + Q("a.b.c") + ")", Path.GetExtension("a.b.c"));
            AssertParity("System.IO.Path.GetExtension(" + Q("a") + ")", Path.GetExtension("a"));
            AssertParity("System.IO.Path.GetExtension(" + Q("a.") + ")", Path.GetExtension("a."));
            AssertParity("System.IO.Path.GetExtension(" + Q(".gitignore") + ")", Path.GetExtension(".gitignore"));
            AssertParity("System.IO.Path.GetExtension(" + Q("dir/file.bak") + ")", Path.GetExtension("dir/file.bak"));
            AssertParity("System.IO.Path.GetExtension(" + Q("..txt") + ")", Path.GetExtension("..txt"));
            AssertParity("System.IO.Path.GetExtension(" + Q("dir/.hidden") + ")", Path.GetExtension("dir/.hidden"));
        }

        [Fact]
        public void GetFileNameWithoutExtension_Parity()
        {
            AssertParity("System.IO.Path.GetFileNameWithoutExtension(" + Q("a.txt") + ")", Path.GetFileNameWithoutExtension("a.txt"));
            AssertParity("System.IO.Path.GetFileNameWithoutExtension(" + Q("a.b.c") + ")", Path.GetFileNameWithoutExtension("a.b.c"));
            AssertParity("System.IO.Path.GetFileNameWithoutExtension(" + Q("a") + ")", Path.GetFileNameWithoutExtension("a"));
            AssertParity("System.IO.Path.GetFileNameWithoutExtension(" + Q(".gitignore") + ")", Path.GetFileNameWithoutExtension(".gitignore"));
            AssertParity("System.IO.Path.GetFileNameWithoutExtension(" + Q("a.b/") + ")", Path.GetFileNameWithoutExtension("a.b/"));
        }

        [Fact]
        public void GetDirectoryName_Parity()
        {
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("a/b/c") + ")", Path.GetDirectoryName("a/b/c"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("a/b") + ")", Path.GetDirectoryName("a/b"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("a") + ")", Path.GetDirectoryName("a"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("/a/b") + ")", Path.GetDirectoryName("/a/b"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("/a") + ")", Path.GetDirectoryName("/a"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("a/b/") + ")", Path.GetDirectoryName("a/b/"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("/") + ")", Path.GetDirectoryName("/"));
            AssertParity("System.IO.Path.GetDirectoryName(" + Q("a\\b\\c") + ")", Path.GetDirectoryName("a\\b\\c"));
        }
    }
}