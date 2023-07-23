using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.IO;

namespace Compiler.Demo
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: cc <source-paths>");
                return;
            }

            if (args.Length > 1)
            {
                Console.WriteLine("error: only one path supported right now!");
                return;
            }

            var path = args.Single();

            var text = File.ReadAllText(path);
            var syntaxTree = SyntaxTree.Parse(text);

            var compilation = new Compilation(syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            if (result.Diagnostics.Any())
            {
                Console.Error.WriteDiagnostics(result.Diagnostics, syntaxTree);
            }
            else
            {
                if (result.Value != null)
                    Console.WriteLine(result.Value);
            }
        }
    }
}