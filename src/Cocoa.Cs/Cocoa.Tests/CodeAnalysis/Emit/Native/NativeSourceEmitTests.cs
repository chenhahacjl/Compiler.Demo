using System.IO;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    public class NativeSourceEmitTests
    {
        private const string X64 = "windows-x64";
        private const string X86 = "windows-x86";

        private static string GetExePath(string name, string target)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-tests");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name + "-" + target + ".exe");
        }

        private static string CompileAndRun(string source, string name, string target, string? input = null, int expectedExitCode = 0, bool useCs = false)
        {
            TargetPlatform.TryParse(target, out var platform);
            var syntaxTree = useCs ? SyntaxTree.ParseCs(source) : SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath(name, target);

            var diagnostics = compilation.EmitNative("test", exePath, platform);

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            return NativeEmitTests.Run(exePath, input, expectedExitCode);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_EmptyMain(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
}", "dbg-empty", target);

            Assert.Equal("", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_VarOnly(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var x = 0
}", "dbg-var", target);

            Assert.Equal("", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_NoExit(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var x = 0
    x = 1
}", "dbg-noexit", target);

            Assert.Equal("", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_PrintInt(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(42)
}", "dbg-int", target);

            Assert.Equal("42\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_DefaultInitializedVariables(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a: i32
    var b: bool
    var d: f64
    var c: char
    var by: u8
    var s: string
    Console.WriteLine(a)
    Console.WriteLine(b)
    Console.WriteLine(d)
    Console.WriteLine(i32(c))
    Console.WriteLine(i32(by))
    Console.WriteLine(s == s)
    const k: i32 = 7
    Console.WriteLine(k)
}", "dbg-default", target);

            Assert.Equal("0\r\nFalse\r\n0\r\n0\r\n0\r\nTrue\r\n7\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_PrintString(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(""hi"")
}", "dbg-str", target);

            Assert.Equal("hi\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_PrintsExpressions(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(""Hello, World!"")
    Console.WriteLine(42)
    Console.WriteLine(7 * 6)
    Console.WriteLine(1 + 2 * 3)
    Console.WriteLine((1 + 2) * 3)
    Console.WriteLine(true)
    Console.WriteLine(false)
}", "src-print-expressions", target);

            Assert.Equal("Hello, World!\r\n42\r\n42\r\n7\r\n9\r\nTrue\r\nFalse\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_VariablesAndAssignment(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var x = 10
    x = x + 5
    Console.WriteLine(x)
    var y = 3
    y = x * y
    Console.WriteLine(y)
    var s = ""foo""
    s = s + ""bar""
    Console.WriteLine(s)
}", "src-variables", target);

            Assert.Equal("15\r\n45\r\nfoobar\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_IfStatement(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var x = 10
    if x > 5
    {
        Console.WriteLine(""big"")
    }
    if x > 20
    {
        Console.WriteLine(""huge"")
    }
    else
    {
        Console.WriteLine(""small"")
    }
}", "src-if", target);

            Assert.Equal("big\r\nsmall\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_UserFunctions(string target)
        {
            var output = CompileAndRun(@"using System

function add(a: i32, b: i32): i32
{
    return a + b
}

function square(x: i32): i32
{
    return x * x
}

function Main()
{
    Console.WriteLine(add(3, 4))
    Console.WriteLine(square(5))
    Console.WriteLine(square(add(2, 3)))
    var nested = add(square(2), square(3))
    Console.WriteLine(nested)
}", "src-user-functions", target);

            Assert.Equal("7\r\n25\r\n25\r\n13\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Recursion(string target)
        {
            var output = CompileAndRun(@"using System

function factorial(n: i32): i32
{
    if n <= 1
    {
        return 1
    }
    return n * factorial(n - 1)
}

function fibonacci(n: i32): i32
{
    if n <= 1
    {
        return n
    }
    return fibonacci(n - 1) + fibonacci(n - 2)
}

function Main()
{
    Console.WriteLine(factorial(5))
    Console.WriteLine(factorial(10))
    Console.WriteLine(fibonacci(10))
}", "src-recursion", target);

            Assert.Equal("120\r\n3628800\r\n55\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_WhileAndBreakContinue(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var i = 0
    while true
    {
        i += 1
        if i == 3
        {
            break
        }
        if i == 1
        {
            continue
        }
        Console.WriteLine(i)
    }
    Console.WriteLine(""done"")
}", "src-while", target);

            Assert.Equal("2\r\ndone\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ForAndDoWhile(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    for var j = 0 to 4
    {
        var even = j / 2 * 2 == j
        if !even
        {
            continue
        }
        Console.WriteLine(j)
    }
    Console.WriteLine(""x"")
    var k = 0
    do
    {
        k += 1
        Console.WriteLine(k)
    }
    while k < 3
}", "src-for-do", target);

            Assert.Equal("0\r\n2\r\n4\r\nx\r\n1\r\n2\r\n3\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_RangeFor_DescendingStep(string target)
        {
            // Y-A4-1：range-for 负常量 step（降序）端到端
            var output = CompileAndRun(@"using System

function Main()
{
    var total = 0
    for var i = 10 to 1 step -1
    {
        total = total + i
    }
    Console.WriteLine(total)

    var evens = 0
    for var i = 10 to 1 step -2
    {
        evens = evens + i
    }
    Console.WriteLine(evens)

    var skipped = 0
    for var i = 0 to 5 step -1
    {
        skipped = skipped + 1
    }
    Console.WriteLine(skipped)
}", "src-range-for-descending", target);

            Assert.Equal("55\r\n30\r\n0\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CSStyleFor_PostfixIncrement(string target)
        {
            var output = CompileAndRun(@"using System;

public static void Main()
{
    var sum = 0;
    for (var i = 0; i < 5; i++)
    {
        sum = sum + i;
    }
    Console.WriteLine(sum);
    var j = 10;
    j--;
    Console.WriteLine(j);
    j++;
    Console.WriteLine(j);
    var total = 0;
    for (;;)
    {
        total = total + 1;
        if (total == 3)
        {
            break;
        }
    }
    Console.WriteLine(total);
    var k = 0;
    for (; k < 4; k = k + 1)
    {
        if (k == 2)
        {
            continue;
        }
        Console.WriteLine(k);
    }
}", "src-cstyle-for", target, useCs: true);

            Assert.Equal("10\r\n9\r\n10\r\n3\r\n0\r\n1\r\n3\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ModuloAndShift(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(7 % 3)
    Console.WriteLine(-7 % 3)
    Console.WriteLine(10 % 2)
    Console.WriteLine(1 << 4)
    Console.WriteLine(8 >> 1)
    Console.WriteLine(-8 >> 1)
    var x = 10
    x %= 3
    Console.WriteLine(x)
    x = 1
    x <<= 4
    Console.WriteLine(x)
    x = -16
    x >>= 2
    Console.WriteLine(x)
    var sum = 0
    for var i = 1 to 5
    {
        if i % 2 == 0
        {
            continue
        }
        sum = sum + i
    }
    Console.WriteLine(sum)
}", "src-modulo-shift", target);

            Assert.Equal("1\r\n-1\r\n0\r\n16\r\n4\r\n-4\r\n1\r\n16\r\n-4\r\n9\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ConditionalAndPrefix(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a = 5
    var b = 10
    Console.WriteLine(a > b ? a : b)
    Console.WriteLine(1 < 2 ? 3 + 4 : 5 + 6)
    var i = 1
    i = ++i
    Console.WriteLine(i)
    i = --i
    Console.WriteLine(i)
    var n = 7
    Console.WriteLine(n % 2 == 0 ? ""even"" : ""odd"")
    var sum = 0
    for var j = 1 to 5
    {
        sum = sum + (j % 2 == 0 ? 10 : j)
    }
    Console.WriteLine(sum)
}", "src-ternary-prefix", target);

            Assert.Equal("10\r\n7\r\n2\r\n1\r\nodd\r\n29\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ModuloByZero(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(1 % 0)
}", "src-modulo-zero", target, expectedExitCode: 1);

            Assert.Equal("error: division by zero\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CompoundAssignment(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var x = 0
    x += 5
    Console.WriteLine(x)
    x *= 3
    Console.WriteLine(x)
    x -= 7
    Console.WriteLine(x)
    x /= 4
    Console.WriteLine(x)
}", "src-compound-assignment", target);

            Assert.Equal("5\r\n15\r\n8\r\n2\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_StringConcatAndCompare(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var s = ""foo""
    var t = ""bar""
    Console.WriteLine(s + t)
    Console.WriteLine(s + ""!"" + t)
    Console.WriteLine(s == s)
    Console.WriteLine(s == t)
    Console.WriteLine(s != t)
}", "src-strings", target);

            Assert.Equal("foobar\r\nfoo!bar\r\nTrue\r\nFalse\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_StringParameter(string target)
        {
            var output = CompileAndRun(@"using System

function greet(name: string): string
{
    return ""Hello, "" + name
}

function Main()
{
    var who = ""Cocoa""
    Console.WriteLine(greet(who))
    Console.WriteLine(greet(""World""))
}", "src-string-parameter", target);

            Assert.Equal("Hello, Cocoa\r\nHello, World\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_NegativeNumbers(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(-5)
    Console.WriteLine(0 - 7)
    var n = -3
    Console.WriteLine(0 - n)
    var m = -2 * 3
    Console.WriteLine(m)
    Console.WriteLine(!true)
    Console.WriteLine(!false)
}", "src-negative", target);

            Assert.Equal("-5\r\n-7\r\n3\r\n-6\r\nFalse\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_LogicalOperators(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a = true && false
    Console.WriteLine(a)
    var b = true || false
    Console.WriteLine(b)
    var c = (1 < 2) && (3 > 2)
    Console.WriteLine(c)
    var t = true
    var f = false
    Console.WriteLine(t && f)
    Console.WriteLine(t && true)
    Console.WriteLine(t || f)
    Console.WriteLine(f || f)
}", "src-logical", target);

            Assert.Equal("False\r\nTrue\r\nTrue\r\nFalse\r\nTrue\r\nTrue\r\nFalse\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Input(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var s = Console.ReadLine()
    Console.WriteLine(s)
}", "src-input", target, input: "Hello\n");

            Assert.Equal("Hello\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Random(string target)
        {
            for (var i = 0; i < 5; i++)
            {
                var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(Runtime.Random(100) < 100)
}", "src-random", target);

                Assert.Equal("True\r\n", output);
            }
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_SyscallMemberCall_Print(string target)
        {
            var output = CompileAndRun(@"using System

class Runtime
{
    syscall function WriteLine(text: string): void
}

function Main()
{
    Runtime.WriteLine(""hello syscall"")
}", "src-syscall-print", target);

            Assert.Equal("hello syscall\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_SyscallMemberCall_Random(string target)
        {
            var output = CompileAndRun(@"using System

class Runtime
{
    syscall function Random(max: i32): i32
}

function Main()
{
    var r = Runtime.Random(100)
    Console.WriteLine(r < 100)
}", "src-syscall-random", target);

            Assert.Equal("True\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Builtin_SleepTickCount(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var t0 = Runtime.TickCount()
    Runtime.Sleep(1)
    var t1 = Runtime.TickCount()
    Console.WriteLine(t1 >= t0)
}", "src-sleep-now", target);

            Assert.Equal("True\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Division(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(42 / 7)
    Console.WriteLine(-42 / 7)
    Console.WriteLine(42 / -7)
    Console.WriteLine(-42 / -7)
}", "src-division", target);

            Assert.Equal("6\r\n-6\r\n-6\r\n6\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_DivisionByZero(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(1 / 0)
}", "src-division-by-zero", target, expectedExitCode: 1);

            Assert.Equal("error: division by zero\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_ReadWriteAndLength(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a = new i32[3] {10, 20, 30}
    a[1] = 99
    Console.WriteLine(a[0])
    Console.WriteLine(a[1])
    Console.WriteLine(a[2])
    Console.WriteLine(a.Length)
}", "src-array", target);

            Assert.Equal("10\r\n99\r\n30\r\n3\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_BoolElements(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var b = new bool[2]
    b[0] = true
    b[1] = false
    Console.WriteLine(b[0])
    Console.WriteLine(b[1])
}", "src-array-bool", target);

            Assert.Equal("True\r\nFalse\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_IndexInLoop(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a = new i32[5]
    var i = 0
    while i < 5
    {
        a[i] = i * 10
        i = i + 1
    }
    var sum = 0
    i = 0
    while i < 5
    {
        sum = sum + a[i]
        i = i + 1
    }
    Console.WriteLine(sum)
}", "src-array-loop", target);

            Assert.Equal("100\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Foreach_OverArrays(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var arr = new i32[] {1, 2, 3}
    foreach (var x in arr)
    {
        Console.WriteLine(x)
    }
    var sum = 0
    foreach (var x in arr)
    {
        sum = sum + x
    }
    Console.WriteLine(sum)
    var doubles: f64[] = new f64[] {1.5, 2.5}
    foreach (var d in doubles)
    {
        Console.WriteLine(d)
    }
    var names = new string[] {""a"", ""b""}
    foreach (var n in names)
    {
        Console.WriteLine(n)
    }
}", "src-foreach-array", target);

            Assert.Equal("1\r\n2\r\n3\r\n6\r\n1.5\r\n2.5\r\na\r\nb\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Foreach_OverString_And_BreakContinue(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var s = ""abc""
    foreach (var c in s)
    {
        Console.WriteLine(c)
    }
    var arr = new i32[] {1, 2, 3, 4}
    foreach (var x in arr)
    {
        if x == 3 continue
        if x == 4 break
        Console.WriteLine(x)
    }
    var result = 0
    foreach (var i in arr)
    {
        foreach (var j in arr)
        {
            if j == 2 continue
            result = result + i * j
        }
    }
    Console.WriteLine(result)
}", "src-foreach-string", target);

            Assert.Equal("a\r\nb\r\nc\r\n1\r\n2\r\n80\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Switch(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var x = 2
    switch (x)
    {
        case 1:
        {
            Console.WriteLine(""one"")
            break
        }
        case 2:
        {
            Console.WriteLine(""two"")
            break
        }
        default:
        {
            Console.WriteLine(""other"")
            break
        }
    }
    switch (x)
    {
        case 1:
        case 2:
        {
            Console.WriteLine(""low"")
            break
        }
        default:
        {
            Console.WriteLine(""high"")
            break
        }
    }
    switch (x)
    {
        case 1:
        {
            Console.WriteLine(""one"")
            break
        }
        case 2 when false:
        {
            Console.WriteLine(""two-when"")
            break
        }
        default:
        {
            Console.WriteLine(""default"")
            break
        }
    }
    var s = ""b""
    switch (s)
    {
        case ""a"":
        {
            Console.WriteLine(""A"")
            break
        }
        case ""b"":
        {
            Console.WriteLine(""B"")
            break
        }
        default:
        {
            Console.WriteLine(""Z"")
            break
        }
    }
    var i = 0
    var sum = 0
    while i < 5
    {
        switch (i)
        {
            case 1:
            {
                i = i + 1
                continue
            }
            case 3:
            {
                break
            }
        }
        sum = sum + i
        i = i + 1
    }
    Console.WriteLine(sum)
}", "src-switch", target);

            Assert.Equal("two\r\nlow\r\ndefault\r\nB\r\n9\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_OutOfBounds(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a = new i32[2]
    a[0] = 1
    a[1] = 2
    Console.WriteLine(a[5])
}", "src-array-oob", target, expectedExitCode: 1);

            Assert.Equal("error: array index out of range\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_JaggedArray_ReportsError(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var syntaxTree = SyntaxTree.Parse(@"
function Main()
{
    var rows = new i32[2]
    var row = new i32[2] {5, 6}
    rows[0] = row
}");
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("test", GetExePath("src-array-jagged", target), platform);

            Assert.Contains(diagnostics, d => d.Message == "Cannot convert type 'int[]' to 'int'.");
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_String_IndexLengthAndSubstring(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var s = ""hello""
    Console.WriteLine(s.Length)
    Console.WriteLine(s[0])
    Console.WriteLine(i32(s[1]))
    var c = s[2]
    Console.WriteLine(c)
    Console.WriteLine(char(97))
    Console.WriteLine(s.substring(1, 3))
    Console.WriteLine(s.substring(1, 3) + ""!"")
}", "src-string-index", target);

            Assert.Equal("5\r\nh\r\n101\r\nl\r\na\r\nell\r\nell!\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CharArray(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var a = new char[2] {'x', 'y'}
    a[0] = 'z'
    Console.WriteLine(a[0])
    Console.WriteLine(a[1])
}", "src-char-array", target);

            Assert.Equal("z\r\ny\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_String_IndexOutOfBounds(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var s = ""abc""
    Console.WriteLine(s[9])
}", "src-string-oob", target, expectedExitCode: 1);

            Assert.Equal("error: array index out of range\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Substring_InvalidArguments(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var s = ""abc""
    Console.WriteLine(s.substring(1, 99))
}", "src-substring-invalid", target, expectedExitCode: 1);

            Assert.Equal("error: invalid substring arguments\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Enum_EndToEnd(string target)
        {
            var output = CompileAndRun(@"using System

public enum Color { Red, Green, Blue }
public enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 }
function f(c: Color): i32 { return i32(c) }
function Main()
{
    var c = Color.Green
    Console.WriteLine(i32(c))
    Console.WriteLine(i32(HttpStatus.NotFound))
    Console.WriteLine(c == Color.Green)
    Console.WriteLine(c == Color.Red)
    Console.WriteLine(i32(f(Color.Blue)))
    Console.WriteLine(i32(Color(99)) == 99)
}", "src-enum", target);

            Assert.Equal("1\r\n404\r\nTrue\r\nFalse\r\n2\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_EnumArray(string target)
        {
            var output = CompileAndRun(@"using System

public enum Color { Red, Green, Blue }
function Main()
{
    var a = new Color[2] {Color.Red, Color.Green}
    Console.WriteLine(i32(a[0]))
    Console.WriteLine(i32(a[1]))
    a[1] = Color.Blue
    Console.WriteLine(i32(a[1]))
}", "src-enum-array", target);

            Assert.Equal("0\r\n1\r\n2\r\n", output);
        }

        [Fact]
        public void NativeSource_Interface_ReportsUnsupported()
        {
            var syntaxTree = SyntaxTree.Parse(@"using System

public interface IShape
{
    function Area(): i32
}

public class Circle extends IShape
{
    public function Area(): i32
    {
        return 1
    }
}

function Main()
{
    var s: IShape = new Circle()
    Console.WriteLine(s.Area())
}");
            var compilation = Compilation.Create(syntaxTree);
            TargetPlatform.TryParse(X64, out var platform);
            var diagnostics = compilation.EmitNative("test", GetExePath("native-interface", X64), platform);
            Assert.NotEmpty(diagnostics);
            // 6e-M19 M4：接口分派未随对象模型落地，仍明确拒绝（原"实例成员类拒绝"门禁已移除）
            Assert.Contains(diagnostics, d => d.Message.Contains("interface 'IShape' 暂不支持 native 后端"));
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CSharpStyleTopLevelFunctions(string target)
        {
            var output = CompileAndRun(@"using System;

public static void Main()
{
    Console.WriteLine(Add(2, 3));
    Console.WriteLine(Square(4));
}

public int Add(int x, int y)
{
    return x + y;
}

public int Square(int n)
{
    return n * n;
}", "src-cs-top-level", target, useCs: true);

            Assert.Equal("5\r\n16\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_NoKeywordTopLevelFunction(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    Console.WriteLine(Add(2, 3))
}

function Add(a: i32, b: i32): i32
{
    return a + b
}", "src-no-keyword-top-level", target);

            Assert.Equal("5\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CSharpStyleConstLocal(string target)
        {
            var output = CompileAndRun(@"using System;

public static void Main()
{
    const int x = 10;
    Console.WriteLine(x);
    const string s = ""hi"";
    Console.WriteLine(s);
}", "src-cs-const", target, useCs: true);

            Assert.Equal("10\r\nhi\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ExpressionBodiedTopLevelFunctions(string target)
        {
            var output = CompileAndRun(@"using System

function Add(a: i32, b: i32): i32 => a + b
function Triple(x: i32): i32 => x * 3

function Main()
{
    Console.WriteLine(Add(2, 3))
    Console.WriteLine(Triple(4))
}", "src-expression-body-top-level", target);

            Assert.Equal("5\r\n12\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_StringInterpolation(string target)
        {
            var output = CompileAndRun(@"using System

function Main()
{
    var name = ""Cocoa""
    var a = 10
    var b = 20
    Console.WriteLine($""Hello {name}"")
    Console.WriteLine($""{a} + {b} = {a + b}"")
    Console.WriteLine($""{3.5}"")
    Console.WriteLine($""{true}"")
    Console.WriteLine($""{'A'}"")
    Console.WriteLine($""{{escaped}} {a}"")
}", "src-interp", target);

            Assert.Equal("Hello Cocoa\r\n10 + 20 = 30\r\n3.5\r\nTrue\r\nA\r\n{escaped} 10\r\n", output);
        }
    }
}
