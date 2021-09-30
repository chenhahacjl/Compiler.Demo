using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis
{
    internal sealed class DiagnosticBag : IEnumerable<Diagnostic>
    {
        private readonly List<Diagnostic> m_diagnostics = new List<Diagnostic>();

        public IEnumerator<Diagnostic> GetEnumerator() => m_diagnostics.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void AddRange(DiagnosticBag diagnostics)
        {
            m_diagnostics.AddRange(diagnostics.m_diagnostics);
        }

        private void Report(TextSpan span, string message)
        {
            var diagnostic = new Diagnostic(span, message);
            m_diagnostics.Add(diagnostic);
        }

        public void ReportInvalidNumber(TextSpan span, string text, Type type)
        {
            var message = $"The number {text} isn't valid {type}.";
            Report(span, message);
        }

        public void ReportBadCharacter(int position, char current)
        {
            TextSpan span = new TextSpan(position, 1);
            var message = $"Bad character input: '{current}'.";
            Report(span, message);
        }

        public void ReportUnexpcetedToken(TextSpan span, SyntaxKind actualKind, SyntaxKind expectedKind)
        {
            var message = $"Unexpected token <{actualKind}>, expected <{expectedKind}>.";
            Report(span, message);
        }

        public void ReportUndefinedUnaryOperator(TextSpan span, string operatorText, Type operandType)
        {
            var message = $"Unary operator '{operatorText}' is not defined for type {operandType}.";
            Report(span, message);
        }

        internal void ReportUndefinedBinaryOperator(TextSpan span, string operatorText, Type leftType, Type rightType)
        {
            var message = $"Binary operator '{operatorText}' is not defined for type {leftType} and {rightType}.";
            Report(span, message);
        }
    }
}
