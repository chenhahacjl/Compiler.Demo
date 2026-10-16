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
        public void WindowsTarget_SoImport_IsRejected()
        {
            // 对称门禁：windows 目标——import libc 编译期拒绝（不再产出运行期 LoadLibrary 坏 exe）。
            var syntaxTree = SyntaxTree.Parse(@"using System

class L
{
    import libc
    {
        static stdcall function getpid(): i32
            extern()
    }
}

function Main(): i32
{
    var pid = L.getpid()
    Console.WriteLine(pid > 0)
    return 0
}");
            var compilation = Compilation.Create(syntaxTree);
            var outputPath = OutputPath("windows-so-rejected");
            var diagnostics = compilation.EmitNative("windows-so-rejected", outputPath, new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.False(diagnostics.IsEmpty);
            Assert.Contains(diagnostics, d =>
                d.Message.Contains(".so 库") || d.Message.Contains("libc"));
            Assert.False(System.IO.File.Exists(outputPath));
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

        [Fact]
        public void LinuxNative_Sha256AndSelfIntrospect_Emits()
        {
            // N5b：SHA-256（纯 LIR）与 SelfIntrospect（/proc/self/exe 扫描 .cocoa）面可编。
            var syntaxTree = SyntaxTree.Parse(@"using System
using System.Syscall

class Runtime
{
    syscall function SelfIntrospect(): bool
}

function Main(): i32
{
    var data: u8[] = new u8[3]
    data[0] = 97
    data[1] = 98
    data[2] = 99
    var hash = System.Syscall.MathSyscall.Sha256Hash(data)
    System.Console.WriteLine(Convert.ToHexString(hash))
    var ok = Runtime.SelfIntrospect()
    System.Console.WriteLine(ok)
    return 0
}");
            var references = new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, syntaxTree);
            var outputPath = OutputPath("sha-selfintrospect");
            var diagnostics = compilation.EmitNative("sha-selfintrospect", outputPath, new TargetPlatform(TargetOS.Linux, Architecture.X64));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));

            var bytes = System.IO.File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }

        [Fact]
        public void LinuxNative_DynamicImport_EmitsInterpDynamic()
        {
            // N3：extern .so 导入 → 动态链接 ELF（PT_INTERP/PT_DYNAMIC 程序头存在）。
            Emit(@"using System

class L
{
    import libc
    {
        static stdcall function getpid(): i32
            extern()
    }
}

function Main(): i32
{
    var pid = L.getpid()
    System.Console.WriteLine(pid > 0)
    return 0
}", "dynamic-import");

            var bytes = System.IO.File.ReadAllBytes(OutputPath("dynamic-import"));
            var phoff = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x20));
            var phnum = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x38));
            Assert.Equal(5, phnum); // LOAD LOAD INTERP DYNAMIC GNU_STACK
            var hasInterp = false;
            var hasDynamic = false;
            for (var i = 0; i < phnum; i++)
            {
                var t = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)(phoff + (ulong)i * 56)));
                if (t == 3) hasInterp = true;
                if (t == 2) hasDynamic = true;
            }

            Assert.True(hasInterp, "PT_INTERP missing");
            Assert.True(hasDynamic, "PT_DYNAMIC missing");
        }

        [Fact]
        public void LinuxNative_StringArgExtern_EmitsDynamicWithLibcDynstr()
        {
            // N4：extern string 实参经 ExternUtf8Ptr 编组（编译期接受 libc），产物为动态 ELF
            // 且 .dynstr 含 DT_NEEDED 的 libc.so.6 与导入符号 puts/strlen；无 Windows DLL 报错。
            Emit(@"using System

class L
{
    import libc
    {
        static stdcall function puts(s: string): i32
            extern()
        static stdcall function strlen(s: string): i32
            extern()
    }
}

function Main(): i32
{
    L.puts(""hi"")
    var n = L.strlen(""cocoa"")
    System.Console.WriteLine(n)
    return 0
}", "dynamic-string-extern");

            var bytes = System.IO.File.ReadAllBytes(OutputPath("dynamic-string-extern"));
            var names = SectionNames(bytes);
            Assert.Contains(".interp", names);
            Assert.Contains(".dynstr", names);
            Assert.Contains(".rela.dyn", names);

            var dynstr = SectionBytes(bytes, ".dynstr");
            Assert.True(IndexOf(dynstr, System.Text.Encoding.ASCII.GetBytes("libc.so.6\0")) >= 0, "dynstr 缺 libc.so.6");
            Assert.True(IndexOf(dynstr, System.Text.Encoding.ASCII.GetBytes("puts\0")) >= 0, "dynstr 缺 puts");
            Assert.True(IndexOf(dynstr, System.Text.Encoding.ASCII.GetBytes("strlen\0")) >= 0, "dynstr 缺 strlen");
        }

        [Fact]
        public void LinuxNative_F64ArgExtern_Libm_EmitsSseArgLoad()
        {
            // N4：extern f64 实参经 SysV xmm 寄存器装载（movd xmm0,eax + pinsrd 位模式），
            // 产物动态 ELF 且 .dynstr 含 libm.so.6 与 sqrt。
            Emit(@"using System

class M
{
    import libm
    {
        static stdcall function sqrt(x: f64): f64
            extern()
    }
}

function Main(): i32
{
    var r = M.sqrt(16.0)
    System.Console.WriteLine(r > 3.9)
    return 0
}", "dynamic-libm-sse");

            var bytes = System.IO.File.ReadAllBytes(OutputPath("dynamic-libm-sse"));
            var names = SectionNames(bytes);
            Assert.Contains(".interp", names);
            Assert.Contains(".dynstr", names);

            var dynstr = SectionBytes(bytes, ".dynstr");
            Assert.True(IndexOf(dynstr, System.Text.Encoding.ASCII.GetBytes("libm.so.6\0")) >= 0, "dynstr 缺 libm.so.6");
            Assert.True(IndexOf(dynstr, System.Text.Encoding.ASCII.GetBytes("sqrt\0")) >= 0, "dynstr 缺 sqrt");

            // movd xmm0,eax（66 0F 6E C0）在 .text 出现 → f64 实参确经 xmm 装载。
            var text = SectionBytes(bytes, ".text");
            Assert.True(IndexOf(text, new byte[] { 0x66, 0x0F, 0x6E, 0xC0 }) >= 0,
                ".text 应含 movd xmm0,eax（f64 实参 xmm 装载）");
        }

        private static System.Collections.Generic.List<string> SectionNames(byte[] image)
        {
            var names = new System.Collections.Generic.List<string>();
            var shoff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
            var shnum = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
            var shstrndx = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3E));
            var shstrOff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(shoff + shstrndx * 64 + 0x18));
            for (var i = 0; i < shnum; i++)
            {
                var nameOff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(shoff + i * 64));
                names.Add(ReadCString(image, shstrOff + nameOff));
            }

            return names;
        }

        private static byte[] SectionBytes(byte[] image, string wanted)
        {
            var shoff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
            var shnum = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
            var shstrndx = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3E));
            var shstrOff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(shoff + shstrndx * 64 + 0x18));
            for (var i = 0; i < shnum; i++)
            {
                var nameOff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(shoff + i * 64));
                if (ReadCString(image, shstrOff + nameOff) == wanted)
                {
                    var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(shoff + i * 64 + 0x18));
                    var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(shoff + i * 64 + 0x20));
                    return image.Skip(offset).Take(size).ToArray();
                }
            }

            throw new Exception($"section '{wanted}' not found");
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ReadCString(byte[] image, int offset)
        {
            var sb = new System.Text.StringBuilder();
            while (offset < image.Length && image[offset] != 0)
            {
                sb.Append((char)image[offset++]);
            }

            return sb.ToString();
        }
    }
}