using Cocoa.CodeAnalysis.Emit.IL;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit
{
    /// <summary>
    /// IL 发射确定性回归：同一源码在进程内多次编译，产出的 dll 必须逐字节一致。
    /// 回归 6e-M26：BoundProgram.Functions（ImmutableDictionary&lt;FunctionSymbol,…&gt;）的枚举顺序受
    /// FunctionSymbol 默认引用 GetHashCode（进程随机）影响，导致方法体/MemberRef/#US 注册顺序跨运行
    /// 变化（观测：ldstr token 0x70000003 ↔ 0x70000001），构建不可复现。修复后迭代须排序。
    /// </summary>
    public class IlEmitDeterminismTests
    {
        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Collections", "List.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir!;
        }

        private static Cocoa.CodeAnalysis.Syntax.SyntaxTree ParseSdkFile(string relative)
        {
            return Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.SDK", relative)));
        }

        private const string Source = @"using System
using System.Collections.Generic

class Box
{
    private _value: i32
    public property Value: i32
    {
        get
        {
            return _value
        }
    }

    public constructor(value: i32)
    {
        _value = value
    }

    public function Add(other: i32): i32
    {
        return _value + other
    }

    public function AddScaled(other: i32, scale: i32): i32
    {
        return (_value + other) * scale
    }
}

function helperZig(a: i32): i32
{
    return a * 7
}

function helperAlpha(name: string): string
{
    return name
}

function helperBeta(x: f64): f64
{
    if x > 3.0
    {
        return Math.Sqrt(x)
    }
    return 0.0
}

function Main()
{
    var b = new Box(5)
    var s = 0
    try
    {
        s = b.Add(2)
        s = b.AddScaled(2, 3)
        throw new Exception(""boom"")
    }
    catch (e: Exception)
    {
        s = e.Message.Length
    }
    finally
    {
        s = s + 1
    }
    Console.WriteLine(s)
    Console.WriteLine(helperZig(6))
    Console.WriteLine(helperAlpha(""world""))
    Console.WriteLine(helperBeta(16.0))
    Console.WriteLine(b.Value)
}";

        [Fact]
        public void Emit_IsByteWiseDeterministic()
        {
            var hashes = new string[3];
            for (var i = 0; i < hashes.Length; i++)
            {
                var syntaxTrees = new[] { ParseSdkFile("System.Collections/Enumerable.co"), ParseSdkFile("System.Core/Exception.co"), Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(Source) };
                var compilation = Cocoa.CodeAnalysis.Compilation.Create("Main", References, syntaxTrees);
                var exePath = Path.Combine(Path.GetTempPath(), "cocoa-det-tests", $"det{i}.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                var diagnostics = compilation.Emit("Main", References, exePath, IlTarget.Parse("net9.0"));
                Assert.Empty(string.Join("\n", diagnostics));
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(exePath);
                hashes[i] = Convert.ToHexString(sha.ComputeHash(fs));
                fs.Close();
                File.Delete(exePath);
            }

            Assert.All(hashes, h => Assert.Equal(hashes[0], h));
        }

        private const string UncaughtSource = @"using System
using System.Collections.Generic

function Main()
{
    throw new Exception(""fatal"")
}";

        /// <summary>
        /// 未捕获异常必须是真实 System.Exception（facade）而非 RuntimeWrappedException，且多次全新编译稳定。
        /// 回归 6e-M26：确定性排序 + MVID 固定后，System.Exception 构造器 MemberRef 不再受发射顺序影响。
        /// </summary>
        [Fact]
        public void Uncaught_IsSystemException_NeverRuntimeWrapped()
        {
            for (var i = 0; i < 5; i++)
            {
                var syntaxTrees = new[] { ParseSdkFile("System.Collections/Enumerable.co"), ParseSdkFile("System.Core/Exception.co"), Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(UncaughtSource) };
                var compilation = Cocoa.CodeAnalysis.Compilation.Create("Main", References, syntaxTrees);
                var exePath = Path.Combine(Path.GetTempPath(), "cocoa-det-tests", $"uncaught-{Environment.ProcessId}-{i}.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                var diagnostics = compilation.Emit("Main", References, exePath, IlTarget.Parse("net9.0"));
                Assert.Empty(string.Join("\n", diagnostics));

                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{exePath}\"")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(15000);
                File.Delete(exePath);

                Assert.NotEqual(0, process.ExitCode);
                Assert.DoesNotContain("RuntimeWrappedException", stdout + "\n" + stderr);
            }
        }
    }
}