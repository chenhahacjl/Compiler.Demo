using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 阶段 6c-2：Native 导入改 OS 加载器静态解析的 PE 结构验证。
    /// 关键契约：描述符 FirstThunk 指向 data 区 IAT 槽；槽磁盘初值为 hintname RVA（fake-IAT 惯例），
    /// loader 启动时将初值替换为解析后的真实函数地址（初值为 0 的槽会被 loader 视为已填充而跳过）。
    /// </summary>
    public class NativeImportStaticResolutionTests
    {
        private static TargetPlatform X64 => new TargetPlatform(TargetOS.Windows, Architecture.X64);
        private static TargetPlatform X86 => new TargetPlatform(TargetOS.Windows, Architecture.X86);

        private static (byte[] Image, int DataStart, int DataEnd, int IdataStart, int IdataEnd, uint ImportDirRva) Parse(string exePath)
        {
            var image = File.ReadAllBytes(exePath);
            var eLfanew = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            var ntOffset = eLfanew;
            Assert.Equal(PeConstants.NtSignature, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ntOffset)));

            var optOffset = ntOffset + 24;
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optOffset));
            var isPe32 = (PeOptionalMagic)magic == PeOptionalMagic.Pe32;
            var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ntOffset + 20));
            var sectionOffset = optOffset + sizeOfOptionalHeader;

            var numSections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ntOffset + 6));
            var dataStart = -1;
            var dataEnd = -1;
            var idataStart = -1;
            var idataEnd = -1;
            for (var i = 0; i < numSections; i++)
            {
                var s = sectionOffset + i * 40;
                var name = System.Text.Encoding.ASCII.GetString(image.AsSpan(s, 8)).TrimEnd('\0');
                var vsz = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 8));
                var va = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 12));
                var end = va + vsz;
                if (name == ".data")
                {
                    dataStart = va;
                    dataEnd = end;
                }
                else if (name == ".idata")
                {
                    idataStart = va;
                    idataEnd = end;
                }
            }

            var importDir = isPe32
                ? BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optOffset + 96 + 8 * 1))
                : BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optOffset + 112 + 8 * 1));
            return (image, dataStart, dataEnd, idataStart, idataEnd, importDir);
        }

        private static string Compile(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-tests");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            var exePath = Path.Combine(directory, name + suffix + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);
            Assert.Empty(diagnostics);
            return exePath;
        }

        private const string ImportTwoDlls = @"
import kernel32.dll

stdcall function ExitProcess(exitCode: int)

import user32.dll

stdcall function MessageBoxW(hWnd: int, lpText: int, lpCaption: int, uType: int): int

function main()
{
    var m = MessageBoxW(0, 0, 0, 0)
    ExitProcess(m)
}";

        [Fact]
        public void Import_DescriptorFirstThunk_PointsIntoDataSection()
        {
            var exePath = Compile(ImportTwoDlls, "import-ft-data-x64", X64);
            var (image, dataStart, dataEnd, idataStart, idataEnd, importDirRva) = Parse(exePath);
            Assert.True(dataStart > 0 && idataStart > 0, "expected .data and .idata sections");

            var eLfanew = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            var optOffset = eLfanew + 24;
            var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(eLfanew + 20));
            var sectionOffset = optOffset + sizeOfOptionalHeader;
            var numSections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(eLfanew + 6));
            var importDirOffset = -1;
            for (var i = 0; i < numSections; i++)
            {
                var s = sectionOffset + i * 40;
                var va = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 12));
                var rawPtr = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 20));
                if (importDirRva >= va && importDirRva < va + (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 8)))
                {
                    importDirOffset = rawPtr + (int)(importDirRva - va);
                    break;
                }
            }

            Assert.True(importDirOffset >= 0, "import directory must resolve into a section");
            var descriptor = ImageImportDescriptor.Read(image.AsSpan(importDirOffset, ImageImportDescriptor.SizeOfEntry));
            var firstThunk = (int)descriptor.FirstThunk;
            Assert.InRange(firstThunk, dataStart, dataEnd - ImageThunkData64.SizeOfEntry);
            Assert.NotEqual(0u, descriptor.OriginalFirstThunk);
        }

        [Fact]
        public void Import_SlotsInitializedToHintNameRvas()
        {
            var exePath = Compile(ImportTwoDlls, "import-slot-init-x64", X64);
            var (image, dataStart, dataEnd, idataStart, idataEnd, importDirRva) = Parse(exePath);

            var eLfanew = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            var optOffset = eLfanew + 24;
            var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(eLfanew + 20));
            var sectionOffset = optOffset + sizeOfOptionalHeader;
            var numSections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(eLfanew + 6));
            var sectionRvas = new int[numSections];
            var sectionRawPtrs = new int[numSections];
            var sectionVsz = new int[numSections];
            for (var i = 0; i < numSections; i++)
            {
                var s = sectionOffset + i * 40;
                sectionRvas[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 12));
                sectionVsz[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 8));
                sectionRawPtrs[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 20));
            }

            Func<uint, uint> toOffset = (uint rva) =>
            {
                for (var i = 0; i < numSections; i++)
                {
                    if (rva >= (uint)sectionRvas[i] && rva < (uint)(sectionRvas[i] + sectionVsz[i]))
                    {
                        return (uint)(sectionRawPtrs[i] + (int)rva - sectionRvas[i]);
                    }
                }

                return 0u;
            };

            // 两个 DLL 的描述符都指向 data 区槽
            var firstThunkRvas = new System.Collections.Generic.List<uint>();
            var dllNames = new System.Collections.Generic.List<string>();
            var fileOffset = toOffset(importDirRva);
            for (uint index = 0; ; index++)
            {
                var descriptorOffset = fileOffset + index * ImageImportDescriptor.SizeOfEntry;
                var descriptor = ImageImportDescriptor.Read(image.AsSpan((int)descriptorOffset, ImageImportDescriptor.SizeOfEntry));
                if (descriptor.IsEndOfArray)
                {
                    break;
                }

                firstThunkRvas.Add(descriptor.FirstThunk);
                var nameOffset = toOffset(descriptor.Name);
                var nameEnd = nameOffset;
                while (image[nameEnd] != 0)
                {
                    nameEnd++;
                }

                dllNames.Add(System.Text.Encoding.ASCII.GetString(image, (int)nameOffset, (int)(nameEnd - nameOffset)));
                Assert.InRange((int)descriptor.FirstThunk, dataStart, dataEnd - 8);
            }

            Assert.Equal(2, firstThunkRvas.Count);

            var expectedCounts = new[] { 1, 1 };
            for (var dll = 0; dll < firstThunkRvas.Count; dll++)
            {
                var baseSlot = firstThunkRvas[dll];
                for (var i = 0; i < expectedCounts[dll]; i++)
                {
                    var slotFileOffset = toOffset(baseSlot + (uint)(i * 8));
                    var slotValue = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)slotFileOffset, 8));
                    Assert.True(slotValue != 0, $"slot {i} of {dllNames[dll]} must be non-zero (fake-IAT hintname RVA)");
                    Assert.InRange((int)slotValue, idataStart, idataEnd - 1);
                }
            }
        }
    }
}