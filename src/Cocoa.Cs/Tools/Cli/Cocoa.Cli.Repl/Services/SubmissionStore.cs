using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cocoa.Cli.Repl;

/// <summary>提交文本的持久化（%LOCALAPPDATA%\Cocoa\Submissions）。</summary>
internal sealed class SubmissionStore
{
    private readonly string _directory;

    public SubmissionStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.Combine(localAppData, "Cocoa", "Submissions");
    }

    public void Save(string text)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var count = Directory.GetFiles(_directory).Length;
            File.WriteAllText(Path.Combine(_directory, $"submission{count:0000}"), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 历史持久化是尽力而为，不因 IO 失败打断 REPL。
        }
    }

    public IReadOnlyList<string> LoadAll()
    {
        try
        {
            if (!Directory.Exists(_directory))
                return Array.Empty<string>();

            return Directory.GetFiles(_directory)
                .OrderBy(f => f)
                .Select(File.ReadAllText)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
