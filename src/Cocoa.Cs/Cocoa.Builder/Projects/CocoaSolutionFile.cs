using System;
using System.Collections.Immutable;
using System.IO;

namespace Cocoa.Projects
{
    public sealed class CocoaSolutionFile
    {
        public CocoaSolutionFile(string filePath, string? name, ImmutableArray<string> projectPaths)
        {
            FilePath = Path.GetFullPath(filePath);
            Directory = Path.GetDirectoryName(FilePath) ?? ".";
            Name = name;
            ProjectPaths = projectPaths;
        }

        public string FilePath { get; }
        public string Directory { get; }
        public string? Name { get; }
        public ImmutableArray<string> ProjectPaths { get; }

        public static CocoaSolutionFile Load(string path)
        {
            var text = File.ReadAllText(path);
            return ProjectFileParser.ParseSolution(text, path);
        }
    }
}
