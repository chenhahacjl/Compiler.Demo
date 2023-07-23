using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.IO;
using System.Net.Http.Headers;
using System.IO;

namespace Compiler.Demo
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            args = new string[] { "E:\\C# Code\\Compiler.Demo\\src\\Samples\\HelloWorld" };

            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: cc <source-paths>");
                return;
            }

            var paths = GetFilePaths(args);
            var syntaxTrees = new List<SyntaxTree>(paths.Count());
            var hasErrors = false;

            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"error: file '{path}' doesn't exist!");
                    hasErrors = true;
                    continue;
                }

                var syntaxTree = SyntaxTree.Load(path);
                syntaxTrees.Add(syntaxTree);
            }

            if (hasErrors)
                return;

            var compilation = new Compilation(syntaxTrees.ToArray());
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            if (!result.Diagnostics.Any())
            {
                if (result.Value != null)
                    Console.WriteLine(result.Value);
            }
            else
            {
                Console.Error.WriteDiagnostics(result.Diagnostics);
            }
        }

        private static IEnumerable<string> GetFilePaths(IEnumerable<string> paths)
        {
            var result = new SortedSet<string>();

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    result.UnionWith(Directory.EnumerateFiles(path, "*.co", SearchOption.AllDirectories));
                }
                else
                {
                    result.Add(path);
                }
            }

            return result;
        }
    }
}