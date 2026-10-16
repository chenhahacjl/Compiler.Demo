using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// 6e-M19 M4：native 对象模型落地——Object 成员面（ToString/GetHashCode/Equals/GetType +
    /// 静态 Equals/ReferenceEquals）在 native 后端编译放行（原 M2-c"未实现"守卫移除）。
    /// 运行时语义由 <see cref="NativeOopEndToEndTests"/> 双平台 e2e 锁定；此处锁定编译期无诊断。
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
        public void Native_ObjectInstanceMemberCall_CompilesWithoutDiagnostics()
        {
            var diagnostics = EmitNative(@"using System

public class Point
{
}

function Main(): i32
{
    var p = new Point()
    var s = p.ToString()
    if s != ""Point"" return 1
    return 0
}");

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Native_ObjectStaticEquals_CompilesWithoutDiagnostics()
        {
            var diagnostics = EmitNative(@"using System

function Main(): i32
{
    if !Object.Equals(""a"", ""a"") return 1
    if Object.ReferenceEquals(1, 1) return 2
    return 0
}");

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Native_FacadeToString_StillAllowed()
        {
            // facade 路由（Runtime.Int32ToString 已下沉为纯 Cocoa 带体 static 方法）不受影响
            var diagnostics = EmitNative(@"
function Main(): i32
{
    var s = 42.ToString()
    if s != ""42"" return 1
    return 0
}");

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Native_AnyReceiver_ObjectFace_BoxesValue()
        {
            // any 接收者装箱表示：值类型 box 后走 Object 面（ToString 输出盒类型名），与三后端一致
            var source = @"using System

function Pick(): object
{
    return 5
}

function Main(): i32
{
    var s = Pick().ToString()
    Console.WriteLine(s)
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-native-any-receiver.exe");
            var diagnostics = compilation.EmitNative("native-any-receiver", exePath);
            Assert.Empty(diagnostics);

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            using var output = new System.IO.MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Assert.True(process.WaitForExit(15000));
            outputTask.Wait();
            var stdout = System.Text.Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("5\n", stdout);
        }
    }
}
