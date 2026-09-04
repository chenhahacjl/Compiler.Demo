using System;
using System.IO;
using System.Linq;

namespace Cocoa.Compiler.Terminal;

/// <summary>元命令（# 开头）的注册表与执行器；#exit/#cls 由引擎直接处理（涉及运行循环与整屏 UI）。</summary>
internal sealed class MetaCommandExecutor
{
    public static readonly string[] Names =
    {
        "cls", "dump", "exit", "help", "import", "keys", "load", "ls", "program", "reset", "tree",
    };

    private readonly ReplSession _session;
    private readonly OutputHistory _output;
    private readonly SubmissionStore _submissions;

    public bool ExitRequested { get; private set; }

    public MetaCommandExecutor(ReplSession session, OutputHistory output, SubmissionStore submissions)
    {
        _session = session;
        _output = output;
        _submissions = submissions;
    }

    /// <summary>是否为完整拼写的已知命令（含带参数形式）。</summary>
    public static bool IsKnown(string trimmed) =>
        trimmed == "#exit" || trimmed == "#cls" || trimmed == "#keys" || trimmed == "#help" ||
        trimmed == "#tree" || trimmed == "#program" || trimmed == "#reset" || trimmed == "#ls" ||
        trimmed.StartsWith("#load ") || trimmed.StartsWith("#dump ") ||
        trimmed.StartsWith("#import ");

    /// <summary>按前缀模糊匹配命令名（"ex" → "exit"）。</summary>
    public static string? Match(string query) =>
        Names.FirstOrDefault(c => c.StartsWith(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>执行元命令；返回 false 表示不是可执行的元命令。</summary>
    public bool TryHandle(string trimmed)
    {
        switch (trimmed)
        {
            case "#keys":
                PrintKeys();
                return true;
            case "#help":
                PrintHelp();
                return true;
            case "#tree":
                _output.AppendLine(_session.ToggleTree() ? "Syntax tree display: ON" : "Syntax tree display: OFF");
                return true;
            case "#program":
                _output.AppendLine(_session.ToggleProgram() ? "IL display: ON" : "IL display: OFF");
                return true;
            case "#reset":
                _session.Reset();
                _output.AppendLine("REPL state reset.");
                return true;
            case "#ls":
                _session.ListSymbols(_output);
                return true;
            case "#load":
                _output.AppendLine("Usage: #load <path>");
                return true;
            case "#import":
                _output.AppendLine("Usage: #import <path.coa>");
                return true;
            case "#dump":
                _output.AppendLine("Usage: #dump <name>");
                return true;
        }

        if (trimmed.StartsWith("#import "))
        {
            _session.ImportLibrary(trimmed.Substring(8).Trim().Trim('"'), _output);
            return true;
        }

        if (trimmed.StartsWith("#load "))
        {
            ExecuteLoad(trimmed.Substring(6).Trim().Trim('"'));
            return true;
        }

        if (trimmed.StartsWith("#dump "))
        {
            _session.DumpSymbol(trimmed.Substring(6).Trim(), _output);
            return true;
        }

        return false;
    }

    private void ExecuteLoad(string path)
    {
        if (!File.Exists(path))
        {
            _output.AppendLine($"File not found: {path}");
            return;
        }

        try
        {
            var fileText = File.ReadAllText(path);
            if (_session.Evaluate(fileText, _output))
                _submissions.Save(fileText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.AppendLine($"Could not read file: {path}");
        }
    }

    private void PrintKeys()
    {
        _output.AppendLine("Enter              Submit (at end of complete code)");
        _output.AppendLine("Ctrl+Enter         Submit (anywhere)");
        _output.AppendLine("Ctrl+Left / Right  Move by word");
        _output.AppendLine("Ctrl+Backspace     Delete previous word");
        _output.AppendLine("Tab                Accept completion");
        _output.AppendLine("Escape             Clear input / close popup");
        _output.AppendLine("PageUp / PageDown  History navigation");
        _output.AppendLine("");
    }

    private void PrintHelp()
    {
        _output.AppendLine("Meta commands:");
        _output.AppendLine("  #exit      Exit the REPL");
        _output.AppendLine("  #cls       Clear the screen");
        _output.AppendLine("  #keys      Show keyboard shortcuts");
        _output.AppendLine("  #help      Show this help");
        _output.AppendLine("  #tree      Toggle syntax tree display");
        _output.AppendLine("  #program   Toggle IL display");
        _output.AppendLine("  #reset     Reset the REPL state");
        _output.AppendLine("  #load <p>  Load a file");
        _output.AppendLine("  #import <p>  Import a .coa library");
        _output.AppendLine("  #ls        List all symbols");
        _output.AppendLine("  #dump <n>  Show symbol details");
        _output.AppendLine("");
    }
}
