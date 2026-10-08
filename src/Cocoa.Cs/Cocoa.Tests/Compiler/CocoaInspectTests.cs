using System.IO;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// M5：工具链 `cocoa inspect &lt;exe&gt;` e2e——native 产物内嵌 .cocoa 节，inspect 子命令读回打印。
    /// 编译产物（复用 EmitNative 含类/属性/字段）+ 子进程跑 inspect，断言输出含类型/方法/字段。
    /// </summary>
    public class CocoaInspectTests
    {
        private static readonly TargetPlatform X64 = new(TargetOS.Windows, Architecture.X64);

        private static string CompileNative(string name, string source)
        {
            var dir = CliTestRunner.NewTempDir("inspect-" + name);
            var exePath = Path.Combine(dir, name + ".exe");
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative(name, exePath, X64);
            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));
            return exePath;
        }

        [Fact]
        public void Inspect_PrintsTypesMethodsAndFields()
        {
            var exePath = CompileNative("inspect-oop", @"using System

public class Point
{
    field x: i32
    public constructor(x: i32)
    {
        this.x = x
    }

    public function GetX(): i32
    {
        return this.x
    }
}

function Main(): i32
{
    var p = new Point(7)
    return p.GetX() - 7
}");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("inspect \"" + exePath + "\"", Path.GetTempPath());

            Assert.True(string.IsNullOrEmpty(stderr), stderr);
            Assert.Equal(0, exitCode);
            Assert.Contains("// cocoa inspect", stdout);
            Assert.Contains("class Point", stdout);
            Assert.Contains("GetX", stdout);
            Assert.Contains("x", stdout);
            Assert.Contains("== top-level functions ==", stdout);
            Assert.Contains("Main", stdout);
        }

        [Fact]
        public void Inspect_PrintsAttrs()
        {
            var exePath = CompileNative("inspect-attr", @"using System

class ObsoleteAttribute extends Attribute
{
    public constructor(message: string) { }
}

[Obsolete(""已弃用"")]
public class Legacy
{
    public function Run(): i32
    {
        return 0
    }
}

function Main(): i32
{
    var l = new Legacy()
    return l.Run()
}");

            var (exitCode, stdout, _) = CliTestRunner.Run("inspect \"" + exePath + "\"", Path.GetTempPath());
            Assert.Equal(0, exitCode);
            Assert.Contains("== attrs ==", stdout);
            Assert.Contains("ObsoleteAttribute", stdout);
            Assert.Contains("== docs ==", stdout);
        }

        [Fact]
        public void Inspect_NonNativeFile_ReportsError()
        {
            var dir = CliTestRunner.NewTempDir("inspect-nonnative");
            var textPath = Path.Combine(dir, "note.txt");
            File.WriteAllText(textPath, "hello");

            var (exitCode, _, stderr) = CliTestRunner.Run("inspect \"" + textPath + "\"", Path.GetTempPath());
            Assert.Equal(1, exitCode);
            Assert.Contains("非有效 PE 镜像", stderr);
        }

        [Fact]
        public void Inspect_MissingFile_ReportsError()
        {
            var dir = CliTestRunner.NewTempDir("inspect-missing");
            var (exitCode, _, stderr) = CliTestRunner.Run("inspect \"" + Path.Combine(dir, "does-not-exist.exe") + "\"", Path.GetTempPath());
            Assert.Equal(1, exitCode);
            Assert.Contains("doesn't exist", stderr);
        }
    }
}