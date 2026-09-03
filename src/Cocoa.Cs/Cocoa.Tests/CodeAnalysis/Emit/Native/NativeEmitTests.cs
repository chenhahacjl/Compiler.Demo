using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.PE;
using Cocoa.CodeGen.Native.Runtime.Windows.X64;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    public class NativeEmitTests
    {
        private static string GetExePath(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-tests");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name + ".exe");
        }

        private static void WriteExe(X64Assembler a, RuntimeResult runtime, int entryLabel, string exePath)
        {
            var dataRva = PeFileWriter.ComputeDataRva(a.ToArray().Length);
            a.Patch(dataRva - PeFileWriter.TextRva, PeFileWriter.ImageBaseOf(Architecture.X64));
            PeFileWriter.Write(exePath, a.ToArray(), a.GetData(), PeFileWriter.TextRva + a.GetLabelOffset(entryLabel), runtime.Imports, Architecture.X64);
        }

        internal static string Run(string exePath, string? input = null, int expectedExitCode = 0)
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

        private static int CreateDataString(X64Assembler a, string text)
        {
            var symbol = a.CreateDataSymbol();
            a.MarkDataSymbol(symbol);
            a.WriteDataUtf16(text);
            return symbol;
        }

        private static RuntimeResult BuildHelloWorld(X64Assembler a)
        {
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX64.Emit(a, entry);

            a.MarkLabel(entry);

            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);
            var hello = CreateDataString(a, "Hello, World!");
            a.LeaRip(X64Register.RCX, hello);
            a.Call(runtime.Labels.PrintString);
            a.Xor(X64Size.Dword, X64Register.RCX, X64Register.RCX);
            a.Call(runtime.Labels.ExitProcess);

            return runtime;
        }

        [Fact]
        public void NativeExe_HasValidPeHeaders()
        {
            var a = new X64Assembler();
            var runtime = BuildHelloWorld(a);
            var exePath = GetExePath("pe-headers");

            WriteExe(a, runtime, runtime.Entry, exePath);
            var bytes = File.ReadAllBytes(exePath);

            Assert.Equal(new byte[] { 0x4D, 0x5A }, new[] { bytes[0], bytes[1] });
            var peOffset = BitConverter.ToInt32(bytes, 0x3C);
            Assert.Equal(0x80, peOffset);
            Assert.Equal("PE", Encoding.ASCII.GetString(bytes, peOffset, 2));
            Assert.Equal(0x8664, BitConverter.ToUInt16(bytes, peOffset + 4));
            Assert.Equal(3, BitConverter.ToUInt16(bytes, peOffset + 6));
            Assert.Equal((uint)PeFileWriter.SizeOfHeaders, BitConverter.ToUInt32(bytes, peOffset + 0x18 + 0x3C));
        }

        [Fact]
        public void NativeExe_PrintsHelloWorld()
        {
            var a = new X64Assembler();
            var runtime = BuildHelloWorld(a);
            var exePath = GetExePath("hello-world");

            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.True(File.Exists(exePath));
            Assert.Equal("Hello, World!", Run(exePath));
        }

        [Fact]
        public void NativeExe_RuntimeSmoke()
        {
            var a = new X64Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX64.Emit(a, entry);

            a.MarkLabel(entry);

            a.Push(X64Register.RBX);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x20);

            a.Mov(X64Size.Dword, X64Register.RCX, 42);
            a.Call(runtime.Labels.PrintInt);

            var foo = CreateDataString(a, "foo");
            var bar = CreateDataString(a, "bar");

            a.LeaRip(X64Register.RCX, foo);
            a.LeaRip(X64Register.RDX, bar);
            a.Call(runtime.Labels.Concat);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RAX);
            a.Call(runtime.Labels.PrintString);

            a.LeaRip(X64Register.RCX, foo);
            a.LeaRip(X64Register.RDX, foo);
            a.Call(runtime.Labels.StrEquals);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RAX);
            a.Call(runtime.Labels.PrintInt);

            a.LeaRip(X64Register.RCX, foo);
            a.LeaRip(X64Register.RDX, bar);
            a.Call(runtime.Labels.ObjectEquals);
            a.Mov(X64Size.Dword, X64Register.RCX, X64Register.RAX);
            a.Call(runtime.Labels.PrintInt);

            a.Call(runtime.Labels.Input);
            a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RAX);
            a.Call(runtime.Labels.PrintString);

            var loopRandom = a.CreateLabel();
            var failRandom = a.CreateLabel();
            a.Xor(X64Size.Dword, X64Register.RBX, X64Register.RBX);
            a.MarkLabel(loopRandom);
            a.Mov(X64Size.Dword, X64Register.RCX, 100);
            a.Call(runtime.Labels.Random);
            a.Cmp(X64Size.Dword, X64Register.RAX, 100);
            a.Jcc(X64CondCode.AboveOrEqual, failRandom);
            a.Add(X64Size.Dword, X64Register.RBX, 1);
            a.Cmp(X64Size.Dword, X64Register.RBX, 20);
            a.Jcc(X64CondCode.Below, loopRandom);

            a.Xor(X64Size.Dword, X64Register.RCX, X64Register.RCX);
            a.Call(runtime.Labels.ExitProcess);

            a.MarkLabel(failRandom);
            a.Mov(X64Size.Dword, X64Register.RCX, 1);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("runtime-smoke");
            WriteExe(a, runtime, runtime.Entry, exePath);

            Assert.Equal("42foobar10AB", Run(exePath, input: "AB\n"));
        }

        [Fact]
        public void NativeExe_ExitOnly()
        {
            var a = new X64Assembler();
            var entry = a.CreateLabel();
            var runtime = RuntimeEmitterX64.Emit(a, entry);

            a.MarkLabel(entry);
            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);
            a.Xor(X64Size.Dword, X64Register.RCX, X64Register.RCX);
            a.Call(runtime.Labels.ExitProcess);

            var exePath = GetExePath("exit-only");
            WriteExe(a, runtime, runtime.Entry, exePath);
            Assert.Equal("", Run(exePath));
        }
    }
}