using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>x86 I64 一般参数高位（A2 复现/回归）。</summary>
    public class NativeI64ParamTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

function Add64(a: i64, b: i64): i64 { return a + b }
function Id64(a: i64): i64 { return a }
function Add3(a: i64, b: i64, c: i64): i64 { return a + b + c }
function Add5(a: i64, b: i64, c: i64, d: i64, e: i64): i64 { return a + b + c + d + e }

function Main(): i32
{
    var big: i64 = 4294967297
    System.Console.WriteLine(Add64(big, 2) == 4294967299)
    System.Console.WriteLine(Id64(big) == 4294967297)
    System.Console.WriteLine(Id64(5) == 5)
    System.Console.WriteLine(Add3(big, 2, big) == 8589934596)
    System.Console.WriteLine(Add5(big, 1, big, 1, big) == 12884901893)
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\n";

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_I64Param(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-i64param", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "i64p-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("i64psdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}