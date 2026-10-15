using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.CodeGen.PE;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Native.Metadata
{
    /// <summary>
    /// PE 资源增量验收：应用图标（RT_GROUP_ICON/RT_ICON）、通用资源（RT_RCDATA）内嵌 native .rsrc；
    /// IL 产物内嵌资源（ManifestResource → Assembly.GetManifestResourceStream 读取）。
    /// </summary>
    public class ResourceEmitTests
    {
        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        private static string NativeExePath(string name, TargetPlatform platform)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-resource-tests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name + (platform.Arch == Architecture.X86 ? "-x86" : "") + ".exe");
        }

        /// <summary>合成最小 .ico（ICONDIR + 1 条目 + 原始图像字节）。解析器只读头部 + 原样拷贝图像。</summary>
        private static byte[] MakeIco()
        {
            var image = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray();
            var ico = new byte[6 + 16 + image.Length];
            ico[0] = 0; ico[1] = 0;      // reserved
            ico[2] = 1; ico[3] = 0;      // type=1 (icon)
            ico[4] = 1; ico[5] = 0;      // count=1
            ico[6] = 16;                  // width=16
            ico[7] = 16;                  // height=16
            ico[8] = 0;                   // colorCount
            ico[9] = 0;                   // reserved
            ico[10] = 1; ico[11] = 0;    // planes=1
            ico[12] = 32; ico[13] = 0;   // bitCount=32
            ico[14] = (byte)image.Length; ico[15] = (byte)(image.Length >> 8); ico[16] = (byte)(image.Length >> 16); ico[17] = (byte)(image.Length >> 24);
            ico[18] = 22; ico[19] = 0; ico[20] = 0; ico[21] = 0; // imageOffset=22
            Array.Copy(image, 0, ico, 22, image.Length);
            return ico;
        }

        private static string EmitNativeWithResources(string name, TargetPlatform platform, PeResourceOptions options)
        {
            var syntaxTree = SyntaxTree.Parse("function Main(): i32 { return 0 }");
            var compilation = Compilation.Create(syntaxTree);
            var exePath = NativeExePath(name, platform);
            var diags = compilation.EmitNative(name, exePath, platform, 3, options);
            Assert.Empty(diags);
            Assert.True(File.Exists(exePath));
            return exePath;
        }

        private static int RvaToOffset(byte[] image, int rva)
        {
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var numSections = BitConverter.ToInt16(image, peOffset + 6);
            var optSize = BitConverter.ToInt16(image, peOffset + 20);
            var sectionTable = peOffset + 24 + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var s = sectionTable + i * 40;
                var virtAddr = BitConverter.ToInt32(image, s + 12);
                var virtSize = BitConverter.ToInt32(image, s + 8);
                var rawPtr = BitConverter.ToInt32(image, s + 20);
                var rawSize = BitConverter.ToInt32(image, s + 16);
                if (rva >= virtAddr && rva < virtAddr + Math.Max(virtSize, rawSize))
                {
                    return rawPtr + (rva - virtAddr);
                }
            }

            return -1;
        }

        private static uint GetResourceRva(byte[] image)
        {
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var magic = BitConverter.ToUInt16(image, peOffset + 24);
            // 数据目录起点：PE32+ 可选头 @ pe+24，标准字段 24B + Windows 特化 88B + 目录
            var dirBase = peOffset + 24 + (magic == 0x20B ? 24 + 88 : 28 + 68);
            return BitConverter.ToUInt32(image, dirBase + 2 * 8);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_Icon_Embeds_GroupIcon_And_Icons(object platform)
        {
            var target = (TargetPlatform)platform;
            var ico = MakeIco();
            var icoPath = Path.Combine(Path.GetTempPath(), "cocoa-resource-tests", "app" + (target.Arch == Architecture.X86 ? "-x86" : "") + ".ico");
            Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
            File.WriteAllBytes(icoPath, ico);

            var options = new PeResourceOptions(null, iconPath: icoPath);
            var exePath = EmitNativeWithResources("cocoa-icon", target, options);

            var image = File.ReadAllBytes(exePath);
            var rsrcRva = GetResourceRva(image);
            Assert.True(rsrcRva > 0, "Resource 数据目录缺失");

            var root = PeResourceTable.Read(image, rva => (uint)RvaToOffset(image, (int)rva), rsrcRva);
            Assert.NotNull(root);

            var groupIcon = root!.Children.FirstOrDefault(c => c.Id == (int)ResourceSectionBuilder.RtGroupIcon);
            Assert.NotNull(groupIcon);
            Assert.Equal(1u, groupIcon!.Children.Single().Id);
            Assert.NotNull(groupIcon.Children.Single().Children.Single().Leaf);

            var iconType = root.Children.FirstOrDefault(c => c.Id == (int)ResourceSectionBuilder.RtIcon);
            Assert.NotNull(iconType);
            Assert.Equal(1u, iconType!.Children.Single().Id);

            // 图标图像数据 = 合成字节
            var leaf = iconType.Children.Single().Children.Single().Leaf!;
            var dataOffset = RvaToOffset(image, (int)leaf.Rva);
            Assert.True(dataOffset >= 0);
            var data = image.AsSpan(dataOffset, (int)leaf.Size).ToArray();
            Assert.Equal(ico.Skip(22).ToArray(), data);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_GenericResource_Embeds_RtRcData(object platform)
        {
            var target = (TargetPlatform)platform;
            var payload = Encoding.UTF8.GetBytes("cocoa resource payload");
            var options = new PeResourceOptions(null, extraResources: new[]
            {
                new PeResourceItem(ResourceSectionBuilder.RtRcData, 1, ResourceSectionBuilder.DefaultLanguageId, payload),
            });
            var exePath = EmitNativeWithResources("cocoa-rcdata", target, options);

            var image = File.ReadAllBytes(exePath);
            var rsrcRva = GetResourceRva(image);
            Assert.True(rsrcRva > 0);

            var root = PeResourceTable.Read(image, rva => (uint)RvaToOffset(image, (int)rva), rsrcRva);
            var rcDataType = root!.Children.FirstOrDefault(c => c.Id == (int)ResourceSectionBuilder.RtRcData);
            Assert.NotNull(rcDataType);
            var leaf = rcDataType!.Children.Single().Children.Single().Leaf!;
            var dataOffset = RvaToOffset(image, (int)leaf.Rva);
            var data = image.AsSpan(dataOffset, (int)leaf.Size).ToArray();
            Assert.Equal(payload, data);
        }

        [Fact]
        public void Il_EmbeddedResource_Readable_Via_GetManifestResourceStream()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-il-embedded", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ilres.dll");

            var payload = Encoding.UTF8.GetBytes("hello embedded resource");
            var options = new PeResourceOptions(null, embeddedResources: new[]
            {
                new PeEmbeddedResource("data.txt", payload),
            });

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(SyntaxTree.Parse("function Main(args: string[]): i32 { return 0 }"));
            var diags = compilation.Emit("ilres", new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location }, path, IlTarget.Parse("net9.0"), emitLibrary: false, null, options);
            Assert.True(!diags.HasErrors(), string.Join("\n", diags.Select(d => d.Message)));

            var asm = Assembly.LoadFile(path);
            var names = asm.GetManifestResourceNames();
            Assert.Contains("data.txt", names);
            using var stream = asm.GetManifestResourceStream("data.txt");
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            Assert.Equal("hello embedded resource", reader.ReadToEnd());
        }
    }
}