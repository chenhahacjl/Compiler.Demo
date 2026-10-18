using System;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Elf
{
    /// <summary>
    /// S4：Linux 共享框架消费侧（`.so` 分离）——linux native 拆分形态 exe 经 GLOB_DAT/DT_NEEDED 导入
    /// <c>System.Core.Native.so</c>（运行时原语 Cocoa_Rt_* + 库函数 Cocoa_*），exe 显著小于内联。
    /// 字节级断言（Windows 环境无法执行 ELF）。
    /// </summary>
    public class LinuxSharedExeTests
    {
        private static readonly TargetPlatform Linux64 = new(TargetOS.Linux, Architecture.X64);

        private static byte[] EmitSharedApp()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-linux-shared-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "App");
            // linkCoaDynamically（System.Core 外链）+ splitRuntime（原语外链）→ 完整 .so 拆分
            var diags = Compilation.Create(new string[0], linkCoaDynamically: true, SyntaxTree.Parse(@"using System
function Main(): i32
{
    var s = Math.Abs(-5.5)
    Console.WriteLine(s)
    Console.WriteLine(""a"" + ""b"")
    return 0
}
")).EmitNative("App", exe, Linux64, splitRuntime: true);
            Assert.True(!diags.HasErrors(), "diags: " + string.Join(" | ", diags.Select(d => d.Message)));
            return File.ReadAllBytes(exe);
        }

        [Fact]
        public void SharedExe_IsEtExec_And_SmallerThanInline()
        {
            var shared = EmitSharedApp();
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, shared.Take(4).ToArray());
            Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(shared.AsSpan(0x10))); // ET_EXEC

            // 内联形态对照（同源码）
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-linux-inline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "Inline");
            Compilation.Create(SyntaxTree.Parse(@"using System
function Main(): i32
{
    var s = Math.Abs(-5.5)
    Console.WriteLine(s)
    Console.WriteLine(""a"" + ""b"")
    return 0
}
")).EmitNative("Inline", exe, Linux64);
            var inline = File.ReadAllBytes(exe);

            Assert.True(shared.Length < inline.Length,
                $"shared ({shared.Length}) should be smaller than inline ({inline.Length})");
        }

        [Fact]
        public void SharedExe_Dynstr_Carries_Needed_And_CocoaImports()
        {
            var image = EmitSharedApp();
            var text = Encoding.ASCII.GetString(image);

            // DT_NEEDED：System.Core.Native.so
            Assert.Contains("System.Core.Native.so", text);

            // S4：DT_RUNPATH=$ORIGIN——加载器按 exe 同目录解析 .so（自含部署）
            Assert.Contains("$ORIGIN", text);

            // 库函数导入（Cocoa_ + mangle(FnKey)）：Console.WriteLine(f64/string) 两重载
            Assert.Contains("Cocoa_System.Core_System.Console.WriteLine__string", text);
            Assert.Contains("Cocoa_System.Core_System.Console.WriteLine__f64", text);

            // 运行时原语导入（Cocoa_Rt_*）：Concat（"a"+"b"）/ 退出 / 栈检查
            Assert.Contains("Cocoa_Rt_concat", text);
            Assert.Contains("Cocoa_Rt_exit_process", text);
            Assert.Contains("Cocoa_Rt_stack_overflow", text);
        }
    }
}