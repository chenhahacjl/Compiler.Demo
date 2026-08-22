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
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(42)
}", "dbg-int", target);

            Assert.Equal("42\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_DefaultInitializedVariables(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a: int
    var b: bool
    var d: double
    var c: char
    var by: byte
    var s: string
    System.Console.WriteLine(a)
    System.Console.WriteLine(b)
    System.Console.WriteLine(d)
    System.Console.WriteLine(int(c))
    System.Console.WriteLine(int(by))
    System.Console.WriteLine(s == s)
    const k: int = 7
    System.Console.WriteLine(k)
}", "dbg-default", target);

            Assert.Equal("0\r\nFalse\r\n0\r\n0\r\n0\r\nTrue\r\n7\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_PrintString(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(""hi"")
}", "dbg-str", target);

            Assert.Equal("hi\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_PrintsExpressions(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(""Hello, World!"")
    System.Console.WriteLine(42)
    System.Console.WriteLine(7 * 6)
    System.Console.WriteLine(1 + 2 * 3)
    System.Console.WriteLine((1 + 2) * 3)
    System.Console.WriteLine(true)
    System.Console.WriteLine(false)
}", "src-print-expressions", target);

            Assert.Equal("Hello, World!\r\n42\r\n42\r\n7\r\n9\r\nTrue\r\nFalse\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_VariablesAndAssignment(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var x = 10
    x = x + 5
    System.Console.WriteLine(x)
    var y = 3
    y = x * y
    System.Console.WriteLine(y)
    var s = ""foo""
    s = s + ""bar""
    System.Console.WriteLine(s)
}", "src-variables", target);

            Assert.Equal("15\r\n45\r\nfoobar\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_IfStatement(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var x = 10
    if x > 5
    {
        System.Console.WriteLine(""big"")
    }
    if x > 20
    {
        System.Console.WriteLine(""huge"")
    }
    else
    {
        System.Console.WriteLine(""small"")
    }
}", "src-if", target);

            Assert.Equal("big\r\nsmall\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_UserFunctions(string target)
        {
            var output = CompileAndRun(@"
function add(a: int, b: int): int
{
    return a + b
}

function square(x: int): int
{
    return x * x
}

function Main()
{
    System.Console.WriteLine(add(3, 4))
    System.Console.WriteLine(square(5))
    System.Console.WriteLine(square(add(2, 3)))
    var nested = add(square(2), square(3))
    System.Console.WriteLine(nested)
}", "src-user-functions", target);

            Assert.Equal("7\r\n25\r\n25\r\n13\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Recursion(string target)
        {
            var output = CompileAndRun(@"
function factorial(n: int): int
{
    if n <= 1
    {
        return 1
    }
    return n * factorial(n - 1)
}

function fibonacci(n: int): int
{
    if n <= 1
    {
        return n
    }
    return fibonacci(n - 1) + fibonacci(n - 2)
}

function Main()
{
    System.Console.WriteLine(factorial(5))
    System.Console.WriteLine(factorial(10))
    System.Console.WriteLine(fibonacci(10))
}", "src-recursion", target);

            Assert.Equal("120\r\n3628800\r\n55\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_WhileAndBreakContinue(string target)
        {
            var output = CompileAndRun(@"
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
        System.Console.WriteLine(i)
    }
    System.Console.WriteLine(""done"")
}", "src-while", target);

            Assert.Equal("2\r\ndone\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ForAndDoWhile(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    for var j = 0 to 4
    {
        var even = j / 2 * 2 == j
        if !even
        {
            continue
        }
        System.Console.WriteLine(j)
    }
    System.Console.WriteLine(""x"")
    var k = 0
    do
    {
        k += 1
        System.Console.WriteLine(k)
    }
    while k < 3
}", "src-for-do", target);

            Assert.Equal("0\r\n2\r\n4\r\nx\r\n1\r\n2\r\n3\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CSStyleFor_PostfixIncrement(string target)
        {
            var output = CompileAndRun(@"
public static void Main()
{
    var sum = 0;
    for (var i = 0; i < 5; i++)
    {
        sum = sum + i;
    }
    System.Console.WriteLine(sum);
    var j = 10;
    j--;
    System.Console.WriteLine(j);
    j++;
    System.Console.WriteLine(j);
    var total = 0;
    for (;;)
    {
        total = total + 1;
        if (total == 3)
        {
            break;
        }
    }
    System.Console.WriteLine(total);
    var k = 0;
    for (; k < 4; k = k + 1)
    {
        if (k == 2)
        {
            continue;
        }
        System.Console.WriteLine(k);
    }
}", "src-cstyle-for", target, useCs: true);

            Assert.Equal("10\r\n9\r\n10\r\n3\r\n0\r\n1\r\n3\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ModuloAndShift(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(7 % 3)
    System.Console.WriteLine(-7 % 3)
    System.Console.WriteLine(10 % 2)
    System.Console.WriteLine(1 << 4)
    System.Console.WriteLine(8 >> 1)
    System.Console.WriteLine(-8 >> 1)
    var x = 10
    x %= 3
    System.Console.WriteLine(x)
    x = 1
    x <<= 4
    System.Console.WriteLine(x)
    x = -16
    x >>= 2
    System.Console.WriteLine(x)
    var sum = 0
    for var i = 1 to 5
    {
        if i % 2 == 0
        {
            continue
        }
        sum = sum + i
    }
    System.Console.WriteLine(sum)
}", "src-modulo-shift", target);

            Assert.Equal("1\r\n-1\r\n0\r\n16\r\n4\r\n-4\r\n1\r\n16\r\n-4\r\n9\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ConditionalAndPrefix(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a = 5
    var b = 10
    System.Console.WriteLine(a > b ? a : b)
    System.Console.WriteLine(1 < 2 ? 3 + 4 : 5 + 6)
    var i = 1
    i = ++i
    System.Console.WriteLine(i)
    i = --i
    System.Console.WriteLine(i)
    var n = 7
    System.Console.WriteLine(n % 2 == 0 ? ""even"" : ""odd"")
    var sum = 0
    for var j = 1 to 5
    {
        sum = sum + (j % 2 == 0 ? 10 : j)
    }
    System.Console.WriteLine(sum)
}", "src-ternary-prefix", target);

            Assert.Equal("10\r\n7\r\n2\r\n1\r\nodd\r\n29\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ModuloByZero(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(1 % 0)
}", "src-modulo-zero", target, expectedExitCode: 1);

            Assert.Equal("error: division by zero\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CompoundAssignment(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var x = 0
    x += 5
    System.Console.WriteLine(x)
    x *= 3
    System.Console.WriteLine(x)
    x -= 7
    System.Console.WriteLine(x)
    x /= 4
    System.Console.WriteLine(x)
}", "src-compound-assignment", target);

            Assert.Equal("5\r\n15\r\n8\r\n2\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_StringConcatAndCompare(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var s = ""foo""
    var t = ""bar""
    System.Console.WriteLine(s + t)
    System.Console.WriteLine(s + ""!"" + t)
    System.Console.WriteLine(s == s)
    System.Console.WriteLine(s == t)
    System.Console.WriteLine(s != t)
}", "src-strings", target);

            Assert.Equal("foobar\r\nfoo!bar\r\nTrue\r\nFalse\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_StringParameter(string target)
        {
            var output = CompileAndRun(@"
function greet(name: string): string
{
    return ""Hello, "" + name
}

function Main()
{
    var who = ""Cocoa""
    System.Console.WriteLine(greet(who))
    System.Console.WriteLine(greet(""World""))
}", "src-string-parameter", target);

            Assert.Equal("Hello, Cocoa\r\nHello, World\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_NegativeNumbers(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(-5)
    System.Console.WriteLine(0 - 7)
    var n = -3
    System.Console.WriteLine(0 - n)
    var m = -2 * 3
    System.Console.WriteLine(m)
    System.Console.WriteLine(!true)
    System.Console.WriteLine(!false)
}", "src-negative", target);

            Assert.Equal("-5\r\n-7\r\n3\r\n-6\r\nFalse\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_LogicalOperators(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a = true && false
    System.Console.WriteLine(a)
    var b = true || false
    System.Console.WriteLine(b)
    var c = (1 < 2) && (3 > 2)
    System.Console.WriteLine(c)
}", "src-logical", target);

            Assert.Equal("False\r\nTrue\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Input(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var s = System.Console.ReadLine()
    System.Console.WriteLine(s)
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
                var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(System.Runtime.Random(100) < 100)
}", "src-random", target);

                Assert.Equal("True\r\n", output);
            }
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_SyscallMemberCall_Print(string target)
        {
            var output = CompileAndRun(@"
class Runtime
{
    syscall function Print(text: string): void
}

function Main()
{
    Runtime.Print(""hello syscall"")
}", "src-syscall-print", target);

            Assert.Equal("hello syscall\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_SyscallMemberCall_Random(string target)
        {
            var output = CompileAndRun(@"
class Runtime
{
    syscall function Random(max: int): int
}

function Main()
{
    var r = Runtime.Random(100)
    System.Console.WriteLine(r < 100)
}", "src-syscall-random", target);

            Assert.Equal("True\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Builtin_SleepNow(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var t0 = System.Runtime.Now()
    System.Runtime.Sleep(1)
    var t1 = System.Runtime.Now()
    System.Console.WriteLine(t1 >= t0)
}", "src-sleep-now", target);

            Assert.Equal("True\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Division(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(42 / 7)
    System.Console.WriteLine(-42 / 7)
    System.Console.WriteLine(42 / -7)
    System.Console.WriteLine(-42 / -7)
}", "src-division", target);

            Assert.Equal("6\r\n-6\r\n-6\r\n6\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_DivisionByZero(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(1 / 0)
}", "src-division-by-zero", target, expectedExitCode: 1);

            Assert.Equal("error: division by zero\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_ReadWriteAndLength(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a = new int[3] {10, 20, 30}
    a[1] = 99
    System.Console.WriteLine(a[0])
    System.Console.WriteLine(a[1])
    System.Console.WriteLine(a[2])
    System.Console.WriteLine(a.Length)
}", "src-array", target);

            Assert.Equal("10\r\n99\r\n30\r\n3\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_BoolElements(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var b = new bool[2]
    b[0] = true
    b[1] = false
    System.Console.WriteLine(b[0])
    System.Console.WriteLine(b[1])
}", "src-array-bool", target);

            Assert.Equal("True\r\nFalse\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_IndexInLoop(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a = new int[5]
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
    System.Console.WriteLine(sum)
}", "src-array-loop", target);

            Assert.Equal("100\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Foreach_OverArrays(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var arr = new int[] {1, 2, 3}
    foreach (var x in arr)
    {
        System.Console.WriteLine(x)
    }
    var sum = 0
    foreach (var x in arr)
    {
        sum = sum + x
    }
    System.Console.WriteLine(sum)
    var doubles: double[] = new double[] {1.5, 2.5}
    foreach (var d in doubles)
    {
        System.Console.WriteLine(d)
    }
    var names = new string[] {""a"", ""b""}
    foreach (var n in names)
    {
        System.Console.WriteLine(n)
    }
}", "src-foreach-array", target);

            Assert.Equal("1\r\n2\r\n3\r\n6\r\n1.5\r\n2.5\r\na\r\nb\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Foreach_OverString_And_BreakContinue(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var s = ""abc""
    foreach (var c in s)
    {
        System.Console.WriteLine(c)
    }
    var arr = new int[] {1, 2, 3, 4}
    foreach (var x in arr)
    {
        if x == 3 continue
        if x == 4 break
        System.Console.WriteLine(x)
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
    System.Console.WriteLine(result)
}", "src-foreach-string", target);

            Assert.Equal("a\r\nb\r\nc\r\n1\r\n2\r\n80\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Switch(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var x = 2
    switch (x)
    {
        case 1:
        {
            System.Console.WriteLine(""one"")
            break
        }
        case 2:
        {
            System.Console.WriteLine(""two"")
            break
        }
        default:
        {
            System.Console.WriteLine(""other"")
            break
        }
    }
    switch (x)
    {
        case 1:
        case 2:
        {
            System.Console.WriteLine(""low"")
            break
        }
        default:
        {
            System.Console.WriteLine(""high"")
            break
        }
    }
    switch (x)
    {
        case 1:
        {
            System.Console.WriteLine(""one"")
            break
        }
        case 2 when false:
        {
            System.Console.WriteLine(""two-when"")
            break
        }
        default:
        {
            System.Console.WriteLine(""default"")
            break
        }
    }
    var s = ""b""
    switch (s)
    {
        case ""a"":
        {
            System.Console.WriteLine(""A"")
            break
        }
        case ""b"":
        {
            System.Console.WriteLine(""B"")
            break
        }
        default:
        {
            System.Console.WriteLine(""Z"")
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
    System.Console.WriteLine(sum)
}", "src-switch", target);

            Assert.Equal("two\r\nlow\r\ndefault\r\nB\r\n9\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Array_OutOfBounds(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a = new int[2]
    a[0] = 1
    a[1] = 2
    System.Console.WriteLine(a[5])
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
    var rows = new int[2]
    var row = new int[2] {5, 6}
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
            var output = CompileAndRun(@"
function Main()
{
    var s = ""hello""
    System.Console.WriteLine(s.Length)
    System.Console.WriteLine(s[0])
    System.Console.WriteLine(int(s[1]))
    var c = s[2]
    System.Console.WriteLine(c)
    System.Console.WriteLine(char(97))
    System.Console.WriteLine(s.substring(1, 3))
    System.Console.WriteLine(s.substring(1, 3) + ""!"")
}", "src-string-index", target);

            Assert.Equal("5\r\nh\r\n101\r\nl\r\na\r\nell\r\nell!\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CharArray(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var a = new char[2] {'x', 'y'}
    a[0] = 'z'
    System.Console.WriteLine(a[0])
    System.Console.WriteLine(a[1])
}", "src-char-array", target);

            Assert.Equal("z\r\ny\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_String_IndexOutOfBounds(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var s = ""abc""
    System.Console.WriteLine(s[9])
}", "src-string-oob", target, expectedExitCode: 1);

            Assert.Equal("error: array index out of range\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Substring_InvalidArguments(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var s = ""abc""
    System.Console.WriteLine(s.substring(1, 99))
}", "src-substring-invalid", target, expectedExitCode: 1);

            Assert.Equal("error: invalid substring arguments\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Enum_EndToEnd(string target)
        {
            var output = CompileAndRun(@"
public enum Color { Red, Green, Blue }
public enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 }
function f(c: Color): int { return int(c) }
function Main()
{
    var c = Color.Green
    System.Console.WriteLine(int(c))
    System.Console.WriteLine(int(HttpStatus.NotFound))
    System.Console.WriteLine(c == Color.Green)
    System.Console.WriteLine(c == Color.Red)
    System.Console.WriteLine(int(f(Color.Blue)))
    System.Console.WriteLine(int(Color(99)) == 99)
}", "src-enum", target);

            Assert.Equal("1\r\n404\r\nTrue\r\nFalse\r\n2\r\nTrue\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_EnumArray(string target)
        {
            var output = CompileAndRun(@"
public enum Color { Red, Green, Blue }
function Main()
{
    var a = new Color[2] {Color.Red, Color.Green}
    System.Console.WriteLine(int(a[0]))
    System.Console.WriteLine(int(a[1]))
    a[1] = Color.Blue
    System.Console.WriteLine(int(a[1]))
}", "src-enum-array", target);

            Assert.Equal("0\r\n1\r\n2\r\n", output);
        }

        [Fact]
        public void NativeSource_Interface_ReportsUnsupported()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public interface IShape
{
    function Area(): int
}

public class Circle extends IShape
{
    public function Area(): int
    {
        return 1
    }
}

function Main()
{
    var s: IShape = new Circle()
    System.Console.WriteLine(s.Area())
}");
            var compilation = Compilation.Create(syntaxTree);
            TargetPlatform.TryParse(X64, out var platform);
            var diagnostics = compilation.EmitNative("test", GetExePath("native-interface", X64), platform);
            Assert.NotEmpty(diagnostics);
            Assert.Contains(diagnostics, d => d.Message.Contains("含实例成员/构造/字段/属性/基类，暂不支持 native 后端"));
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_CSharpStyleTopLevelFunctions(string target)
        {
            var output = CompileAndRun(@"
public static void Main()
{
    System.Console.WriteLine(Add(2, 3));
    System.Console.WriteLine(Square(4));
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
            var output = CompileAndRun(@"
function Main()
{
    System.Console.WriteLine(Add(2, 3))
}

function Add(a: int, b: int): int
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
            var output = CompileAndRun(@"
public static void Main()
{
    const int x = 10;
    System.Console.WriteLine(x);
    const string s = ""hi"";
    System.Console.WriteLine(s);
}", "src-cs-const", target, useCs: true);

            Assert.Equal("10\r\nhi\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_ExpressionBodiedTopLevelFunctions(string target)
        {
            var output = CompileAndRun(@"
function Add(a: int, b: int): int => a + b
function Triple(x: int): int => x * 3

function Main()
{
    System.Console.WriteLine(Add(2, 3))
    System.Console.WriteLine(Triple(4))
}", "src-expression-body-top-level", target);

            Assert.Equal("5\r\n12\r\n", output);
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_StringInterpolation(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    var name = ""Cocoa""
    var a = 10
    var b = 20
    System.Console.WriteLine($""Hello {name}"")
    System.Console.WriteLine($""{a} + {b} = {a + b}"")
    System.Console.WriteLine($""{3.5}"")
    System.Console.WriteLine($""{true}"")
    System.Console.WriteLine($""{'A'}"")
    System.Console.WriteLine($""{{escaped}} {a}"")
}", "src-interp", target);

            Assert.Equal("Hello Cocoa\r\n10 + 20 = 30\r\n3.5\r\nTrue\r\nA\r\n{escaped} 10\r\n", output);
        }
    }
}
