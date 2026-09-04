using System;
using System.Collections.Generic;
using System.IO;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.Cli.Repl;

/// <summary>REPL 会话状态：已提交的编译链、变量值与显示开关。</summary>
internal sealed class ReplSession
{
    private Compilation? _previous;
    private readonly Dictionary<VariableSymbol, object> _variables = new();
    private readonly List<string> _references = new();
    private bool _showTree;
    private bool _showProgram;

    public Compilation? Previous => _previous;
    public IReadOnlyList<string> References => _references;

    /// <summary>导入 .coa 库（#import）：验证存在性与可加载性后加入引用列表。</summary>
    public bool ImportLibrary(string path, OutputHistory output)
    {
        if (!File.Exists(path))
        {
            output.AppendLine($"File not found: {path}");
            return false;
        }

        if (!path.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
        {
            output.AppendLine($"Not a .coa library: {path}");
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        if (_references.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            output.AppendLine($"Already imported: {fullPath}");
            return false;
        }

        _references.Add(fullPath);

        try
        {
            // 预检：构造空脚本编译即触发 .coa 反序列化，尽早暴露损坏/不兼容库
            var probe = Compilation.CreateScript(null, _references.ToArray());
            _ = probe.GetDiagnostics();
        }
        catch (Exception ex)
        {
            _references.RemoveAt(_references.Count - 1);
            output.AppendLine($"Import failed: {ex.Message}");
            return false;
        }

        output.AppendLine($"Imported library: {fullPath}");
        return true;
    }

    /// <summary>求值一段提交文本；返回 true 表示成功（调用方据此持久化）。</summary>
    public bool Evaluate(string text, OutputHistory output)
    {
        var syntaxTree = SyntaxTree.Parse(text);
        var compilation = Compilation.CreateScript(
            _previous,
            _references.Count > 0 ? _references.ToArray() : null,
            syntaxTree);

        if (_showTree)
            output.AppendLine(WriteToString(writer => syntaxTree.Root.WriteTo(writer)));

        if (_showProgram)
            output.AppendLine(WriteToString(writer => compilation.EmitTree(writer)));

        var result = compilation.Evaluate(_variables);
        var diagnostics = result.Diagnostics;

        if (diagnostics.HasErrors())
        {
            foreach (var diag in diagnostics)
            {
                if (diag.IsError)
                    output.AppendLine($"error: {diag}", ConsoleColor.Red);
            }
            return false;
        }

        foreach (var diag in diagnostics)
            output.AppendLine($"warning: {diag}", ConsoleColor.Yellow);

        if (result.Value != null)
            output.AppendLine(result.Value.ToString() ?? "");

        _previous = compilation;
        return true;
    }

    public bool ToggleTree()
    {
        _showTree = !_showTree;
        return _showTree;
    }

    public bool ToggleProgram()
    {
        _showProgram = !_showProgram;
        return _showProgram;
    }

    public void Reset()
    {
        _previous = null;
        _variables.Clear();
    }

    /// <summary>跨提交链的用户符号（函数/变量）。内置函数（syscall 级原语）不可裸调，不列出。</summary>
    public static IEnumerable<Symbol> EnumerateUserSymbols(Compilation compilation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var submission = compilation;
        while (submission != null)
        {
            foreach (var function in submission.Functions)
                if (seen.Add(function.Name))
                    yield return function;

            foreach (var variable in submission.Variables)
                if (seen.Add(variable.Name))
                    yield return variable;

            submission = submission.Previous;
        }
    }

    public void ListSymbols(OutputHistory output)
    {
        var compilation = _previous ?? Compilation.CreateScript(null);
        foreach (var symbol in EnumerateUserSymbols(compilation))
            output.AppendLine($"  {symbol}");
    }

    public void DumpSymbol(string name, OutputHistory output)
    {
        var compilation = _previous ?? Compilation.CreateScript(null);
        var found = false;
        foreach (var symbol in EnumerateUserSymbols(compilation))
        {
            if (symbol.Name == name)
            {
                output.AppendLine($"  {symbol}");
                found = true;
            }
        }
        if (!found)
            output.AppendLine($"Symbol '{name}' not found.");
    }

    private static string WriteToString(Action<TextWriter> write)
    {
        var writer = new StringWriter();
        write(writer);
        return writer.ToString();
    }
}
