using System;
using System.IO;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class ContentCopyTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _outputPath;
        private readonly StringWriter _out = new();

        public ContentCopyTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_content_" + Guid.NewGuid().ToString("N"));
            _outputPath = Path.Combine(_directory, "out");
            Directory.CreateDirectory(Path.Combine(_directory, "assets", "sub"));
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

        private CocoaProjectFile WriteProject(string contentXml, string name = "App")
        {
            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>" + name + "</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><Configuration>Debug</Configuration><OutputPath>out</OutputPath></PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Source Include=\"*.co\" />\n" +
                contentXml +
                "  </ItemGroup>\n" +
                "</Project>\n";
            var path = Path.Combine(_directory, name + ".coproj");
            File.WriteAllText(path, xml);
            return CocoaProjectFile.Load(path);
        }

        private ProjectBuildResult Build(CocoaProjectFile project, bool noIncremental = false)
        {
            _out.GetStringBuilder().Length = 0;
            return ProjectBuilder.Build(
                project,
                new ProjectBuildOptions { CacheRoot = _directory, NoIncremental = noIncremental },
                _out);
        }

        [Fact]
        public void Content_Glob_IsDeployedToOutputWithRelativePath()
        {
            File.WriteAllText(Path.Combine(_directory, "assets", "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(_directory, "assets", "sub", "b.txt"), "beta");

            var project = WriteProject("    <Content Include=\"assets/**\" CopyToOutput=\"true\" />\n");
            var result = Build(project);

            Assert.True(result.Success);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_outputPath, "assets", "a.txt")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(_outputPath, "assets", "sub", "b.txt")));
        }

        [Fact]
        public void Content_CopyToOutputFalse_IsNotDeployed()
        {
            File.WriteAllText(Path.Combine(_directory, "assets", "skip.txt"), "x");

            var project = WriteProject("    <Content Include=\"assets/*\" CopyToOutput=\"false\" />\n");
            var result = Build(project);

            Assert.True(result.Success);
            Assert.False(File.Exists(Path.Combine(_outputPath, "assets", "skip.txt")));
        }

        [Fact]
        public void Content_UnmatchedPattern_EmitsWarning()
        {
            File.WriteAllText(Path.Combine(_directory, "assets", "a.txt"), "a");

            var project = WriteProject("    <Content Include=\"missing/**\" CopyToOutput=\"true\" />\n");
            var result = Build(project);

            Assert.True(result.Success);
            Assert.Contains("content pattern 'missing/**'", _out.ToString());
        }

        [Fact]
        public void Content_IsDeployed_OnIncrementalUpToDate()
        {
            File.WriteAllText(Path.Combine(_directory, "assets", "a.txt"), "alpha");

            var project = WriteProject("    <Content Include=\"assets/*\" CopyToOutput=\"true\" />\n");
            Assert.True(Build(project).Success);

            // 二次构建：增量命中，Content 仍须保证部署（幂等）
            var second = Build(project);
            Assert.True(second.Success);
            Assert.True(second.UpToDate);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_outputPath, "assets", "a.txt")));
        }
    }
}