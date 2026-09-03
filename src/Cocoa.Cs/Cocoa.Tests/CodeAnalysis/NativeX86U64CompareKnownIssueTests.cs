using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 已知缺陷锁定（不在本批修复范围）：native windows-x86 上 u64 的 &lt; / &lt;= 比较恒为假。
    ///
    /// 定位经过：P1c 下沉 Runtime.ParseInt64 后 x86 解析结果归 0 → 逐行打印得字符码与
    /// 数字 d 均正确、u64 acc 却不累加 → 探针二分得 u64 `&lt;=` 与 u64 `&lt;` 恒假，
    /// 而 u64 `&gt;`、i64 `&lt;=`、i32 `&lt;`/`&lt;=` 均正常。
    ///
    /// 预先存在：git stash 回 HEAD 后，纯 Cocoa 的 Int64.TryParse("12345") 在 x86 仍返回 false。
    /// 影响面：标准库中凡以 u64 做 &lt; / &lt;= 判断的逻辑在 x86 静默失效（Int64.TryParse 已是受害者；
    ///        Char.IsDigit 走 i32 字符码，不受影响）。
    /// 规避：Runtime.ParseInt64 改用 i32 字符码 48..57 判定，见 NumericSinkThreeBackendTests。
    /// 后续：native x86 修复 u64 有序比较后，把 RunX86 的期望串翻转为 x64 的并删去本类注释。
    /// </summary>
    public class NativeX86U64CompareKnownIssueTests
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

function Main()
{
    Console.WriteLine(U64Le(1ul).ToString())
    Console.WriteLine(U64Lt(1ul).ToString())
    Console.WriteLine(U64Gt(1ul).ToString())
    Console.WriteLine(I64Le(1).ToString())
    Console.WriteLine(I32Lt(45).ToString())
}";

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

        /// <summary>正确语义：u64 `&lt;=` 真、u64 `&lt;` 真、u64 `&gt;` 真、i64 `&lt;=` 与 i32 `&lt;` 正常。</summary>
        [Fact]
        public void X64_U64OrderedCompare_IsCorrect() => Assert.Equal("1\n1\n1\n5\n1\n", RunNative("windows-x64"));

        /// <summary>x86 现状：前两行 u64 `&lt;`/`&lt;=` 恒假（返回 2），`&gt;` 与 i64/i32 比较正常。</summary>
        [Fact]
        [Trait("Category", "KnownIssue")]
        public void X86_U64OrderedCompare_CurrentlyBroken() => Assert.Equal("2\n2\n1\n5\n1\n", RunNative("windows-x86"));
    }
}
