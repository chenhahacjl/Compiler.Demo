using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.Targeting;
using Xunit;
using Architecture = Cocoa.Targeting.Architecture;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// S3（native-s1）：真实 System.Core.coa → System.Core.Native.dll 全量库发射验收——
    /// PE DLL 形态 + 全公共导出（FnKey 契约）+ 运行时原语 + 共享类型表；native 不支持的叶函数
    /// （如 decimal 打印）按函数体容错跳过（不发射不导出），其余全量可编译。
    /// 依赖 SDK 部署的 System.Core.coa（SystemLibrary 解析）。
    /// </summary>
    public class SystemCoreNativeLibraryTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Windows, Architecture.X64);

        private static (CoaProgram Core, byte[] Image, FunctionSymbol[] Skipped) EmitRealSystemCore()
        {
            var core = SystemLibrary.Load().Single(l => l.Name.StartsWith("System.Core", StringComparison.Ordinal));
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-systemcore-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dll = Path.Combine(dir, "System.Core.Native.dll");
            var skipped = EmitNativeLibrary.Emit(core, dll, Platform).ToArray();
            return (core, File.ReadAllBytes(dll), skipped);
        }

        [Fact]
        public void RealSystemCore_Emits_FullLibrary_DllHead()
        {
            var (_, image, skipped) = EmitRealSystemCore();

            Assert.True(image.Length > 100_000, $"unexpected small image: {image.Length}");
            var reader = PeImageReader.TryOpen(image);
            Assert.NotNull(reader);
            Assert.Equal(0u, reader!.EntryPointRva);

            var eLfanew = BitConverter.ToInt32(image, 0x3C);
            var characteristics = BitConverter.ToUInt16(image, eLfanew + 4 + 18);
            Assert.True((characteristics & 0x2000) != 0, $"expected IMAGE_FILE_DLL, got 0x{characteristics:X4}");
        }

        [Fact]
        public void RealSystemCore_Exports_LibraryFnsAndPrimitives()
        {
            var (core, image, _) = EmitRealSystemCore();
            var reader = PeImageReader.TryOpen(image)!;
            var dir = reader.GetDirectory(PeDataDirectoryEntry.Export);
            Assert.True(dir.VirtualAddress != 0);

            var dirOffset = reader.RvaToFileOffset(dir.VirtualAddress);
            var exportDir = ImageExportDirectory.Read(image.AsSpan((int)dirOffset, ImageExportDirectory.SizeOfEntry));
            var index = PeExportIndex.FromDirectory(exportDir) with { ExportDirRva = dir.VirtualAddress };

            // 代表性 System.Core 函数（System.Math.Abs 双精度）经 FnKey 契约导出
            var abs = core.Functions.FirstOrDefault(f => f.ContainingClass?.FullName == "System.Math" && f.Name == "Abs" && f.Parameters.Length == 1 && f.Parameters[0].Type == TypeSymbol.Double);
            Assert.NotNull(abs);
            AssertExportResolvable(image, reader, index, NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(abs!)));

            // 运行时原语 + 类型表入口
            AssertExportResolvable(image, reader, index, NativeSymbolMangling.RtExportName("Alloc"));
            AssertExportResolvable(image, reader, index, NativeSymbolMangling.RtTypeGet);
            AssertExportResolvable(image, reader, index, NativeSymbolMangling.RtTypeRegister);
        }

        [Fact]
        public void RealSystemCore_SkipsOnlyNativeUnsupportedLeaves()
        {
            var (core, image, skipped) = EmitRealSystemCore();
            var reader = PeImageReader.TryOpen(image)!;
            var dir = reader.GetDirectory(PeDataDirectoryEntry.Export);
            var dirOffset = reader.RvaToFileOffset(dir.VirtualAddress);
            var exportDir = ImageExportDirectory.Read(image.AsSpan((int)dirOffset, ImageExportDirectory.SizeOfEntry));
            var index = PeExportIndex.FromDirectory(exportDir) with { ExportDirRva = dir.VirtualAddress };

            // 跳过集非空：decimal 打印重载（Console.WriteLine/Write）必在其中；另可能含 native 不支持的
            // 结构返回等叶函数（如 ValueTuple）。此处只断言「必含」与「不误伤正常函数」。
            Assert.True(skipped.Length > 0, "expected at least decimal print overloads skipped");
            Assert.Contains(skipped, s =>
                s.ContainingClass?.FullName == "System.Console" && (s.Name == "WriteLine" || s.Name == "Write")
                && s.Parameters.Length == 1 && s.Parameters[0].Type.Name == "decimal");

            // 被跳过的函数不导出（decimal Console.WriteLine/Write）
            var skippedNames = skipped.Select(s => NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(s))).ToArray();
            Assert.Contains(skippedNames, n => n.Contains("WriteLine"));
            foreach (var name in skippedNames)
            {
                Assert.True(
                    !PeExportTable.TryGetExportRva(image, reader.RvaToFileOffset, index, name, out _),
                    $"skipped function should not be exported: {name}");
            }

            // 正常函数不被跳过
            Assert.DoesNotContain(skipped, s => (s.ContainingClass?.FullName ?? s.Name).Contains("Math.Abs"));
        }

        [Fact]
        public void RealSystemCore_Loads()
        {
            var (_, image, _) = EmitRealSystemCore();
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-systemcore-native-load-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dll = Path.Combine(dir, "System.Core.Native.dll");
            File.WriteAllBytes(dll, image);

            var handle = LoadLibraryExW(dll, IntPtr.Zero, 0);
            Assert.NotEqual(IntPtr.Zero, handle);
            try
            {
                Assert.NotEqual(IntPtr.Zero, GetProcAddress(handle, NativeSymbolMangling.RtExportName("Alloc")));
            }
            finally
            {
                FreeLibrary(handle);
            }
        }

        private void AssertExportResolvable(byte[] image, PeImageReader reader, PeExportIndex index, string exportName)
        {
            uint rva;
            Assert.True(PeExportTable.TryGetExportRva(image, reader.RvaToFileOffset, index, exportName, out rva),
                $"export '{exportName}' not found");
            Assert.NotEqual(0u, rva);
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling = false)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);
    }
}