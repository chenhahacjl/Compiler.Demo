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

        private static string CompileAndRun(string source, string name, string target, string? input = null, int expectedExitCode = 0)
        {
            TargetPlatform.TryParse(target, out var platform);
            var syntaxTree = SyntaxTree.Parse(source);
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
    print(42)
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
    print(a)
    print(b)
    print(d)
    print(int(c))
    print(int(by))
    print(s == s)
    const k: int = 7
    print(k)
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
    print(""hi"")
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
    print(""Hello, World!"")
    print(42)
    print(7 * 6)
    print(1 + 2 * 3)
    print((1 + 2) * 3)
    print(true)
    print(false)
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
    print(x)
    var y = 3
    y = x * y
    print(y)
    var s = ""foo""
    s = s + ""bar""
    print(s)
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
        print(""big"")
    }
    if x > 20
    {
        print(""huge"")
    }
    else
    {
        print(""small"")
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
    print(add(3, 4))
    print(square(5))
    print(square(add(2, 3)))
    var nested = add(square(2), square(3))
    print(nested)
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
    print(factorial(5))
    print(factorial(10))
    print(fibonacci(10))
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
        print(i)
    }
    print(""done"")
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
    for j = 0 to 4
    {
        var even = j / 2 * 2 == j
        if !even
        {
            continue
        }
        print(j)
    }
    print(""x"")
    var k = 0
    do
    {
        k += 1
        print(k)
    }
    while k < 3
}", "src-for-do", target);

            Assert.Equal("0\r\n2\r\n4\r\nx\r\n1\r\n2\r\n3\r\n", output);
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
    print(x)
    x *= 3
    print(x)
    x -= 7
    print(x)
    x /= 4
    print(x)
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
    print(s + t)
    print(s + ""!"" + t)
    print(s == s)
    print(s == t)
    print(s != t)
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
    print(greet(who))
    print(greet(""World""))
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
    print(-5)
    print(0 - 7)
    var n = -3
    print(0 - n)
    var m = -2 * 3
    print(m)
    print(!true)
    print(!false)
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
    print(a)
    var b = true || false
    print(b)
    var c = (1 < 2) && (3 > 2)
    print(c)
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
    var s = input()
    print(s)
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
    print(random(100) < 100)
}", "src-random", target);

                Assert.Equal("True\r\n", output);
            }
        }

        [Theory]
        [InlineData(X64)]
        [InlineData(X86)]
        public void NativeSource_Division(string target)
        {
            var output = CompileAndRun(@"
function Main()
{
    print(42 / 7)
    print(-42 / 7)
    print(42 / -7)
    print(-42 / -7)
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
    print(1 / 0)
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
    print(a[0])
    print(a[1])
    print(a[2])
    print(a.Length)
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
    print(b[0])
    print(b[1])
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
    print(sum)
}", "src-array-loop", target);

            Assert.Equal("100\r\n", output);
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
    print(a[5])
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
    print(s.Length)
    print(s[0])
    print(int(s[1]))
    var c = s[2]
    print(c)
    print(char(97))
    print(s.substring(1, 3))
    print(s.substring(1, 3) + ""!"")
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
    print(a[0])
    print(a[1])
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
    print(s[9])
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
    print(s.substring(1, 99))
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
    print(int(c))
    print(int(HttpStatus.NotFound))
    print(c == Color.Green)
    print(c == Color.Red)
    print(int(f(Color.Blue)))
    print(int(Color(99)) == 99)
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
    print(int(a[0]))
    print(int(a[1]))
    a[1] = Color.Blue
    print(int(a[1]))
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

public class Circle: IShape
{
    public function Area(): int
    {
        return 1
    }
}

function Main()
{
    var s: IShape = new Circle()
    print(s.Area())
}");
            var compilation = Compilation.Create(syntaxTree);
            TargetPlatform.TryParse(X64, out var platform);
            var diagnostics = compilation.EmitNative("test", GetExePath("native-interface", X64), platform);
            Assert.NotEmpty(diagnostics);
            Assert.Contains(diagnostics, d => d.Message.Contains("class 暂不支持 native 后端"));
        }
    }
}
