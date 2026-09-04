using System.Collections.Generic;

namespace Cocoa.Compiler.Terminal;

internal interface ICompletionProvider
{
    IReadOnlyList<CompletionItem> GetCompletions(string text, int cursorPosition);
    string? GetSignatureHint(string text, int cursorPosition);
}
