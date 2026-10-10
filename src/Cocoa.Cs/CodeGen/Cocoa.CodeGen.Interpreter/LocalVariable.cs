namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>一个局部变量快照。</summary>
    public sealed record LocalVariable(string Name, string? Type, object? Value);
}