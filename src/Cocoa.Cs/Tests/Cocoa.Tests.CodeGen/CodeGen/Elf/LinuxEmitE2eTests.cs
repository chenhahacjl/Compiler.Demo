using System;
using System.Buffers.Binary;
using System.IO;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Elf
{
    /// <summary>
    /// T3.3：linux-x64 原生发射冒烟（结构断言；实机可执行验收由 tools/verify-linux-vm.cmd 覆盖）。
    /// 验证 NativeCodeEmitter 按 OS 分流到 ELF 写出器、入口落在 .text 段、中文（多字节 UTF-8）链路可编。
    /// </summary>
    public class LinuxEmitE2eTests
    {
        private static string OutputPath(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-linux-tests");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name);
        }

        private static void Emit(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var outputPath = OutputPath(name);
            var diagnostics = compilation.EmitNative(name, outputPath, new TargetPlatform(TargetOS.Linux, Architecture.X64));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));
            Assert.True(File.Exists(outputPath));
        }

        [Fact]
        public void LinuxNative_ProducesValidElf()
        {
            Emit(@"using System

function Main()
{
    Console.WriteLine(""ok"")
}", "hello-elf");

            var bytes = File.ReadAllBytes(OutputPath("hello-elf"));
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x10))); // ET_EXEC
            Assert.Equal(0x3E, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x12))); // EM_X86_64

            var entry = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x18));
            var ph0Vaddr = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x40 + 0x10));
            var ph0Size = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x40 + 0x28));
            Assert.InRange(entry, ph0Vaddr, ph0Vaddr + ph0Size);
        }

        [Fact]
        public void LinuxNative_ChineseOutput_Emits()
        {
            // 中文路径验证 UTF-16→UTF-8（BMP）转换器可编（不实际运行；结构断言）。
            Emit(@"using System

function Main()
{
    Console.WriteLine(""中文测试"")
}", "hello-cn");
            var bytes = File.ReadAllBytes(OutputPath("hello-cn"));
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }

        [Fact]
        public void LinuxNative_Extern_IsRejected()
        {
            // kernel32 非 shim 符号（CreateFileW）在 Linux 编译期拒绝。
            var syntaxTree = SyntaxTree.Parse(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function CreateFileW(name: string): i32
            extern()
    }
}

function Main()
{
    var t = Kernel32.CreateFileW(""a"")
    Console.WriteLine(t)
}");
            var compilation = Compilation.Create(syntaxTree);
            var outputPath = OutputPath("extern-rejected");
            var diagnostics = compilation.EmitNative("extern-rejected", outputPath, new TargetPlatform(TargetOS.Linux, Architecture.X64));
            Assert.False(diagnostics.IsEmpty);
            Assert.Contains(diagnostics, d => d.Message.Contains("extern"));
        }

        [Fact]
        public void LinuxNative_ShimExtern_Compiles()
        {
            // kernel32 四符号 shim（GetTickCount/Sleep/Beep/ExitProcess）在 Linux 零导入编译通过。
            var syntaxTree = SyntaxTree.Parse(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
            extern()
        static stdcall function Sleep(ms: i32): void
            extern()
    }
}

function Main()
{
    var t = Kernel32.GetTickCount()
    Console.WriteLine(t)
    Kernel32.Sleep(10)
}");
            var compilation = Compilation.Create(syntaxTree);
            var outputPath = OutputPath("shim-extern");
            var diagnostics = compilation.EmitNative("shim-extern", outputPath, new TargetPlatform(TargetOS.Linux, Architecture.X64));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));

            var bytes = System.IO.File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }

        [Fact]
        public void LinuxNative_FileEnvSurface_Emits()
        {
            // N2：文件/环境/句柄族 + argv + Process 面可编（结构断言；实机语义见 verify-linux-vm 冒烟）。
            var syntaxTree = SyntaxTree.Parse(@"using System
using System.IO
using System.Syscall

function Main(args: string[]): i32
{
    System.IO.File.WriteAllBytes(""/tmp/x.bin"", new u8[4])
    var r = System.IO.File.ReadAllBytes(""/tmp/x.bin"")
    System.Console.WriteLine(r.Length)
    System.IO.File.Delete(""/tmp/x.bin"")
    var e = System.Environment.GetEnvironmentVariable(""PATH"")
    System.Console.WriteLine(e.Length > 0)
    System.Environment.GetExecutablePath()
    System.IO.Directory.GetCurrentDirectory()
    var c = System.Syscall.ProcessSyscall.LaunchProcess(""/bin/echo"", ""hi"", """")
    System.Console.WriteLine(c == 0)
    return 0
}");
            // 与 SystemIOFileStream 等 facade 测试同款标准库引用，解析 System.IO/System.Syscall 面。
            var references = new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, syntaxTree);
            var outputPath = OutputPath("file-env-surface");
            var diagnostics = compilation.EmitNative("file-env-surface", outputPath, new TargetPlatform(TargetOS.Linux, Architecture.X64));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));

            var bytes = System.IO.File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }
    }
}