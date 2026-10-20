using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.Targeting;
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Il
{
    /// <summary>IL 后端 Portable PDB 产物验证：旁置 .pdb、SRM PortablePdbReader 可读、含 Document + MethodDebugInformation。</summary>
    public class PortablePdbTests
    {
        [Fact]
        public void ManagedEmit_Produces_PortablePdb_Readable()
        {
            const string source = "function Main(): i32\n{\n    let a = 1\n    let b = 2\n    return a + b\n}\n";
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-ppdb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var coPath = Path.Combine(dir, "main.co");
                File.WriteAllText(coPath, source);
                var compilation = Compilation.Create(SyntaxTree.Load(coPath));

                IlTarget.TryParse("net9.0", out var target);
                var dllPath = Path.Combine(dir, "App.dll");
                var diagnostics = compilation.Emit("App", new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location }, dllPath, target, emitLibrary: true);
                Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));

                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                Assert.True(File.Exists(pdbPath), "Portable PDB 未旁置生成");

                // 程序集 Debug 目录（index 6）→ CodeView/RSDS，GUID 匹配 PDB 的 debug metadata id（VS 自动加载符号）
                var assemblyBytes = File.ReadAllBytes(dllPath);
                var (debugType, rsdsGuid) = ReadCodeView(assemblyBytes);
                Assert.Equal(2u, debugType); // CodeView
                var expectedGuid = PdbWriter.MakeGuid("App");
                using (var pdbFs = File.OpenRead(pdbPath))
                using (var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbFs))
                {
                    var pdbId = pdbProvider.GetMetadataReader().DebugMetadataHeader!.Id;
                    Assert.Equal(expectedGuid.ToByteArray(), pdbId.Take(16).ToArray());
                }

                Assert.Equal(expectedGuid, rsdsGuid);

                using var fs = File.OpenRead(pdbPath);
                using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
                var md = provider.GetMetadataReader();
                Assert.True(md.GetTableRowCount(TableIndex.Document) > 0, "Document 表为空");
                Assert.True(md.GetTableRowCount(TableIndex.MethodDebugInformation) > 0, "MethodDebugInformation 表为空");

                // Main 的语句级序列点应解码为 行 3/4/5（let a / let b / return），IL 偏移严格递增
                var mainFound = false;
                foreach (var mdi in md.MethodDebugInformation)
                {
                    var methodName = md.GetString(md.GetMethodDefinition(mdi.ToDefinitionHandle()).Name);
                    if (methodName != "Main") continue;

                    mainFound = true;
                    var info = md.GetMethodDebugInformation(mdi);
                    var points = info.GetSequencePoints().ToList();
                    Assert.Equal(new[] { 3, 4, 5 }, points.Select(p => p.StartLine).ToArray());
                    var offsets = points.Select(p => p.Offset).ToArray();
                    for (var i = 1; i < offsets.Length; i++)
                    {
                        Assert.True(offsets[i] > offsets[i - 1], "序列点 IL 偏移应严格递增");
                    }

                    // 文档路径必须指向真实 .co 源文件——VS 靠它自动打开源码并跳到断点行
                    var docNameBlob = md.GetBlobBytes(md.GetDocument(info.Document).Name);
                    var docName = System.Text.Encoding.UTF8.GetString(docNameBlob, 1, docNameBlob.Length - 1).TrimEnd('\0'); // 跳路径格式码 + 尾 NUL
                    Assert.True(string.Equals(coPath, docName, StringComparison.OrdinalIgnoreCase),
                        $"Document 应为源 .co 路径：{coPath} != {docName}");
                    break;
                }

                Assert.True(mainFound, "未找到 Main 的调试信息");
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
        private static (uint DebugType, Guid RsdsGuid) ReadCodeView(byte[] image)
        {
            var eLfanew = BitConverter.ToInt32(image, 0x3C);
            var isPe32 = BitConverter.ToUInt16(image, eLfanew + 24) == 0x10B;
            var numSections = BitConverter.ToUInt16(image, eLfanew + 6);
            var optSize = BitConverter.ToUInt16(image, eLfanew + 20);
            var secTab = eLfanew + 24 + optSize;
            var dirBase = eLfanew + 24 + (isPe32 ? 96 : 112);
            var debugRva = BitConverter.ToUInt32(image, dirBase + 6 * 8);
            if (debugRva == 0)
            {
                return (0, Guid.Empty);
            }

            int RvaToOffset(uint rva)
            {
                for (var i = 0; i < numSections; i++)
                {
                    var st = secTab + i * 40;
                    var va = BitConverter.ToUInt32(image, st + 12);
                    var vs = BitConverter.ToUInt32(image, st + 8);
                    var rawPtr = BitConverter.ToUInt32(image, st + 20);
                    if (rva >= va && rva < va + vs)
                    {
                        return (int)(rawPtr + (rva - va));
                    }
                }

                return -1;
            }

            var dirOff = RvaToOffset(debugRva);
            if (dirOff < 0)
            {
                return (0, Guid.Empty);
            }

            var debugType = BitConverter.ToUInt32(image, dirOff + 12);
            var rsdsRva = BitConverter.ToUInt32(image, dirOff + 20);
            var rsdsOff = RvaToOffset(rsdsRva);
            if (rsdsOff < 0)
            {
                return (debugType, Guid.Empty);
            }

            var guid = new Guid(image.Skip(rsdsOff + 4).Take(16).ToArray());
            return (debugType, guid);
        }
    }
}
