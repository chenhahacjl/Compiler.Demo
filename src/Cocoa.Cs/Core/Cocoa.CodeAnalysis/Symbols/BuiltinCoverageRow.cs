namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed record BuiltinCoverageRow(BuiltinKind Kind, BuiltinBackend Backends, string? GapReason);
}