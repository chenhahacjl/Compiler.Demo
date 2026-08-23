using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 6e-M19 M2-c：native 后端 Object 成员面调用守卫——编译期明确"未实现"诊断（M4 落地后移除）。
    /// </summary>
    public class NativeObjectFaceTests
    {
        private static ImmutableArray<Diagnostic> EmitNative(string source)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            return compilation.EmitNative("native-object-face", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-native-object-face.exe"));
        }

        [Fact]
        public void Native_ObjectInstanceMemberCall_ReportsNotImplemented()
        {
            var diagnostics = EmitNative(@"using System

public class Point
{
}

function Main(): i32
{
    var p = new Point()
    var s = p.ToString()
    return 0
}");

            Assert.Contains(diagnostics, d => d.Message.Contains("System.Object 成员方法"));
        }

        [Fact]
        public void Native_ObjectStaticEquals_ReportsNotImplemented()
        {
            var diagnostics = EmitNative(@"using System

function Main(): i32
{
    if !Object.Equals(1, 1) return 1
    return 0
}");

            Assert.Contains(diagnostics, d => d.Message.Contains("System.Object 成员方法"));
        }

        [Fact]
        public void Native_FacadeToString_StillAllowed()
        {
            // facade 路由（Runtime.Int32ToString syscall）不受守卫影响
            var diagnostics = EmitNative(@"
function Main(): i32
{
    var s = 42.ToString()
    if s != ""42"" return 1
    return 0
}");

            Assert.Empty(diagnostics);
        }
    }
}
