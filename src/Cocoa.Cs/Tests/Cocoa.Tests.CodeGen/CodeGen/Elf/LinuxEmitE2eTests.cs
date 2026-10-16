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
            var syntaxTree = SyntaxTree.Parse(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
            extern()
    }
}

function Main()
{
    var t = Kernel32.GetTickCount()
    Console.WriteLine(t)
}");
            var compilation = Compilation.Create(syntaxTree);
            var outputPath = OutputPath("extern-rejected");
            var diagnostics = compilation.EmitNative("extern-rejected", outputPath, new TargetPlatform(TargetOS.Linux, Architecture.X64));
            Assert.False(diagnostics.IsEmpty);
            Assert.Contains(diagnostics, d => d.Message.Contains("extern"));
        }
    }
}