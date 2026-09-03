using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-G7 ⑤a：编码原语（System.Convert，纯 Cocoa 实现）三后端锁定——
    /// hex/base64 编解码，Evaluator/IL/native（x64/x86）输出一致。
    /// 依赖 native `StringFromChars` 运行时接入（char[] → string）。
    /// </summary>
    public class ConvertTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

function Main(): i32
{
    var data: u8[] = new u8[3]
    data[0] = 15
    data[1] = 42
    data[2] = 255
    System.Console.WriteLine(Convert.ToHexString(data))
    System.Console.WriteLine(Convert.ToBase64String(data))

    var backHex = Convert.FromHexString(""0F2AFF"")
    System.Console.WriteLine(backHex.Length)
    if backHex.Length != 3 return 1
    if i32(backHex[0]) != 15 return 2

    var backB64 = Convert.FromBase64String(""Dyr/"")
    System.Console.WriteLine(backB64.Length)
    if backB64.Length != 3 return 3
    if i32(backB64[2]) != 255 return 4

    // 边界：空输入、奇数 hex、非法 hex、base64 填充
    var empty = Convert.ToHexString(new u8[0])
    if empty != """" return 5
    if Convert.FromHexString(""ABC"").Length != 0 return 6
    if Convert.FromHexString(""G1"").Length != 0 return 7
    if Convert.FromBase64String(""a"").Length != 0 return 8

    // 填充 round-trip：1 字节与 2 字节余数
    var one: u8[] = new u8[1]
    one[0] = 65
    var oneB64 = Convert.ToBase64String(one)
    var oneBack = Convert.FromBase64String(oneB64)
    if oneBack.Length != 1 return 9
    if i32(oneBack[0]) != 65 return 10
    System.Console.WriteLine(oneB64)

    var two: u8[] = new u8[2]
    two[0] = 65
    two[1] = 66
    System.Console.WriteLine(Convert.ToBase64String(two))
    return 0
}";

        private const string Expected = "0F2AFF\nDyr/\n3\n3\nQQ==\nQUI=\n";

        [Fact]
        public void Evaluator_Convert()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string RunIl()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-convert", "convert-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("convert", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("IL exe did not exit in time.");
            }

            outputTask.Wait();
            Assert.Equal(0, process.ExitCode);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        private static string RunNative(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-convert", "convert-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.EmitNative("convert", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            return NativeEmitTests.Run(exePath);
        }

        [Fact]
        public void IlE2e_Convert() => Assert.Equal(Expected, RunIl().Replace("\r\n", "\n").Replace("\r", "\n"));

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_Convert(string target) => Assert.Equal(Expected, RunNative(target).Replace("\r\n", "\n").Replace("\r", "\n"));
    }
}
