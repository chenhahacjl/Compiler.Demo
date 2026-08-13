using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X86;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X86;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X64;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    public class X86NativeEmitTests
    {
        private static string GetExePath(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-x86-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name + ".exe");
        }

        private static void WriteExe(X86Assembler a, RuntimeResult runtime, int entryLabel, string exePath)
        {
            var dataRva = PefileWriter.ComputeDataRva(a.ToArray().Length);
            a.Patch(dataRva - PefileWriter.TextRva, PefileWriter.ImageBaseOf(Architecture.X86));
            PefileWriter.Write(exePath, a.ToArray(), a.GetData(), PefileWriter.TextRva + a.GetLabelOffset(entryLabel), runtime.Imports, Architecture.X86);
        }

        private static string Run(string exePath, string? input = null, int expectedExitCode = 0)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = input != null,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)!;

            if (input != null)
            {
                var inputBytes = Encoding.Unicode.GetBytes(input);
                process.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                process.StandardInput.BaseStream.Close();
            }

            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(10000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var bytes = output.ToArray();

            Assert.Equal(expectedExitCode, process.ExitCode);
            return Encoding.Unicode.GetString(bytes);
        }

        private static int CreateDataString(X86Assembler a, string text)
        {
            var symbol = a.CreateDataSymbol();
            a.MarkDataSymbol(symbol);
            a.WriteDataUtf16(text);
            return symbol;
        }

        [Fact]
        public void X86_ExitOnly()
        {
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);
            a.Mov(X64Size.Dword, X64Register.ECX, 0);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("exit-only");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("", Run(exePath));
        }

        [Fact]
        public void X86_PrintsHelloWorld()
        {
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);
            var hello = CreateDataString(a, "Hello, World!");
            a.LeaRip(X64Register.ECX, hello);
            a.Call(runtime.Labels.PrintString);
            a.Mov(X64Size.Dword, X64Register.ECX, 0);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("hello-world");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("Hello, World!", Run(exePath));
        }

        [Fact]
        public void X86_BuildIntDirect()
        {
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);
            a.Sub(X64Size.Dword, X64Register.ESP, 0x60);
            a.Mov(X64Size.Dword, X64Register.ECX, 42);
            a.Lea(X64Register.EDX, new X64MemoryOperand(X64Register.ESP, 0x20));
            a.Call(runtime.Labels.BuildInt);
            a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EAX); // len bytes
            a.Lea(X64Register.ECX, new X64MemoryOperand(X64Register.ESP, 0x20));
            a.Mov(X64Size.Dword, X64Register.EDX, X64Register.ESI);
            a.Shr(X64Size.Dword, X64Register.EDX, 1);
            a.Call(runtime.Labels.WriteStr);
            a.Mov(X64Size.Dword, X64Register.ECX, 0);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("buildint");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("42", Run(exePath));
        }

        [Fact]
        public void X86_PrintIntOnly()
        {
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);
            a.Mov(X64Size.Dword, X64Register.ECX, 42);
            a.Call(runtime.Labels.PrintInt);
            a.Mov(X64Size.Dword, X64Register.ECX, -7);
            a.Call(runtime.Labels.PrintInt);
            a.Mov(X64Size.Dword, X64Register.ECX, 0);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("print-int");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("42-7", Run(exePath));
        }

        [Fact]
        public void X86_RuntimeSmoke()
        {
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);

            a.Mov(X64Size.Dword, X64Register.ECX, 42);
            a.Call(runtime.Labels.PrintInt);

            var foo = CreateDataString(a, "foo");
            var bar = CreateDataString(a, "bar");

            a.LeaRip(X64Register.ECX, foo);
            a.LeaRip(X64Register.EDX, bar);
            a.Call(runtime.Labels.Concat);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Call(runtime.Labels.PrintString);

            a.LeaRip(X64Register.ECX, foo);
            a.LeaRip(X64Register.EDX, foo);
            a.Call(runtime.Labels.StrEquals);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Call(runtime.Labels.PrintInt);

            a.LeaRip(X64Register.ECX, foo);
            a.LeaRip(X64Register.EDX, bar);
            a.Call(runtime.Labels.ObjectEquals);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Call(runtime.Labels.PrintInt);

            a.Call(runtime.Labels.Input);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Call(runtime.Labels.PrintString);

            var loopRandom = a.CreateLabel();
            var failRandom = a.CreateLabel();
            a.Xor(X64Size.Dword, X64Register.EBX, X64Register.EBX);
            a.MarkLabel(loopRandom);
            a.Mov(X64Size.Dword, X64Register.ECX, 100);
            a.Call(runtime.Labels.Random);
            a.Cmp(X64Size.Dword, X64Register.EAX, 100);
            a.Jcc(X64CondCode.AboveOrEqual, failRandom);
            a.Add(X64Size.Dword, X64Register.EBX, 1);
            a.Cmp(X64Size.Dword, X64Register.EBX, 20);
            a.Jcc(X64CondCode.Below, loopRandom);

            a.Mov(X64Size.Dword, X64Register.ECX, 0);
            a.Call(runtime.Labels.ExitProcess);

            a.MarkLabel(failRandom);
            a.Mov(X64Size.Dword, X64Register.ECX, 1);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("runtime-smoke");
            WriteExe(a, runtime, runtime.Entry, exePath);

            Assert.Equal("42foobar10AB", Run(exePath, input: "AB\n"));
        }

        [Fact]
        public void X86_InputStripsCRLF()
        {
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);
            a.Call(runtime.Labels.Input);
            a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            a.Call(runtime.Labels.PrintString);
            a.Mov(X64Size.Dword, X64Register.ECX, 0);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("input-strip-crlf");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("AB", Run(exePath, input: "AB\r\n"));
        }

        [Fact]
        public void X86_ExitCodeFromMain()
        {
            // main 返回码测试：直接调用 ExitProcess(3) 验证 stdcall 无栈错位
            var a = new X86Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX86.Emit(a, entry);

            a.MarkLabel(entry);
            a.Push(X64Register.EBX);
            a.Sub(X64Size.Dword, X64Register.ESP, 4);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 0), X64Register.EBX);
            a.Add(X64Size.Dword, X64Register.ESP, 4);
            a.Pop(X64Register.EBX);
            a.Mov(X64Size.Dword, X64Register.ECX, 3);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("exit-code");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("", Run(exePath, expectedExitCode: 3));
        }
    }
}
