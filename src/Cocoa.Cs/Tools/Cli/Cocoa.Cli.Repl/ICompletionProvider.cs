using System.Collections.Generic;

namespace Cocoa.Cli.Repl;

internal interface ICompletionProvider
{
    IReadOnlyList<CompletionItem> GetCompletions(string text, int cursorPosition);
    string? GetSignatureHint(string text, int cursorPosition);
}
