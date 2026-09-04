using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit
{
    /// <summary>
    /// 6e-M26 Phase3：facade struct —— 映射 CO struct 到 BCL 值类型
    /// （类型/构造/成员调用重定向到 BCL；this 为 BCL 值类型，按托管指针传参）。
    /// 测试源将 facade struct 置于 namespace System（FullName 即 BCL 全名），文件内同名空间自动可见，无需 using。
    /// 覆盖：值语义构造（newobj BCL .ctor）、按值传入 BCL 方法、静态方法重定向到 BCL。
    /// </summary>
    public class FacadeStructTests
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

        private static (int ExitCode, string Stdout) EmitAndRun(string source, string name, string[]? extraReferences = null)
        {
            var coreDir = Path.Combine(RepoRoot(), "src", "Cocoa.SDK", "System.Core");
            var syntaxTrees = new[]
            {
                Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(coreDir, "Exception.co"))),
                Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source),
            };

            var references = new List<string>
            {
                typeof(object).Assembly.Location,
                typeof(System.Console).Assembly.Location,
            };
            if (extraReferences != null)
            {
                references.AddRange(extraReferences);
            }

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(
                "Main",
                references.ToArray(),
                syntaxTrees);

            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-facade-struct-tests", $"{Environment.ProcessId:x}{Interlocked.Increment(ref _exeSeq):x3}-{name}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var diagnostics = compilation.Emit(
                name,
                references.ToArray(),
                exePath,
                Cocoa.Targeting.IlTarget.Parse("net9.0"));

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
        public void FacadeStruct_Ctor_And_StaticMethodRedirect()
        {
            var source = @"
namespace System
{
    facade struct DateTime
    {
        public constructor(y: i32, m: i32, d: i32) {}
        public static function DaysInMonth(y: i32, m: i32): i32
        {
            return 0
        }
    }
}

function Main()
{
    var d = new DateTime(2024, 1, 1)
    Console.WriteLine(DateTime.DaysInMonth(2024, 2))
}";
            var (_, stdout) = EmitAndRun(source, "FacadeDateTime");
            // BCL DateTime.DaysInMonth(2024, 2) = 29（闰年 2 月）
            Assert.Equal("29\r\n", stdout);
        }

        [Fact]
        public void FacadeStruct_StaticMethodReturningStruct_PassByValue()
        {
            var source = @"
namespace System
{
    facade struct DateTime
    {
        public constructor(y: i32, m: i32, d: i32) {}
        public static function Compare(a: DateTime, b: DateTime): i32
        {
            return 0
        }
    }
}

function Main()
{
    var d1 = new DateTime(2024, 1, 1)
    var d2 = new DateTime(2024, 1, 1)
    Console.WriteLine(DateTime.Compare(d1, d2))
}";
            // 两个相等 DateTime 经 BCL Guid.Compare 等价路径返回 0
            var (_, stdout) = EmitAndRun(source, "FacadeDateTimeEqual");
            Assert.Equal("0\r\n", stdout);
        }

        [Fact]
        public void FacadeStruct_PassByValue_ToBclMethod()
        {
            var source = @"
namespace System
{
    facade struct DateTime
    {
        public constructor(y: i32, m: i32, d: i32) {}
        public static function Compare(a: DateTime, b: DateTime): i32
        {
            return 0
        }
    }
}

function Main()
{
    var d1 = new DateTime(2024, 1, 1)
    var d2 = new DateTime(2024, 1, 2)
    Console.WriteLine(DateTime.Compare(d1, d2))
}";
            var (_, stdout) = EmitAndRun(source, "FacadeDateTimeCompare");
            Assert.Equal("-1\r\n", stdout);
        }

        [Fact]
        public void FacadeStruct_InstanceMethodRedirect()
        {
            var source = @"
namespace System
{
    facade struct DateTime
    {
        public constructor(y: i32, m: i32, d: i32) {}
        public function CompareTo(other: DateTime): i32 { return 0 }
    }
}

function Main()
{
    var d1 = new DateTime(2024, 1, 1)
    var d2 = new DateTime(2024, 1, 2)
    Console.WriteLine(d1.CompareTo(d2))
}";
            // BCL DateTime.CompareTo 返回 -1（d1 < d2）
            var (_, stdout) = EmitAndRun(source, "FacadeDateTimeCompareTo");
            Assert.Equal("-1\r\n", stdout);
        }

        [Fact]
        public void FacadeStruct_PropertyGetRedirect()
        {
            var source = @"
namespace System
{
    facade struct DateTime
    {
        public constructor(y: i32, m: i32, d: i32) {}
        public property Ticks: i64 { get }
    }
}

function Main()
{
    var d = new DateTime(2024, 1, 1)
    Console.WriteLine(d.Ticks)
}";
            var (_, stdout) = EmitAndRun(source, "FacadeDateTimeTicks");
            Assert.Equal(new DateTime(2024, 1, 1).Ticks.ToString() + "\r\n", stdout);
        }

        [Fact]
        public void FacadeStruct_PropertyGetSetRedirect()
        {
            var source = @"
namespace System.Numerics
{
    facade struct Vector3
    {
        public constructor(x: f32, y: f32, z: f32) {}
        public property X: f32 { get set }
    }
}

function Main()
{
    var v = new Vector3(1.0f, 2.0f, 3.0f)
    v.X = 9.0f
    Console.WriteLine(v.X)
}";
            var (_, stdout) = EmitAndRun(source, "FacadeVector3X", new[] { typeof(System.Numerics.Vector3).Assembly.Location });
            Assert.Equal("9\r\n", stdout);
        }
    }
}
