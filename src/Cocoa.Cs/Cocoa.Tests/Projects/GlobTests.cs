using System;
using System.IO;
using System.Linq;
using Cocoa.ProjectSystem;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class GlobTests : IDisposable
    {
        private readonly string _baseDirectory;

        public GlobTests()
        {
            _baseDirectory = Path.Combine(Path.GetTempPath(), "cocoa-glob-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_baseDirectory, "sub", "deep"));
            Directory.CreateDirectory(Path.Combine(_baseDirectory, "sub.x"));
            File.WriteAllText(Path.Combine(_baseDirectory, "a.co"), "");
            File.WriteAllText(Path.Combine(_baseDirectory, "b.co"), "");
            File.WriteAllText(Path.Combine(_baseDirectory, "main.co"), "");
            File.WriteAllText(Path.Combine(_baseDirectory, "readme.txt"), "");
            File.WriteAllText(Path.Combine(_baseDirectory, "sub", "c.co"), "");
            File.WriteAllText(Path.Combine(_baseDirectory, "sub", "deep", "d.co"), "");
            File.WriteAllText(Path.Combine(_baseDirectory, "sub.x", "f.co"), "");
        }

        public void Dispose()
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }

        private static string[] Names(GlobExpansion expansion)
        {
            return expansion.Files.Select(f => Path.GetFileName(f)).ToArray();
        }

        [Fact]
        public void Star_DoesNotCrossDirectories()
        {
            var expansion = Glob.Expand(new[] { "*.co" }, _baseDirectory);

            Assert.Equal(new[] { "a.co", "b.co", "main.co" }, Names(expansion).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void SingleLevel_Pattern_DoesNotMatch_NestedFiles()
        {
            var expansion = Glob.Expand(new[] { "sub/*.co" }, _baseDirectory);

            Assert.Equal(new[] { "c.co" }, Names(expansion));
        }

        [Fact]
        public void DoubleStar_Matches_Recursively()
        {
            var expansion = Glob.Expand(new[] { "sub/**/*.co" }, _baseDirectory);

            Assert.Equal(new[] { "c.co", "d.co" }, Names(expansion).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void DoubleStar_AtRoot_Matches_All()
        {
            var expansion = Glob.Expand(new[] { "**/*.co" }, _baseDirectory);

            Assert.Equal(6, expansion.Files.Length);
        }

        [Fact]
        public void DoubleStar_MatchesZeroDirectories()
        {
            var expansion = Glob.Expand(new[] { "**/c.co" }, _baseDirectory);

            Assert.Equal(new[] { "c.co" }, Names(expansion));
        }

        [Fact]
        public void Literal_SingleFile_Match()
        {
            var expansion = Glob.Expand(new[] { "main.co" }, _baseDirectory);

            Assert.Equal(new[] { "main.co" }, Names(expansion));
        }

        [Fact]
        public void DirectoryNameWithDot_MatchesPattern()
        {
            var expansion = Glob.Expand(new[] { "sub.x/*.co" }, _baseDirectory);

            Assert.Equal(new[] { "f.co" }, Names(expansion));
        }

        [Fact]
        public void QuestionMark_MatchesSingleCharacter()
        {
            var expansion = Glob.Expand(new[] { "?.co" }, _baseDirectory);

            Assert.Equal(new[] { "a.co", "b.co" }, Names(expansion).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void UnmatchedPattern_IsReported()
        {
            var expansion = Glob.Expand(new[] { "*.co", "missing.co" }, _baseDirectory);

            Assert.Equal(new[] { "missing.co" }, expansion.UnmatchedPatterns.ToArray());
        }

        [Fact]
        public void NonExistentBaseDirectory_AllPatternsUnmatched()
        {
            var expansion = Glob.Expand(new[] { "*.co" }, Path.Combine(_baseDirectory, "nope"));

            Assert.Empty(expansion.Files);
            Assert.Equal(new[] { "*.co" }, expansion.UnmatchedPatterns.ToArray());
        }

        [Fact]
        public void DuplicateMatches_AreDeDuplicated()
        {
            var expansion = Glob.Expand(new[] { "**/*.co", "*.co" }, _baseDirectory);

            Assert.Equal(expansion.Files.Length, expansion.Files.Distinct().Count());
        }
    }
}