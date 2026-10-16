using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 9d：NativeImportValidator 签名自检（生成期失败快，不产运行期坏 exe）。
    /// 负例：浮点返回 / 浮点参数（x86）/ 字符串返回 → 编译期 Error 诊断；
    /// 正例：x64 浮点返回/参数（N4 经 XMM0）放行。
    /// </summary>
    public class NativeImportValidatorTests
    {
        private static readonly TargetPlatform X64 = new(TargetOS.Windows, Architecture.X64);
        private static readonly TargetPlatform X86 = new(TargetOS.Windows, Architecture.X86);

        private static string[] Validate(string source, TargetPlatform platform)
        {
            var st = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(st);
            var diagnostics = compilation.EmitNative("import-neg", "imp-neg.exe", platform);
            return System.Linq.Enumerable.Select(diagnostics, d => d.Message).ToArray();
        }

        [Fact]
        public void FloatReturn_ReportsErrorOnX86()
        {
            var messages = Validate(@"class K
{
    import kernel32.dll
    {
        static stdcall function SqrtF(x: f32): f32
    }
}

function Main(): i32
{
    return 0
}", X86);
            Assert.True(messages.Any(m => m.Contains("不受 native 后端支持")), "got: " + string.Join(" | ", messages));
        }

        [Fact]
        public void FloatParameter_ReportsErrorOnX86()
        {
            var messages = Validate(@"class K
{
    import kernel32.dll
    {
        static stdcall function SetF(x: f64): i32
    }
}

function Main(): i32
{
    return 0
}", X86);
            Assert.True(messages.Any(m => m.Contains("浮点")), "got: " + string.Join(" | ", messages));
        }

        [Fact]
        public void FloatSignature_AllowedOnX64()
        {
            // N4：x64 浮点返回/参数经 XMM0，签名自检放行（不再报"浮点不受支持"）。
            var messages = Validate(@"class K
{
    import kernel32.dll
    {
        static stdcall function SqrtF(x: f32): f32
        static stdcall function SetD(x: f64): i32
    }
}

function Main(): i32
{
    return 0
}", X64);
            Assert.DoesNotContain(messages, m => m.Contains("浮点") || m.Contains("不受 native 后端支持"));
        }

        [Fact]
        public void StringReturn_ReportsError()
        {
            var messages = Validate(@"class K
{
    import kernel32.dll
    {
        static stdcall function GetName(): string
    }
}

function Main(): i32
{
    return 0
}", X64);
            Assert.True(messages.Any(m => m.Contains("不受 native 后端支持")), "got: " + string.Join(" | ", messages));
        }
    }
}