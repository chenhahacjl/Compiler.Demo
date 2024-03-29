﻿using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Cocoa.IO;

namespace Compiler.Demo
{
    internal sealed class CocoaRepl : Repl
    {
        private Compilation m_previous;
        private bool m_showTree;
        private bool m_showProgram;
        private readonly Dictionary<VariableSymbol, object> m_variables = new Dictionary<VariableSymbol, object>();

        protected override void RenderLine(string line)
        {
            var tokens = SyntaxTree.ParseTokens(line);
            foreach (var token in tokens)
            {
                var isKeyword = token.Kind.ToString().EndsWith("Keyword");
                var isIdentifier = token.Kind == SyntaxKind.IdentifierToken;
                var isNumber = token.Kind == SyntaxKind.NumberToken;
                var isString = token.Kind == SyntaxKind.StringToken;

                if (isKeyword)
                    Console.ForegroundColor = ConsoleColor.Blue;
                else if (isIdentifier)
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                else if (isNumber)
                    Console.ForegroundColor = ConsoleColor.Cyan;
                else if (isString)
                    Console.ForegroundColor = ConsoleColor.Magenta;
                else
                    Console.ForegroundColor = ConsoleColor.DarkGray;

                Console.Write(token.Text);

                Console.ResetColor();
            }
        }

        protected override void EvaluateMetaCommand(string input)
        {
            switch (input)
            {
                case "#showTree":
                    m_showTree = !m_showTree;
                    Console.WriteLine(m_showTree ? "Showing parse trees." : "Not showing parse trees.");
                    break;
                case "#showProgram":
                    m_showProgram = !m_showProgram;
                    Console.WriteLine(m_showProgram ? "Showing bound trees." : "Not showing bound trees.");
                    break;
                case "#cls":
                    Console.Clear();
                    break;
                case "#reset":
                    m_previous = null;
                    m_variables.Clear();
                    break;
                default:
                    base.EvaluateMetaCommand(input);
                    break;
            }
        }

        protected override bool IsCompleteSubmission(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            var lastTwoLineAreBlank = text.Split(Environment.NewLine)
                                          .Reverse()
                                          .TakeWhile(s => string.IsNullOrEmpty(s))
                                          .Take(2)
                                          .Count() == 2;

            if (lastTwoLineAreBlank)
                return true;

            var syntaxTree = SyntaxTree.Parse(text);

            // Use Member because we need to exclude the EndOfFileToken.
            if (syntaxTree.Root.Members.Last().GetLastToken().IsMissing)
                return false;

            return true;
        }

        protected override void EvaluateSubmission(string text)
        {
            var syntaxTree = SyntaxTree.Parse(text);

            var compilation = m_previous == null
                ? new Compilation(syntaxTree)
                : m_previous.ContinueWith(syntaxTree);

            if (m_showTree)
                syntaxTree.Root.WriteTo(Console.Out);

            if (m_showProgram)
                compilation.EmitTree(Console.Out);

            var result = compilation.Evaluate(m_variables);

            if (!result.Diagnostics.Any())
            {
                if (result.Value != null)
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(result.Value);
                    Console.ResetColor();
                }

                m_previous = compilation;
            }
            else
            {
                Console.Out.WriteDiagnostics(result.Diagnostics);
            }
        }
    }
}
