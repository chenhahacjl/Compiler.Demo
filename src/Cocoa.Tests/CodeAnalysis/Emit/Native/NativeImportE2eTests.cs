using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// Native 路径 import 消费 e2e（阶段 6c-1）：用户 extern 经多 DLL 导入表 + 启动 stub 解析，
    /// 退出码/返回值验证参数与符号穿越（x64 与 x86 双平台）。
    /// </summary>
    public class NativeImportE2eTests
    {
        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-tests");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            return Path.Combine(directory, name + suffix + ".exe");
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath(name, platform);
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray());
            return (process.ExitCode, stdout);
        }

        private static TargetPlatform X64 => new TargetPlatform(TargetOS.Windows, Architecture.X64);
        private static TargetPlatform X86 => new TargetPlatform(TargetOS.Windows, Architecture.X86);

        [Fact]
        public void Native_StdCallExtern_ExitProcess_ExitCode()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function ExitProcess(exitCode: i32)
    }
}

function Main()
{
    Kernel32.ExitProcess(42)
}", "native-import-exitprocess", X64);

            Assert.Equal(42, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Native_CdeclExtern_ExitProcess_ExitCode()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"
class Kernel32
{
    import kernel32.dll
    {
        static cdecl function ExitProcess(exitCode: i32)
    }
}

function Main()
{
    Kernel32.ExitProcess(7)
}", "native-import-cdecl-exitprocess", X64);

            // x64 上约定统一；验证 cdecl 关键字走通多 DLL stub 关键路径
            Assert.Equal(7, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Native_CrossDll_GetTickCountAndUser32MessageBeep()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
    }
}

class User32
{
    import user32.dll
    {
        static stdcall function MessageBeep(uType: i32): i32
    }
}

function Main()
{
    var t = Kernel32.GetTickCount()
    var b = User32.MessageBeep(0)
    if t > 0 && b != 0
    {
        Console.WriteLine(""ok"")
    }
}", "native-import-crossdll", X64);

            // 跨 DLL：kernel32 符号 + user32 符号（LoadLibraryA 路径解析）
            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Native_GetStdHandle_InputHandle()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetStdHandle(nStdHandle: i32): i32
    }
}

function Main()
{
    var h = Kernel32.GetStdHandle(0 - 10)
    if h != 0
    {
        Console.WriteLine(""ok"")
    }
    else
    {
        Console.WriteLine(""none"")
    }
}", "native-import-getstdhandle", X64);

            Assert.Equal(0, exitCode);
            // 测试宿主未必有可用 stdin，句柄有效性不在此校验
            Assert.True(stdout == "ok\r\n" || stdout == "none\r\n", stdout);
        }

        [Fact]
        public void Native_Byte_EndToEnd()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var b1: u8 = 65
    Console.WriteLine(b1)
    var buf: u8[] = new u8[3]
    buf[0] = 200
    buf[1] = 0xFF
    Console.WriteLine(buf[0])
    Console.WriteLine(buf[1])
    Console.WriteLine((u8)300)
    Console.WriteLine((i32)buf[0])
    Console.WriteLine(0xFF)
}", "native-byte-e2e", X64);

            Assert.Equal(0, exitCode);
            Assert.Equal("65\r\n200\r\n255\r\n44\r\n200\r\n255\r\n", stdout);
        }

        [Fact]
        public void Native_X86_Byte_EndToEnd()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var b1: u8 = 65
    Console.WriteLine(b1)
    var buf: u8[] = new u8[3]
    buf[0] = 200
    buf[1] = 0xFF
    Console.WriteLine(buf[0])
    Console.WriteLine(buf[1])
    Console.WriteLine((u8)300)
    Console.WriteLine((i32)buf[0])
    Console.WriteLine(0xFF)
}", "native-byte-e2e-x86", X86);

            Assert.Equal(0, exitCode);
            Assert.Equal("65\r\n200\r\n255\r\n44\r\n200\r\n255\r\n", stdout);
        }

        [Fact]
        public void Native_X86_StdCallExtern_ExitProcess_ExitCode()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function ExitProcess(exitCode: i32)
    }
}

function Main()
{
    Kernel32.ExitProcess(42)
}", "native-import-x86-exitprocess", X86);

            Assert.Equal(42, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Native_X86_CdeclExtern_Args_CallerCleanup()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"
class Kernel32
{
    import kernel32.dll
    {
        static cdecl function ExitProcess(exitCode: i32)
    }
}

function Main()
{
    Kernel32.ExitProcess(5)
}", "native-import-x86-cdecl", X86);

            // x86 cdecl：调用方清栈路径
            Assert.Equal(5, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Native_X86_CrossDll_MessageBeep()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

class User32
{
    import user32.dll
    {
        static stdcall function MessageBeep(uType: i32): i32
    }
}

function Main()
{
    var b = User32.MessageBeep(0)
    if b != 0
    {
        Console.WriteLine(""ok"")
    }
}", "native-import-x86-crossdll", X86);

            // x86 stub 的 LoadLibraryA 路径（无 kernel32 用户导入，组顺序验证）
            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Native_MainWithIntReturn_ExitCode()
        {
            // main(): int 的返回值成为进程退出码（EAX → ECX → ExitProcess）
            var (exitCode, stdout) = EmitNativeAndRun(@"
function Main(): i32
{
    return 42
}", "native-main-int-return", X64);

            Assert.Equal(42, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Native_X86_MainWithIntReturn_ExitCode()
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"
function Main(): i32
{
    return 42
}", "native-x86-main-int-return", X86);

            Assert.Equal(42, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Native_UnknownSymbol_ReportsWarningDiagnostic()
        {
            var syntaxTree = SyntaxTree.Parse(@"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function NotARealSymbol(x: i32): i32
    }
}

function Main()
{
    var v = Kernel32.NotARealSymbol(1)
}
");
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath("native-import-unknown", X64);
            var diagnostics = compilation.EmitNative("native-import-unknown", exePath, X64);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("not found in export table of 'kernel32.dll'", diagnostic.Message);
        }

        [Fact]
        public void Native_ExternEntryAlias_ResolvesToRealExport()
        {
            // entry 别名：Cocoa 名 GetTickCountAlias 映射到 DLL 导出名 GetTickCount
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCountAlias(): i32
            extern(entry = GetTickCount)
    }
}

function Main()
{
    var t = Kernel32.GetTickCountAlias()
    if t > 0
    {
        Console.WriteLine(""ok"")
    }
}", "native-import-entry-alias", X64);

            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Native_ExternCharsetAnsi_ReportsNotImplementedError()
        {
            var syntaxTree = SyntaxTree.Parse(@"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
            extern(charset = ansi)
    }
}

function Main()
{
    var v = Kernel32.GetTickCount()
}
");
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath("native-import-ansi", X64);
            var diagnostics = compilation.EmitNative("native-import-ansi", exePath, X64);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("charset = ansi，native 后端未实现", diagnostic.Message);
        }
    }
}