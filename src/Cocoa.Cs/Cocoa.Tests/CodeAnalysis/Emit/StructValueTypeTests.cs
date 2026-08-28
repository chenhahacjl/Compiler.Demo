using System.Diagnostics;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit
{
    /// <summary>
    /// 6e-M26：struct 值类型（IL 值语义）——创建/字段读写/按值传参/返回/默认零值。
    /// IL-first 验证（native/evaluator 后置）。
    /// </summary>
    public class StructValueTypeTests
    {
        private static int _exeSeq;

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "Exception.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }

        private static (int ExitCode, string Stdout) EmitAndRun(string source, string name)
        {
            var coreDir = Path.Combine(RepoRoot(), "src", "Cocoa.SDK", "System.Core");
            var syntaxTrees = new[]
            {
                Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(coreDir, "Exception.co"))),
                Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source),
            };

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(
                "Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                syntaxTrees);

            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-struct-tests", $"{Environment.ProcessId:x}{Interlocked.Increment(ref _exeSeq):x3}-{name}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var diagnostics = compilation.Emit(
                name,
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                exePath,
                Cocoa.CodeAnalysis.Emit.IL.IlTarget.Parse("net9.0"));

            var diagText = string.Join("\n", diagnostics);
            if (diagText.Length > 0)
            {
                Assert.True(false, "Diagnostics:\n" + diagText);
            }
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            var combined = stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "");
            if (process.ExitCode != 0)
            {
                Assert.True(false, $"exit={process.ExitCode}\n{combined}");
            }

            return (process.ExitCode, combined);
        }

        [Fact]
        public void Struct_Create_And_FieldAccess()
        {
            var source = @"
struct Point
{
    public x: i32
    public y: i32
    public constructor(x: i32, y: i32)
    {
        this.x = x
        this.y = y
    }
}

function Main()
{
    var p = new Point(3, 4)
    Console.WriteLine(p.x)
    Console.WriteLine(p.y)
}";
            var (_, stdout) = EmitAndRun(source, "StructCreate");
            Assert.Equal("3\r\n4\r\n", stdout);
        }

        [Fact]
        public void Struct_PassByValue_DoesNotMutateCaller()
        {
            var source = @"
struct Point
{
    public x: i32
    public y: i32
    public constructor(x: i32, y: i32)
    {
        this.x = x
        this.y = y
    }
}

function AddOne(p: Point)
{
    p.x = p.x + 1
}

function Main()
{
    var p = new Point(3, 4)
    AddOne(p)
    Console.WriteLine(p.x)
}";
            var (_, stdout) = EmitAndRun(source, "StructPassByValue");
            Assert.Equal("3\r\n", stdout);
        }

        [Fact]
        public void Struct_ReturnValue()
        {
            var source = @"
struct Point
{
    public x: i32
    public y: i32
    public constructor(x: i32, y: i32)
    {
        this.x = x
        this.y = y
    }
}

function Make(a: i32, b: i32): Point
{
    return new Point(a, b)
}

function Main()
{
    var p = Make(7, 8)
    Console.WriteLine(p.x)
    Console.WriteLine(p.y)
}";
            var (_, stdout) = EmitAndRun(source, "StructReturn");
            Assert.Equal("7\r\n8\r\n", stdout);
        }

        [Fact]
        public void Struct_DefaultZero()
        {
            var source = @"
struct Point
{
    public x: i32
    public y: i32
    public constructor(x: i32, y: i32)
    {
        this.x = x
        this.y = y
    }
}

function Main()
{
    var p: Point
    Console.WriteLine(p.x)
    Console.WriteLine(p.y)
}";
            var (_, stdout) = EmitAndRun(source, "StructDefault");
            Assert.Equal("0\r\n0\r\n", stdout);
        }
    }
}
