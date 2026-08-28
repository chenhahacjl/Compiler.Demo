using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class NewCommandTests
    {
        [Fact]
        public void New_ConsoleTemplate_CreatesProjectFiles()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new console MyApp -o .", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.True(File.Exists(Path.Combine(dir, "MyApp.cocproj")), "coproj missing");
            Assert.True(File.Exists(Path.Combine(dir, "main.co")), "main.co missing");

            var coproj = File.ReadAllText(Path.Combine(dir, "MyApp.cocproj"));
            Assert.Contains("name = MyApp", coproj);
            Assert.Contains("output = executable", coproj);
            Assert.Contains("entry = Main", coproj);
            Assert.Contains("dotnetRuntime = net48", coproj);

            var source = File.ReadAllText(Path.Combine(dir, "main.co"));
            Assert.Contains("function Main()", source);
        }

        [Fact]
        public void New_TemplateDefaultsToConsole_WhenFirstPositionalIsName()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new MyApp", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.True(File.Exists(Path.Combine(dir, "MyApp", "MyApp.cocproj")), "subdirectory project missing");
        }

        [Fact]
        public void New_LibraryTemplate_SetsOutputLibrary()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new library MyLib -o .", dir);

            Assert.True(exitCode == 0, stderr);
            var coproj = File.ReadAllText(Path.Combine(dir, "MyLib.cocproj"));
            Assert.Contains("output = library", coproj);
            Assert.True(File.Exists(Path.Combine(dir, "MyLib.co")));
        }

        [Fact]
        public void New_CocoaTemplate_SetsOutputCocoaAndNamespace()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new cocoa MyLib -o .", dir);

            Assert.True(exitCode == 0, stderr);
            var coproj = File.ReadAllText(Path.Combine(dir, "MyLib.cocproj"));
            Assert.Contains("output = cocoa", coproj);
            Assert.Contains("namespace MyLib", File.ReadAllText(Path.Combine(dir, "MyLib.co")));
        }

        [Fact]
        public void New_SolutionTemplate_CreatesCoslnAndSubProject()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new solution MySol -o .", dir);

            Assert.True(exitCode == 0, stderr);
            var solution = File.ReadAllText(Path.Combine(dir, "MySol.cosln"));
            Assert.Contains("[projects]", solution);
            Assert.Contains("MySol/MySol.cocproj", solution);
            Assert.True(File.Exists(Path.Combine(dir, "MySol", "MySol.cocproj")));
            Assert.True(File.Exists(Path.Combine(dir, "MySol", "main.co")));
        }

        [Fact]
        public void New_NameFromOutputDirectory_WhenNoNameGiven()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var projectDir = Path.Combine(dir, "hello");
            Directory.CreateDirectory(projectDir);
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new console -o hello", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.True(File.Exists(Path.Combine(projectDir, "hello.cocproj")), "name should default to output directory name");
        }

        [Fact]
        public void New_UnknownTemplate_Fails()
        {
            var dir = CliTestRunner.NewTempDir("new");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new MyApp -t foobar -o .", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("unknown template", stderr);
        }

        [Fact]
        public void New_ExistingProjectFile_Fails()
        {
            var dir = CliTestRunner.NewTempDir("new");
            File.WriteAllText(Path.Combine(dir, "MyApp.cocproj"), "name = MyApp\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("new console MyApp -o .", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("already exists", stderr);
        }
    }
}
