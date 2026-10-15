using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 9d：NativeImportValidator 签名自检（生成期失败快，不产运行期坏 exe）。
    /// 负例：浮点返回 / 浮点参数 / 字符串返回 → 编译期 Error 诊断。
    /// </summary>
    public class NativeImportValidatorTests
    {
        private static readonly TargetPlatform X64 = new(TargetOS.Windows, Architecture.X64);

        private static string[] Validate(string source)
        {
            var st = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(st);
            var diagnostics = compilation.EmitNative("import-neg", "imp-neg.exe", X64);
            return System.Linq.Enumerable.Select(diagnostics, d => d.Message).ToArray();
        }

        [Fact]
        public void FloatReturn_ReportsError()
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
}");
            Assert.True(messages.Any(m => m.Contains("浮点")), "got: " + string.Join(" | ", messages));
        }

        [Fact]
        public void FloatParameter_ReportsError()
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
}");
            Assert.True(messages.Any(m => m.Contains("浮点")), "got: " + string.Join(" | ", messages));
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
}");
            Assert.True(messages.Any(m => m.Contains("不受 native 后端支持")), "got: " + string.Join(" | ", messages));
        }
    }
}