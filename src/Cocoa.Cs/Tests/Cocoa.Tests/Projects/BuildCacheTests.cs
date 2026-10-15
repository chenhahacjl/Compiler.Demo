using System;
using System.Collections.Immutable;
using System.IO;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Projects
{
    public class BuildCacheTests : IDisposable
    {
        private readonly string _directory;

        public BuildCacheTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa-cache-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public void GetCachePath_SingleProject_AtAnchor()
        {
            var cacheRoot = BuildCache.GetDefaultCacheRoot(_directory);
            var path = BuildCache.GetCachePath(cacheRoot, _directory, "App");

            Assert.Equal(Path.Combine(_directory, ".cocoa", "App.cache"), path);
        }

        [Fact]
        public void GetCachePath_SolutionNestedProject_UsesRelativeDirectory()
        {
            var cacheRoot = BuildCache.GetDefaultCacheRoot(_directory);
            var path = BuildCache.GetCachePath(cacheRoot, Path.Combine(_directory, "App"), "App");

            Assert.Equal(Path.Combine(_directory, ".cocoa", "App", "App.cache"), path);
        }

        [Fact]
        public void GetCachePath_SameName_DifferentDirectories_NoCollision()
        {
            var cacheRoot = BuildCache.GetDefaultCacheRoot(_directory);
            var a = BuildCache.GetCachePath(cacheRoot, Path.Combine(_directory, "A"), "Core");
            var b = BuildCache.GetCachePath(cacheRoot, Path.Combine(_directory, "B"), "Core");

            Assert.NotEqual(a, b);
            Assert.Equal(Path.Combine(_directory, ".cocoa", "A", "Core.cache"), a);
            Assert.Equal(Path.Combine(_directory, ".cocoa", "B", "Core.cache"), b);
        }

        [Fact]
        public void GetCachePath_ProjectOutsideRoot_FallsBackToFolderName()
        {
            var cacheRoot = BuildCache.GetDefaultCacheRoot(_directory);
            var sibling = _directory + "-sibling";
            var path = BuildCache.GetCachePath(cacheRoot, sibling, "App");

            Assert.Equal(Path.Combine(cacheRoot, Path.GetFileName(sibling), "App.cache"), path);
            Assert.StartsWith(cacheRoot + Path.DirectorySeparatorChar, path);
        }

        [Fact]
        public void IsUpToDate_False_WhenCacheMissing()
        {
            var cachePath = BuildCache.GetCachePath(BuildCache.GetDefaultCacheRoot(_directory), _directory, "App");
            Assert.False(BuildCache.IsUpToDate(cachePath, "abc"));
        }

        [Fact]
        public void Write_Then_IsUpToDate_True_ForSameFingerprint()
        {
            var cachePath = BuildCache.GetCachePath(BuildCache.GetDefaultCacheRoot(_directory), _directory, "App");
            BuildCache.Write(cachePath, "abc");

            Assert.True(BuildCache.IsUpToDate(cachePath, "abc"));
            Assert.False(BuildCache.IsUpToDate(cachePath, "xyz"));
        }

        [Fact]
        public void Fingerprint_SourceContentChange_Invalidates()
        {
            var source = Path.Combine(_directory, "a.co");
            File.WriteAllText(source, "function Main()");

            var before = BuildCache.ComputeFingerprint(
                ImmutableArray.Create(source), ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

            File.WriteAllText(source, "function main2()");

            var after = BuildCache.ComputeFingerprint(
                ImmutableArray.Create(source), ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Fingerprint_OptionChange_Invalidates()
        {
            var before = BuildCache.ComputeFingerprint(
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create("debug=false"));

            var after = BuildCache.ComputeFingerprint(
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create("debug=true"));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Fingerprint_MissingSource_HashesEmpty()
        {
            var fingerprint = BuildCache.ComputeFingerprint(
                ImmutableArray.Create(Path.Combine(_directory, "missing.co")),
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty);

            Assert.False(string.IsNullOrEmpty(fingerprint));
        }

        [Theory]
        [InlineData("def", true)]
        [InlineData("abcd", false)]
        public void Fingerprint_ReferenceChange_Invalidates(string content, bool changeTimestamp)
        {
            var reference = Path.Combine(_directory, "ref.dll");
            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(reference, "abc");
            File.SetLastWriteTimeUtc(reference, timestamp);
            var originalTimestamp = File.GetLastWriteTimeUtc(reference);
            var originalLength = new FileInfo(reference).Length;

            var before = BuildCache.ComputeFingerprint(
                ImmutableArray<string>.Empty, ImmutableArray.Create(reference), ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

            Assert.Equal(before, BuildCache.ComputeFingerprint(
                ImmutableArray<string>.Empty, ImmutableArray.Create(reference), ImmutableArray<string>.Empty, ImmutableArray<string>.Empty));

            File.WriteAllText(reference, content);
            File.SetLastWriteTimeUtc(reference, changeTimestamp ? timestamp.AddSeconds(2) : timestamp);

            if (changeTimestamp)
            {
                Assert.Equal(originalLength, new FileInfo(reference).Length);
                Assert.NotEqual(originalTimestamp, File.GetLastWriteTimeUtc(reference));
            }
            else
            {
                Assert.NotEqual(originalLength, new FileInfo(reference).Length);
                Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(reference));
            }

            var after = BuildCache.ComputeFingerprint(
                ImmutableArray<string>.Empty, ImmutableArray.Create(reference), ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

            Assert.NotEqual(before, after);
        }
    }
}