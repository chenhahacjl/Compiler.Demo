using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;

namespace Compiler.Demo
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            bool showTree = false;

            while (true)
            {
                Console.Write("> ");

                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                if (line == "#showTree")
                {
                    showTree = !showTree;
                    Console.WriteLine(showTree ? "Showing parse trees." : "Not showing parse trees.");
                    continue;
                }
                else if (line == "#cls")
                {
                    Console.Clear();
                    continue;
                }

                var syntaxTree = SyntaxTree.Parse(line);
                var compilation = new Compilation(syntaxTree);
                var result = compilation.Evaluate();

                IReadOnlyList<Diagnostic> diagnostics = result.Diagnostics;

                if (showTree)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    PrettyPrint(syntaxTree.Root);
                    Console.ResetColor();
                }

                if (diagnostics.Any())
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;

                    foreach (var diagnostic in diagnostics)
                    {
                        Console.WriteLine(diagnostic);
                    }

                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine(result.Value);
                }
            }
        }

        static void PrettyPrint(SyntaxNode node, string indent = "", bool isLast = true)
        {
            var marker = isLast ? "└---" : "├---";

            Console.Write($"{indent}{marker}{node.Kind}");

            if (node is SyntaxToken syntaxToken && syntaxToken.Value != null)
            {
                Console.Write($" {syntaxToken.Value}");
            }

            Console.WriteLine();

            indent += isLast ? "　   " : "│   ";

            var lastChild = node.GetChildren().LastOrDefault();

            foreach (var child in node.GetChildren())
            {
                PrettyPrint(child, indent, child == lastChild);
            }
        }
    }
}
