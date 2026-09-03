using System;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// Phase 2 COCOA_DUMP_IR 断言测试：锁 LIR 归并形态。
    /// 设 COCOA_DUMP_IR 编译 → LirToAssembler.DumpIr 写 cocoa-ir-{x86|x64}.txt 到临时目录，
    /// 断言：显式基本块（bbN:）、64 位整型 opcode 已归并（无 add64 等平台项）、
    /// 运算由 LirType 驱动（add/sub/imul 语义正确）。
    /// </summary>
    public class LirDumpTests
    {
        private static string DumpIr(string source, string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-dump");
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "dump-ir-" + target + ".exe");
            var dumpFile = Path.Combine(Path.GetTempPath(), target.EndsWith("x86") ? "cocoa-ir-x86.txt" : "cocoa-ir-x64.txt");

            var previous = Environment.GetEnvironmentVariable("COCOA_DUMP_IR");
            Environment.SetEnvironmentVariable("COCOA_DUMP_IR", "1");
            try
            {
                File.Delete(dumpFile);
                var diagnostics = compilation.EmitNative("test", exePath, platform);
                Assert.Empty(diagnostics);
                Assert.True(File.Exists(dumpFile), "COCOA_DUMP_IR did not produce dump file.");
                return File.ReadAllText(dumpFile, Encoding.UTF8);
            }
            finally
            {
                if (previous == null)
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_IR", null);
                }
                else
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_IR", previous);
                }
            }
        }

        [Theory]
        [InlineData("windows-x86")]
        [InlineData("windows-x64")]
        public void Dump_Shows_BasicBlocks(string target)
        {
            var text = DumpIr(@"
using System
function Main()
{
    var x = 0
    for var i = 1 to 10 { x = x + i }
    Console.WriteLine(x)
}", target);

            Assert.Contains("=== Main (ret=", text);
            Assert.Contains("bb:", text);
            Assert.Contains("jcc", text);
            Assert.Contains("ret L", text);
        }

        [Theory]
        [InlineData("windows-x86")]
        [InlineData("windows-x64")]
        public void Dump_Has_No_Legacy_64Bit_OpCodes(string target)
        {
            var text = DumpIr(@"
using System
function Main()
{
    var a: i64 = 9223372036854775807
    var b: i64 = 2
    var c = a * b + a / b - a % b
    a = a << 3
    a = a >> 1
    if (c > a) Console.WriteLine(1) else Console.WriteLine(0)
}", target);

            Assert.DoesNotContain("add64", text);
            Assert.DoesNotContain("sub64", text);
            Assert.DoesNotContain("imul64", text);
            Assert.DoesNotContain("and64", text);
            Assert.DoesNotContain("or64", text);
            Assert.DoesNotContain("xor64", text);
            Assert.DoesNotContain("neg64", text);
            Assert.DoesNotContain("not64", text);
            Assert.DoesNotContain("shl64", text);
            Assert.DoesNotContain("shr64", text);
            Assert.DoesNotContain("sar64", text);
            Assert.DoesNotContain("cmp64", text);
            Assert.DoesNotContain("idiv64", text);
            Assert.DoesNotContain("irem64", text);
            Assert.DoesNotContain("udiv64", text);
            Assert.DoesNotContain("urem64", text);
        }

        [Theory]
        [InlineData("windows-x86")]
        [InlineData("windows-x64")]
        public void Dump_Uses_Merged_Arithmetic_OpCodes(string target)
        {
            var text = DumpIr(@"
using System
function Main()
{
    var x = 5
    var y = 7
    Console.WriteLine(x + y)
}", target);

            // 归并后 32 位整型算术用 add（无类型后缀）
            Assert.Contains("add ", text);
            Assert.Contains("const ", text);
            Assert.Contains("call rt$PrintInt", text);
        }
    }
}
