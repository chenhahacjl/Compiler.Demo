using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法节点
    /// </summary>
    public abstract class SyntaxNode
    {
        public abstract SyntaxKind Kind { get; }

        public virtual TextSpan Span
        {
            get
            {
                var first = GetChildren().First().Span;
                var last = GetChildren().Last().Span;

                return TextSpan.FromBounds(first.Start, last.End);
            }
        }

        public IEnumerable<SyntaxNode> GetChildren()
        {
            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var property in properties)
            {
                if (typeof(SyntaxNode).IsAssignableFrom(property.PropertyType))
                {
                    var child = (SyntaxNode)property.GetValue(this);
                    if (child != null)
                    {
                        yield return child;
                    }
                }
                else if (typeof(IEnumerable<SyntaxNode>).IsAssignableFrom(property.PropertyType))
                {
                    var children = (IEnumerable<SyntaxNode>)property.GetValue(this);

                    foreach (var child in children)
                    {
                        if (child != null)
                        {
                            yield return child;
                        }
                    }
                }
            }
        }

        public void WriteTo(TextWriter writer)
        {
            PrettyPrint(writer, this);
        }

        private static void PrettyPrint(TextWriter write, SyntaxNode node, string indent = "", bool isLast = true)
        {
            var isToConsole = write == Console.Out;
            var marker = isLast ? "└──" : "├──";

            if (isToConsole) { Console.ForegroundColor = ConsoleColor.DarkGray; }
            write.Write($"{indent}{marker}");

            if (isToConsole) { Console.ForegroundColor = node is SyntaxToken ? ConsoleColor.Blue : ConsoleColor.Cyan; }
            write.Write($"{node.Kind}");

            if (node is SyntaxToken syntaxToken && syntaxToken.Value != null)
            {
                write.Write($" {syntaxToken.Value}");
            }

            if (isToConsole) { Console.ResetColor(); }

            write.WriteLine();

            indent += isLast ? "　   " : "│   ";

            var lastChild = node.GetChildren().LastOrDefault();

            foreach (var child in node.GetChildren())
            {
                PrettyPrint(write, child, indent, child == lastChild);
            }
        }

        public override string ToString()
        {
            using (var writer = new StringWriter())
            {
                WriteTo(writer);

                return writer.ToString();
            }
        }
    }
}
