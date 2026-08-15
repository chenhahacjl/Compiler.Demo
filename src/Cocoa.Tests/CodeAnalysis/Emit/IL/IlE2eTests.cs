using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    public class IlE2eTests
    {
        private static string GetOutputPath(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-il-tests");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name + ".exe");
        }

        private static (int ExitCode, string Stdout) EmitAndRun(string source, string name, string? input = null)
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var exePath = GetOutputPath(name);
            var diagnostics = compilation.Emit(name, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath);

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            if (input != null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            return (process.ExitCode, stdout);
        }

        [Fact]
        public void Run_CocoaProgram_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function main()
{
    var sum = 0
    var i = 0
    while i < 5
    {
        sum = sum + i
        i = i + 1
    }
    print(sum)
    var name = input()
    print(""hello "" + name)
    print(sum > 10)
    var r = random(100)
    if r >= 0 && r < 100
    {
        print(""ok"")
    }
}", "e2e-builtins", "World");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\nhello World\r\nFalse\r\nok\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithUserFunctions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function add(a: int, b: int): int
{
    return a + b
}

function square(x: int): int
{
    return x * x
}

function greet(name: string): string
{
    return ""Hello, "" + name
}

function fib(n: int): int
{
    if n <= 1
    {
        return n
    }
    return fib(n - 1) + fib(n - 2)
}

function isPositive(n: int): bool
{
    return n > 0
}

function main()
{
    print(add(2, 3))
    print(square(add(1, 2)))
    print(greet(""Cocoa""))
    print(fib(10))
    print(isPositive(7))
    print(isPositive(0 - 3))
    print(add(fib(6), fib(7)))
}", "e2e-user-functions");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n9\r\nHello, Cocoa\r\n55\r\nTrue\r\nFalse\r\n21\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithControlFlow_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function main()
{
    var total = 0
    for i = 1 to 5
    {
        if i == 3
        {
            continue
        }
        total = total + i
    }
    print(total)

    var j = 0
    do
    {
        j = j + 1
    } while j < 3
    print(j)

    var m = 0
    for k = 1 to 10
    {
        if k > 2
        {
            break
        }
        m = m + k
    }
    print(m)

    var nested = 0
    var p = 2
    while p > 0
    {
        var q = p
        while q > 0
        {
            nested = nested + q
            q = q - 1
        }
        p = p - 1
    }
    print(nested)
}", "e2e-control-flow");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n3\r\n3\r\n4\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithWideCallAndLongConcat_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function sum10(a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int, i: int, j: int): int
{
    return a + b + c + d + e + f + g + h + i + j
}

function main()
{
    let name = ""Cocoa""
    var x = ""1""
    var y = ""2""
    print(""a"" + x + ""b"" + y + ""c"" + name)
    print(sum10(1, 2, 3, 4, 5, 6, 7, 8, 9, 10))
    print(name + ""!"")
}", "e2e-wide-call-long-concat");

            Assert.Equal(0, exitCode);
            Assert.Equal("a1b2cCocoa\r\n55\r\nCocoa!\r\n", stdout);
        }
    }
}
