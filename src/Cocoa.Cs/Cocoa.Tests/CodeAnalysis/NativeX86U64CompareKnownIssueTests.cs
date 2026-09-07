using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// u64 有序比较三后端回归（x86 修复后解锁，原 NativeX86U64CompareKnownIssueTests）。
    ///
    /// 修复定位：x86 64 位比较（EmitCmp64X86）高 32 位恒用有符号 Less/Greater，u64 无符号大值
    /// （高 32 ≥ 0x80000000 被视负）→ `u64 <`/`<=` 恒假。新增 LirOpCode.CmpU（u64 比较）承载
    /// 符号性，x86 高 32 位按无符号排序（Below/Above）。边界覆盖 0x8000000000000000 与全 1。
    /// </summary>
    public class X86U64OrderedCompareTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

function U64Le(x: u64): i32
{
    if x <= 9ul return 1
    return 2
}

function U64Lt(x: u64): i32
{
    if x < 10ul return 1
    return 2
}

function U64Gt(x: u64): i32
{
    if x > 0ul return 1
    return 2
}

function I64Le(x: i64): u64
{
    var a: u64 = 0
    if x <= 9
    {
        a = 5ul
    }
    return a
}

function I32Lt(x: i32): i32
{
    if x < 48 return 1
    return 2
}

function U64LeMax(x: u64): i32
{
    if x <= 0xFFFFFFFFFFFFFFFFul return 1
    return 2
}

function U64GtHalf(x: u64): i32
{
    if x > 0x7FFFFFFFFFFFFFFFul return 1
    return 2
}

function U64LtHalf(x: u64): i32
{
    if x < 0x8000000000000000ul return 1
    return 2
}

function Main()
{
    Console.WriteLine(U64Le(1ul).ToString())
    Console.WriteLine(U64Lt(1ul).ToString())
    Console.WriteLine(U64Gt(1ul).ToString())
    Console.WriteLine(I64Le(1).ToString())
    Console.WriteLine(I32Lt(45).ToString())
    Console.WriteLine(U64LeMax(0xFFFFFFFFFFFFFFFFul).ToString())
    Console.WriteLine(U64GtHalf(0x8000000000000000ul).ToString())
    Console.WriteLine(U64LtHalf(0x7FFFFFFFFFFFFFFFul).ToString())
    Console.WriteLine(U64GtHalf(1ul).ToString())
}";

        private const string Expected = "1\n1\n1\n5\n1\n1\n1\n1\n2\n";

        private static string RunNative(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-u64cmp", "u64cmp-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.EmitNative("u64cmp", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            return NativeEmitTests.Run(exePath).Replace("\r\n", "\n");
        }

        [Fact]
        public void X64_U64OrderedCompare_IsCorrect() => Assert.Equal(Expected, RunNative("windows-x64"));

        [Fact]
        public void X86_U64OrderedCompare_IsCorrect() => Assert.Equal(Expected, RunNative("windows-x86"));
    }
}