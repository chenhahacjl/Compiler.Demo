using System;
using System.IO;
using Cocoa.Build;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.Integration.Projects
{
    /// <summary>
    /// 增量判定（.NET Inputs/Outputs 时间戳模型）与默认输出布局（bin/&lt;Config&gt;/…）测试。
    /// 取代旧 BuildCache 指纹缓存；时间戳在测试中显式设定以避免时钟粒度。
    /// </summary>
    public class IncrementalCheckTests : IDisposable
    {
        private readonly string _directory;

        public IncrementalCheckTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa-incremental-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        private static void Stamp(string path, DateTime utc)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "x");
            }

            File.SetLastWriteTimeUtc(path, utc);
        }

        [Fact]
        public void IsUpToDate_False_WhenOutputMissing()
        {
            var outFile = Path.Combine(_directory, "App.exe");
            Assert.False(IncrementalCheck.IsUpToDate(outFile, new[] { Path.Combine(_directory, "a.co") }));
        }

        [Fact]
        public void IsUpToDate_False_WhenInputNewerThanOutput()
        {
            var output = Path.Combine(_directory, "App.exe");
            var source = Path.Combine(_directory, "a.co");
            Stamp(output, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Stamp(source, new DateTime(2020, 1, 1, 0, 0, 2, DateTimeKind.Utc));

            Assert.False(IncrementalCheck.IsUpToDate(output, new[] { source }));
        }

        [Fact]
        public void IsUpToDate_True_WhenOutputNewerThanAllInputs()
        {
            var output = Path.Combine(_directory, "App.exe");
            var source = Path.Combine(_directory, "a.co");
            var reference = Path.Combine(_directory, "ref.dll");
            Stamp(output, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Stamp(source, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Stamp(reference, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.True(IncrementalCheck.IsUpToDate(output, new[] { source, reference }));
        }

        [Fact]
        public void IsUpToDate_False_WhenInputMissing()
        {
            var output = Path.Combine(_directory, "App.exe");
            Stamp(output, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.False(IncrementalCheck.IsUpToDate(output, new[] { Path.Combine(_directory, "missing.co") }));
        }

        private static CocoaProjectFile LoadProject(
            string dir, string name = "App", string outputType = "Executable",
            string? platform = "AnyCPU", string? tfm = null, string? backend = null, string? outputPath = null)
        {
            var coproj = $@"<Project Version=""1"">
  <PropertyGroup Label=""Assembly""><AssemblyName>{name}</AssemblyName></PropertyGroup>
  <PropertyGroup Label=""Target""><Platform>{platform}</Platform></PropertyGroup>
  <PropertyGroup Label=""Output""><OutputType>{outputType}</OutputType></PropertyGroup>
  {(tfm == null ? "" : $@"  <PropertyGroup Label=""Target2""><TargetFramework>{tfm}</TargetFramework></PropertyGroup>")}
  {(backend == null ? "" : $@"  <PropertyGroup Label=""Build""><Backend>{backend}</Backend></PropertyGroup>")}
  {(outputPath == null ? "" : $@"  <PropertyGroup Label=""Build2""><OutputPath>{outputPath}</OutputPath></PropertyGroup>")}
  <ItemGroup><Source Include=""*.co"" /></ItemGroup>
</Project>";
            var path = Path.Combine(dir, name + ".coproj");
            File.WriteAllText(path, coproj);
            return CocoaProjectFile.Load(path);
        }

        [Fact]
        public void Layout_Cocoa_Output_IsBinConfig()
        {
            var project = LoadProject(_directory, outputType: "Cocoa");
            Assert.Equal(Path.Combine(_directory, "bin", "Debug"), project.GetOutputDirectory());
        }

        [Fact]
        public void Layout_Native_IsBinConfigNativeArch()
        {
            var project = LoadProject(_directory, platform: "x64", backend: "native");
            Assert.Equal(Path.Combine(_directory, "bin", "Debug", "native", "x64"), project.GetOutputDirectory());
        }

        [Fact]
        public void Layout_Managed_IsBinConfigManagedTfm()
        {
            var project = LoadProject(_directory, tfm: "net48");
            Assert.Equal(Path.Combine(_directory, "bin", "Debug", "managed", "net48"), project.GetOutputDirectory());
        }

        [Fact]
        public void Layout_ManagedNet_Tfm_Segment()
        {
            var project = LoadProject(_directory, tfm: "net9.0");
            Assert.Equal(Path.Combine(_directory, "bin", "Debug", "managed", "net9.0"), project.GetOutputDirectory());
        }

        [Fact]
        public void Layout_ManagedNet9_EffectiveOverride()
        {
            var project = LoadProject(_directory, tfm: "net48");
            var dir = project.GetOutputDirectory(ProjectConfiguration.Debug, CodeBackend.ManagedNet, Architecture.X64, "net9.0");
            // 方案 A：managed 不写 arch（AnyCPU IL 平台无关）
            Assert.Equal(Path.Combine(_directory, "bin", "Debug", "managed", "net9.0"), dir);
        }

        [Fact]
        public void Layout_ExplicitOutputPath_Wins()
        {
            var project = LoadProject(_directory, outputPath: "build");
            Assert.Equal(Path.Combine(_directory, "build"), project.GetOutputDirectory());
        }
    }
}