using System;
using System.IO;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Projects
{
    /// <summary>
    /// T3.3：linux-native .coproj 生成立面——产物无扩展名（ELF 惯例）+ ELF 魔数；
    /// Backend=native + TargetOS=linux + x64 合法通过校验矩阵。
    /// </summary>
    public class LinuxNativeProjectEmitTests : IDisposable
    {
        private readonly string _directory;
        private readonly StringWriter _out = new();

        public LinuxNativeProjectEmitTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_linuxproj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "main.co"),
                "using System\n\nfunction Main()\n{\n    Console.WriteLine(\"ok\")\n}\n");
        }

        public void Dispose()
        {
            _out.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private CocoaProjectFile WriteProject()
        {
            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>App</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\"><Backend>native</Backend><Platform>x64</Platform><TargetOS>linux</TargetOS></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\"><OutputType>Executable</OutputType></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><OutputPath>out</OutputPath></PropertyGroup>\n" +
                "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                "</Project>\n";
            var path = Path.Combine(_directory, "App.coproj");
            File.WriteAllText(path, xml);
            return CocoaProjectFile.Load(path);
        }

        [Fact]
        public void LinuxNative_EmitsExtensionlessElf()
        {
            var project = WriteProject();
            var result = ProjectBuilder.Build(
                project,
                new ProjectBuildOptions { CacheRoot = _directory, NoIncremental = true },
                _out);

            Assert.True(result.Success, _out.ToString());

            var outputPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName());
            Assert.True(File.Exists(outputPath), $"output '{outputPath}' not found");
            Assert.False(outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }

        [Fact]
        public void LinuxNative_TargetOsOverride_OverridesProject()
        {
            // T3 --os：命令行 TargetOsOverride=linux 覆盖项目声明（项目本身未声明 TargetOS，默认 windows）。
            var project = WriteProject();
            var result = ProjectBuilder.Build(
                project,
                new ProjectBuildOptions
                {
                    CacheRoot = _directory,
                    NoIncremental = true,
                    TargetOsOverride = Cocoa.Targeting.TargetOS.Linux,
                },
                _out);

            Assert.True(result.Success, _out.ToString());

            var outputPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName(Cocoa.Targeting.TargetOS.Linux, Cocoa.Targeting.CodeBackend.Native));
            Assert.True(File.Exists(outputPath), $"output '{outputPath}' not found");
            Assert.False(outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }
    }
}