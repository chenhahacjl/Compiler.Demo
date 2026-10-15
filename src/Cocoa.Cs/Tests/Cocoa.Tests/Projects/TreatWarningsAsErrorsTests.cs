using System;
using System.IO;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class TreatWarningsAsErrorsTests : IDisposable
    {
        private readonly string _directory;
        private readonly StringWriter _out = new();

        public TreatWarningsAsErrorsTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_treat_" + Guid.NewGuid().ToString("N"));
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

        private CocoaProjectFile WriteProject(string treatLine, string extraItem = "", bool sourceMiss = true)
        {
            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>App</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><Configuration>Debug</Configuration><OutputPath>out</OutputPath>" + treatLine + "</PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Source Include=\"*.co\" />\n" +
                (sourceMiss ? "    <Source Include=\"gone*.co\" />\n" : string.Empty) +
                (extraItem ?? string.Empty) +
                "  </ItemGroup>\n" +
                "</Project>\n";
            var path = Path.Combine(_directory, "App.coproj");
            File.WriteAllText(path, xml);
            return CocoaProjectFile.Load(path);
        }

        private ProjectBuildResult Build(CocoaProjectFile project)
        {
            _out.GetStringBuilder().Length = 0;
            return ProjectBuilder.Build(
                project,
                new ProjectBuildOptions { CacheRoot = _directory, NoIncremental = true },
                _out);
        }

        [Fact]
        public void TreatAsErrors_UnmatchedSourcePattern_Fails()
        {
            var result = Build(WriteProject("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"));

            Assert.False(result.Success);
            Assert.Contains("error: source pattern 'gone*.co'", _out.ToString());
        }

        [Fact]
        public void TreatAsErrors_Default_WarnsAndSucceeds()
        {
            var result = Build(WriteProject(""));

            Assert.True(result.Success, _out.ToString());
            Assert.Contains("warning: source pattern 'gone*.co'", _out.ToString());
        }

        [Fact]
        public void TreatAsErrors_UnmatchedContent_Fails()
        {
            var result = Build(WriteProject("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
                "    <Content Include=\"missing/**\" CopyToOutput=\"true\" />\n",
                sourceMiss: false));

            Assert.False(result.Success);
            Assert.Contains("error: content pattern 'missing/**'", _out.ToString());
        }
    }
}