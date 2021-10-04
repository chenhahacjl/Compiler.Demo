﻿namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 编译单元语法
    /// </summary>
    public sealed class CompilationUnitSyntax : SyntaxNode
    {
        public CompilationUnitSyntax(StatementSyntax statement, SyntaxToken endOfFileToken)
        {
            Statement = statement;
            EndOfFileToken = endOfFileToken;
        }

        public override SyntaxKind Kind => SyntaxKind.CompilationUnit;

        public StatementSyntax Statement { get; }
        public SyntaxToken EndOfFileToken { get; }
    }
}
