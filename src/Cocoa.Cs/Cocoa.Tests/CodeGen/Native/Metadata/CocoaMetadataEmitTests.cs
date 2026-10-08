using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.Native.Metadata;
using Cocoa.CodeGen.PE;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Native.Metadata
{
    /// <summary>
    /// M4：native 写侧嵌入 `.cocoa` 节验收——产物含节（魔数 "COCOA"）、读回 CocoaMetadataReader 可解析、
    /// 程序 byte-identical 正常运行（e2e 双平台）。
    /// </summary>
    public class CocoaMetadataEmitTests
    {
        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        private static TargetPlatform Platform(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            return platform;
        }

        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-metadata-tests");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            return Path.Combine(directory, name + suffix + ".exe");
        }

        private static string EmitNative(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath(name, platform);
            var diagnostics = compilation.EmitNative(name, exePath, platform);
            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));
            return exePath;
        }

        private static byte[] ExtractSection(byte[] image, string wantedName)
        {
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var numSections = BitConverter.ToInt16(image, peOffset + 6);
            var optSize = BitConverter.ToInt16(image, peOffset + 20);
            var sectionTable = peOffset + 24 + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var offset = sectionTable + i * ImageSectionHeader.Size;
                var nameBytes = image.Skip(offset).Take(8).ToArray();
                var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                if (name == wantedName)
                {
                    var rawPtr = BitConverter.ToInt32(image, offset + 20);
                    var rawSize = BitConverter.ToInt32(image, offset + 16);
                    var raw = new byte[rawSize];
                    Array.Copy(image, rawPtr, raw, 0, rawSize);
                    return raw;
                }
            }

            return Array.Empty<byte>();
        }

        private static (int ExitCode, string Stdout) Run(string exePath)
        {
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
            return (process.ExitCode, Encoding.Unicode.GetString(output.ToArray()));
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Cocoa_Section_Embedded_And_RoundTrip(object platform)
        {
            var target = (TargetPlatform)platform;
            var exePath = EmitNative(@"using System

public class Point
{
    field x: i32
    public constructor(x: i32)
    {
        this.x = x
    }

    public function GetX(): i32
    {
        return this.x
    }
}

function Main(): i32
{
    var p = new Point(7)
    Console.WriteLine(p.GetX())
    return 0
}", "cocoa-embedded", target);

            var image = File.ReadAllBytes(exePath);

            // 节存在且魔数正确
            var cocoa = ExtractSection(image, ".cocoa");
            Assert.True(cocoa.Length > 0, ".cocoa 节缺失");
            Assert.Equal("COCOA", Encoding.ASCII.GetString(cocoa, 0, 5));

            // Reader 可解析出类型/方法/字段
            var model = new CocoaMetadataReader(cocoa).Read();
            Assert.Contains(model.Types, t => t.FullName == "Point");
            Assert.Contains(model.Methods, m => m.FullName == "Point.GetX");
            Assert.Contains(model.Fields, f => f.Name == "x" && f.OwnerType == "Point");

            // e2e：产物仍正常运行（嵌入节不破坏载荷）
            var run = Run(exePath);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal("7", run.Stdout.Trim());
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Cocoa_Section_Present_Even_WithoutClasses(object platform)
        {
            var target = (TargetPlatform)platform;
            var exePath = EmitNative(@"
function Main(): i32
{
    return 0
}", "cocoa-noclasses", target);

            var image = File.ReadAllBytes(exePath);
            var cocoa = ExtractSection(image, ".cocoa");
            Assert.True(cocoa.Length > 0, ".cocoa 节缺失");
            Assert.Equal("COCOA", Encoding.ASCII.GetString(cocoa, 0, 5));
        }
    }
}