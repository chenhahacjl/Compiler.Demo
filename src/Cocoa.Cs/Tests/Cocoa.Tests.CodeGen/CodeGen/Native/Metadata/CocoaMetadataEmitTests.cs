using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.Native.Metadata;
using Cocoa.CodeGen.ObjectFormat.PE;
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

        /// <summary>
        /// M6（弱化）：运行期自省冒烟——SelfIntrospect 读 data 段回填的 .cocoa 元数据指针槽判非零
        /// （不再 GetModuleHandleW / 遍历自身镜像）→ Main 返回 0；探不到返回 1（挂）。
        /// </summary>
        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void SelfIntrospect_DetectsOwnCocoaSection(object platform)
        {
            var target = (TargetPlatform)platform;
            var exePath = EmitNative(@"using System

class Runtime
{
    syscall function SelfIntrospect(): bool
}

function Main(): i32
{
    if !Runtime.SelfIntrospect() return 1
    return 0
}", "cocoa-selfintrospect", target);

            var run = Run(exePath);
            Assert.Equal(0, run.ExitCode);
        }

        /// <summary>
        /// M6 弱化：native 产物导入表不再含 GetModuleHandleW（自省不再读自身镜像）。
        /// </summary>
        [Fact]
        public void NativeImage_No_GetModuleHandleW_Import()
        {
            var target = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var exePath = EmitNative(@"function Main(): i32
{
    return 0
}", "cocoa-debug-rsds", target);

            var image = File.ReadAllBytes(exePath);
            var reader = PeImageReader.TryOpen(image);
            Assert.NotNull(reader);

            var importDir = reader!.GetDirectory(PeDataDirectoryEntry.Import);
            var imports = ImportTableReader.ReadAt(image, reader.RvaToFileOffset, importDir.VirtualAddress, importDir.Size);
            Assert.DoesNotContain(imports.SelectMany(i => i.Item2),
                e => string.Equals(e.Name, "GetModuleHandleW", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// COFF 符号表：文件头 PointerToSymbolTable/NumberOfSymbols 非零，blob 含函数符号名与行号
        /// （外部工具 IDA/dumpbin 可直接读符号，无需 PDB 文件）。
        /// </summary>
        [Fact]
        public void NativeImage_Has_CoffSymbolTable_With_FunctionNames()
        {
            var target = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var exePath = EmitNative(@"function Main(): i32
{
    return 0
}", "cocoa-coff", target);

            var image = File.ReadAllBytes(exePath);
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var symbolFileOffset = BitConverter.ToInt32(image, peOffset + 4 + 8);
            var symbolCount = BitConverter.ToInt32(image, peOffset + 4 + 12);
            Assert.True(symbolFileOffset > 0 && symbolCount > 0, "COFF 符号表应存在");

            // 字符串表起点 = 符号表之后；解析主符号名，断言含 Main。
            var stringTableStart = symbolFileOffset + symbolCount * 18;
            Assert.True(stringTableStart < image.Length, "字符串表越界");
            var stringTableLength = BitConverter.ToInt32(image, stringTableStart);
            Assert.True(stringTableLength > 4, "字符串表应含名字");

            var names = new List<string>();
            for (var i = 0; i < symbolCount; i++)
            {
                var offset = symbolFileOffset + i * 18;
                var name = Encoding.ASCII.GetString(image, offset, 8).TrimEnd('\0');
                if (BitConverter.ToUInt32(image, offset) == 0)
                {
                    var nameOffset = BitConverter.ToInt32(image, offset + 4);
                    if (nameOffset > 0 && nameOffset < stringTableLength)
                    {
                        var start = stringTableStart + nameOffset;
                        var end = start;
                        while (end < image.Length && image[end] != 0)
                        {
                            end++;
                        }

                        name = Encoding.ASCII.GetString(image, start, end - start);
                    }
                }

                names.Add(name);
            }

            Assert.Contains("Main", names);
            Assert.Contains(".text", names);
        }

        /// <summary>
        /// 导入表自包含于 .idata：dir1(index 1) / dir12(index 12) / descriptor.FirstThunk 均指向 .idata，
        /// IAT 与 INT 同区镜像（Borland/老 VS 形态），.cocoa 节置于末位——规避 IDA "imports destroyed"。
        /// </summary>
        [Fact]
        public void NativeImage_ImportSelfContained_InIdata_And_CocoaLast()
        {
            var target = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var exePath = EmitNative(@"function Main(): i32
{
    return 0
}", "cocoa-idata", target);

            var image = File.ReadAllBytes(exePath);
            var reader = PeImageReader.TryOpen(image);
            Assert.NotNull(reader);

            uint SecStart(int idx)
            {
                var peOffset = BitConverter.ToInt32(image, 0x3C);
                var numSections = BitConverter.ToInt16(image, peOffset + 6);
                var optSize = BitConverter.ToInt16(image, peOffset + 20);
                var sectionTable = peOffset + 24 + optSize;
                return BitConverter.ToUInt32(image, sectionTable + idx * 40 + 12);
            }

            var sectionCount = BitConverter.ToInt16(image, BitConverter.ToInt32(image, 0x3C) + 6);
            string SectionNameAt(int idx)
            {
                var peOffset = BitConverter.ToInt32(image, 0x3C);
                var optSize = BitConverter.ToInt16(image, peOffset + 20);
                var sectionTable = peOffset + 24 + optSize;
                return Encoding.ASCII.GetString(image, sectionTable + idx * 40, 8).TrimEnd('\0');
            }

            string SectionOf(uint rva)
            {
                for (var i = 0; i < sectionCount; i++)
                {
                    var va = SecStart(i);
                    var size = BitConverter.ToUInt32(image, BitConverter.ToInt32(image, 0x3C) + 24 + BitConverter.ToInt16(image, BitConverter.ToInt32(image, 0x3C) + 20) + i * 40 + 8);
                    if (rva >= va && rva < va + size)
                    {
                        return SectionNameAt(i);
                    }
                }

                return "?";
            }

            // dir1(index 1)/dir12(index 12) 均落在 .idata
            var importDir = reader!.GetDirectory(PeDataDirectoryEntry.Import);
            var iatDir = reader.GetDirectory(PeDataDirectoryEntry.Iat);
            Assert.True(importDir.VirtualAddress != 0 && iatDir.VirtualAddress != 0, "Import/IAT 目录缺失");
            Assert.Equal(".idata", SectionOf(importDir.VirtualAddress));
            Assert.Equal(".idata", SectionOf(iatDir.VirtualAddress));

            // descriptor.FirstThunk（加载器填充位）也指向 .idata
            var io = (int)reader.RvaToFileOffset(importDir.VirtualAddress);
            var firstThunk = BitConverter.ToUInt32(image, io + 16);
            Assert.Equal(".idata", SectionOf(firstThunk));

            // .cocoa 为末位节（自定义节放最后，前置标准节）
            var lastSection = SectionNameAt(sectionCount - 1);
            Assert.Equal(".cocoa", lastSection);
            Assert.Contains(SectionNameAt(0), new[] { ".text" });
        }

        /// <summary>从 PE 头数据目录（index 2 = Resource）取 .rsrc 根 RVA。</summary>
        private static uint GetResourceRva(byte[] image)
        {
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var magic = BitConverter.ToUInt16(image, peOffset + 24);
            // 数据目录起点：PE32+ 可选头 @ pe+24，标准字段 24B + Windows 特化 88B + 目录
            var dirBase = peOffset + 24 + (magic == 0x20B ? 24 + 88 : 28 + 68);
            return BitConverter.ToUInt32(image, dirBase + 2 * 8);
        }

        /// <summary>RVA → 文件偏移（依节表映射）。</summary>
        private static int RvaToOffset(byte[] image, uint rva)
        {
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var numSections = BitConverter.ToInt16(image, peOffset + 6);
            var optSize = BitConverter.ToInt16(image, peOffset + 20);
            var sectionTable = peOffset + 24 + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var offset = sectionTable + i * ImageSectionHeader.Size;
                var va = BitConverter.ToUInt32(image, offset + 12);
                var rawPtr = BitConverter.ToUInt32(image, offset + 20);
                var rawSize = BitConverter.ToUInt32(image, offset + 16);
                if (rva >= va && rva < va + rawSize)
                {
                    return (int)(rawPtr + (rva - va));
                }
            }

            return -1;
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void VersionResource_Embedded_VsVersionInfo(object platform)
        {
            // 版本资源（VS_VERSIONINFO → .rsrc RT_VERSION）：native 产物须携带合法资源目录 +
            // VS_FIXEDFILEINFO + StringFileInfo（CompanyName/ProductName/FileVersion 等），
            // 规避杀软对无资源裸 PE 的启发式误报。
            var target = (TargetPlatform)platform;
            var syntaxTree = SyntaxTree.Parse("function Main(): i32 { return 0 }");
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath("cocoa-version", target);
            var resourceOptions = new PeResourceOptions(new PeVersionInfo(
                CompanyName: "Acme Corp",
                ProductName: "Version Probe",
                FileDescription: "version resource probe",
                FileVersion: "1.2.3.4",
                ProductVersion: "2.0",
                AssemblyName: "cocoa-version",
                Copyright: "Copyright (c) 2026"));
            var diags = compilation.EmitNative("cocoa-version", exePath, target, 3, resourceOptions);
            Assert.Empty(diags);

            var image = File.ReadAllBytes(exePath);

            // 资源目录存在
            var rsrcRva = GetResourceRva(image);
            Assert.True(rsrcRva > 0, "Resource 数据目录缺失");

            // PeResourceTable 展开三层树：RT_VERSION(16) → id=1 → lang
            var root = PeResourceTable.Read(image, rva => (uint)RvaToOffset(image, rva), rsrcRva);
            Assert.NotNull(root);

            var versionType = root!.Children.FirstOrDefault(c => c.Id == 16); // RT_VERSION
            Assert.NotNull(versionType);
            var idNode = versionType!.Children.FirstOrDefault(c => c.Id == 1);
            Assert.NotNull(idNode);
            var langLeaf = idNode!.Children.FirstOrDefault();
            Assert.NotNull(langLeaf);
            Assert.NotNull(langLeaf!.Leaf);

            // 数据区含 VS_VERSION_INFO 键与版本字符串
            var dataOffset = RvaToOffset(image, langLeaf.Leaf!.Rva);
            Assert.True(dataOffset >= 0, "版本数据 RVA 映射失败");
            var blob = image.AsSpan(dataOffset, (int)langLeaf.Leaf.Size);
            // 键 "VS_VERSION_INFO"（UTF-16LE）紧跟 6 字节头（wLength/wValueLength/wType）
            var keyUtf16 = Encoding.Unicode.GetBytes("VS_VERSION_INFO");
            var found = IndexOf(blob, keyUtf16);
            Assert.True(found >= 0, "VS_VERSION_INFO 键缺失");

            // StringFileInfo 含 CompanyName 键值（BuildVersionInfo 写入）
            var company = Encoding.Unicode.GetBytes("Acme Corp");
            Assert.True(IndexOf(blob, company) >= 0, "CompanyName 值缺失");
            var fileVer = Encoding.Unicode.GetBytes("1.2.3.4");
            Assert.True(IndexOf(blob, fileVer) >= 0, "FileVersion 值缺失");

            // e2e：带资源节的产物仍正常运行
            var run = Run(exePath);
            Assert.Equal(0, run.ExitCode);
        }

        private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
            }

            return -1;
        }
    }
}