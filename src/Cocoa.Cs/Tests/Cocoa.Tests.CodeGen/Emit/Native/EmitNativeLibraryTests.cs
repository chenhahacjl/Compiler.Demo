using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.Targeting;
using Xunit;
using Architecture = Cocoa.Targeting.Architecture;

namespace Cocoa.Tests.CodeGen.Emit.Native
{
    /// <summary>
    /// S1（native-s1 实施计划）：`EmitNativeLibrary` 管线验收——任 `.coa` → native 共享 DLL（每 coa 一 DLL）。
    /// PE DLL 形态（entry=0、IMAGE_FILE_DLL）+ 全导出（库函数 cocoa_+mangle(FnKey)、运行时原语 cocoa_rt_*）。
    /// 用**自包含夹具 coa**（纯 i32 运算，模块内自洽）驱动全链：MirToLir 库模式 → RuntimeEmitterLir →
    /// LirToAssembler → PeFileWriter（isDll）。
    /// 注：完整 System.Core.coa 含 native 尚未支持的成员（如 decimal 打印），全量编译为 S5 开放项，
    /// 本测试以夹具验证管线机制正确性。
    /// </summary>
    public class EmitNativeLibraryTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Windows, Architecture.X64);

        private const string FixtureSource = @"
namespace Fixture {
    public class Geometry
    {
        private field _x: i32

        public constructor(x: i32)
        {
            _x = x
        }

        public property X: i32 { get set }

        public function Double(): i32
        {
            return _x * 2
        }

        public static function FromZero(): Geometry
        {
            return new Geometry(0)
        }
    }

    public function Sum(a: i32, b: i32): i32
    {
        return a + b
    }
}
";

        private static (CoaProgram Program, string DllPath) EmitFixtureLibrary()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-native-lib-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var coaPath = Path.Combine(dir, "Fixture.coa");
            var dllPath = Path.Combine(dir, "Fixture.Native.dll");

            var syntaxTree = SyntaxTree.Parse(FixtureSource);
            var diagnostics = Compilation.Create(syntaxTree).EmitCoa("Fixture", coaPath);
            Assert.True(!diagnostics.HasErrors(), "fixture coa diags: " + string.Join(" | ", diagnostics.Select(d => d.Message)));

            var external = SystemLibrary.Load();
            var coa = CoaSerializer.Load(coaPath, external);
            EmitNativeLibrary.Emit(coa, dllPath, Platform);
            return (coa, dllPath);
        }

        [Fact]
        public void Emit_Produces_DllHead_EntryZero()
        {
            var (_, dllPath) = EmitFixtureLibrary();
            var image = File.ReadAllBytes(dllPath);

            var reader = PeImageReader.TryOpen(image);
            Assert.NotNull(reader);
            Assert.Equal(0u, reader!.EntryPointRva);

            // COFF Characteristics：IMAGE_FILE_DLL（0x2000）置位
            var eLfanew = BitConverter.ToInt32(image, 0x3C);
            var characteristics = BitConverter.ToUInt16(image, eLfanew + 4 + 18);
            Assert.True((characteristics & 0x2000) != 0, $"expected IMAGE_FILE_DLL, got 0x{characteristics:X4}");
        }

        [Fact]
        public void Emit_Exports_RuntimePrimitives()
        {
            var (_, dllPath) = EmitFixtureLibrary();
            var image = File.ReadAllBytes(dllPath);
            AssertExportResolvable(image, NativeSymbolMangling.RtExportName("Alloc"));
            AssertExportResolvable(image, NativeSymbolMangling.RtExportName("NewArray"));
            AssertExportResolvable(image, NativeSymbolMangling.RtExportName("IntToString"));
        }

        [Fact]
        public void Emit_Exports_LibraryFunctions_ByFnKey()
        {
            var (coa, dllPath) = EmitFixtureLibrary();
            var image = File.ReadAllBytes(dllPath);

            // 类实例方法 / 属性访问器 / 构造函数 / 类静态 / 顶层函数 逐一验证导出名可解析
            var double1 = coa.Functions.Single(f => f.ContainingClass?.FullName == "Fixture.Geometry" && f.Name == "Double");
            AssertExportResolvable(image, NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(double1)));

            var ctor = coa.Functions.Single(f => f.ContainingClass?.FullName == "Fixture.Geometry" && f.IsConstructor && !f.IsStatic);
            AssertExportResolvable(image, NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(ctor)));

            var staticFn = coa.Functions.Single(f => f.ContainingClass?.FullName == "Fixture.Geometry" && f.Name == "FromZero");
            AssertExportResolvable(image, NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(staticFn)));

            var topLevel = coa.Functions.Single(f => f.ContainingClass == null && f.Name == "Sum");
            AssertExportResolvable(image, NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(topLevel)));
        }

        [Fact]
        public void Emit_Exports_NonEmptySet()
        {
            var (_, dllPath) = EmitFixtureLibrary();
            var image = File.ReadAllBytes(dllPath);
            var reader = PeImageReader.TryOpen(image)!;
            var dir = reader.GetDirectory(PeDataDirectoryEntry.Export);
            Assert.True(dir.VirtualAddress != 0, "expected an export directory (full-public exports)");
        }

        [Fact]
        public void Emit_Dll_ResolvableViaGetProcAddress()
        {
            var (coa, dllPath) = EmitFixtureLibrary();
            var double1 = coa.Functions.Single(f => f.ContainingClass?.FullName == "Fixture.Geometry" && f.Name == "Double");
            var exportName = NativeSymbolMangling.ExportName(CoaFunctionNaming.FnKey(double1));

            var handle = LoadLibraryExW(dllPath, IntPtr.Zero, 0 /* 正常加载：解析 IAT 导入，验证 DLL 可装载 */);
            Assert.NotEqual(IntPtr.Zero, handle);
            try
            {
                Assert.NotEqual(IntPtr.Zero, GetProcAddress(handle, NativeSymbolMangling.RtExportName("Alloc")));
                Assert.NotEqual(IntPtr.Zero, GetProcAddress(handle, exportName));
            }
            finally
            {
                FreeLibrary(handle);
            }
        }

        private static void AssertExportResolvable(byte[] image, string exportName)
        {
            var reader = PeImageReader.TryOpen(image)!;
            var dir = reader.GetDirectory(PeDataDirectoryEntry.Export);
            Assert.True(dir.VirtualAddress != 0 && dir.Size >= ImageExportDirectory.SizeOfEntry,
                $"no export directory to resolve '{exportName}'");

            var dirOffset = reader.RvaToFileOffset(dir.VirtualAddress);
            var exportDir = ImageExportDirectory.Read(image.AsSpan((int)dirOffset, ImageExportDirectory.SizeOfEntry));
            var index = PeExportIndex.FromDirectory(exportDir) with { ExportDirRva = dir.VirtualAddress };

            uint rva;
            Assert.True(
                PeExportTable.TryGetExportRva(image, reader.RvaToFileOffset, index, exportName, out rva),
                $"export '{exportName}' not found");
            Assert.NotEqual(0u, rva);
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = false)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);
    }
}