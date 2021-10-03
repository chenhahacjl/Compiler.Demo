﻿using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法符号
    /// </summary>
    public sealed class SyntaxToken : SyntaxNode
    {
        public SyntaxToken(SyntaxKind kind, int position, string text, object value)
        {
            Kind = kind;
            Position = position;
            Text = text;
            Value = value;
        }

        public override SyntaxKind Kind { get; }

        public int Position { get; }
        public string Text { get; }
        public object Value { get; }
        public TextSpan Span => new TextSpan(Position, Text.Length);
    }
}
