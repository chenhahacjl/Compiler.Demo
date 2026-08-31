using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class ReferenceCommandTests
    {
        private static string WriteProject(string dir, string content = "name = App\noutput = executable\n\n[sources]\n*.co\n")
        {
            var path = Path.Combine(dir, "App.cocproj");
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void AddReference_CreatesReferencesSection()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);

            var (exitCode, stdout, stderr) = CliTestRunner.Run("add reference -p App.cocproj ../Libs/MyLib.coa", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Added reference", stdout);

            var text = File.ReadAllText(Path.Combine(dir, "App.cocproj"));
            Assert.Contains("[references]", text);
            Assert.Contains("../Libs/MyLib.coa", text);
        }

        [Fact]
        public void AddReference_Duplicate_IsIdempotent()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);

            var first = CliTestRunner.Run("add reference -p App.cocproj ../Libs/MyLib.coa", dir);
            Assert.True(first.ExitCode == 0, first.Stderr);

            var second = CliTestRunner.Run("add reference -p App.cocproj ../Libs/MyLib.coa", dir);
            Assert.True(second.ExitCode == 0, second.Stderr);
            Assert.Contains("already present", second.Stdout);

            var text = File.ReadAllText(Path.Combine(dir, "App.cocproj"));
            var count = 0;
            foreach (var line in text.Split('\n'))
            {
                if (line.Trim() == "../Libs/MyLib.coa")
                {
                    count++;
                }
            }

            Assert.Equal(1, count);
        }

        [Fact]
        public void RemoveReference_RemovesLine()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir, "name = App\noutput = executable\n\n[sources]\n*.co\n\n[references]\n../Libs/MyLib.coa\n");

            var (exitCode, stdout, stderr) = CliTestRunner.Run("remove reference -p App.cocproj ../Libs/MyLib.coa", dir);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Removed reference", stdout);
            Assert.DoesNotContain("../Libs/MyLib.coa", File.ReadAllText(Path.Combine(dir, "App.cocproj")));
        }

        [Fact]
        public void RemoveReference_NotFound_Fails()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);

            var (exitCode, stdout, stderr) = CliTestRunner.Run("remove reference -p App.cocproj ../Libs/MyLib.coa", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("was not found", stderr);
        }

        [Fact]
        public void AddReference_AbsolutePath_StoredRelative()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            WriteProject(dir);
            var libPath = Path.Combine(dir, "..", "Libs", "MyLib.coa");

            var (exitCode, stdout, stderr) = CliTestRunner.Run($"add reference -p App.cocproj \"{libPath}\"", dir);

            Assert.True(exitCode == 0, stderr);
            var text = File.ReadAllText(Path.Combine(dir, "App.cocproj"));
            Assert.Contains("MyLib.coa", text);
            Assert.DoesNotContain(dir, text.Replace('/', '\\'));
        }

        [Fact]
        public void AddReference_NonProject_Fails()
        {
            var dir = CliTestRunner.NewTempDir("ref");
            File.WriteAllText(Path.Combine(dir, "Sol.cosln"), "name = Sol\n\n[projects]\n");
            var (exitCode, stdout, stderr) = CliTestRunner.Run("add reference -p Sol.cosln ../Libs/MyLib.coa", dir);

            Assert.Equal(1, exitCode);
            Assert.Contains("not a .cocproj", stderr);
        }
    }
}
