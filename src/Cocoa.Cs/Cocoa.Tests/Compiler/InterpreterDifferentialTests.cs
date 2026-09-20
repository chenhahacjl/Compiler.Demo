using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量五 M5-a1：自举 Interpreter 与 C# Evaluator 活差分。
    /// 语料 = 13 条 bound corpus（GoldenGenerator.BinderBoundCorpus）。
    /// 自举侧：编译器 .co + CodeGen .co + 驱动 Main → Evaluator 运行 → 解析 @@INT:i 段；
    /// C# 基准：Compilation.Create(...).Evaluate(...).Value.ToString()。
    /// 断言返回值 + Console 输出逐字节一致（黄金串捕获的信任来源）。
    /// </summary>
    public class InterpreterDifferentialTests
    {
        [Fact]
        public void SelfInterpreter_Matches_CShaEvaluator_ForBoundCorpus()
        {
            var corpora = GoldenGenerator.BinderBoundCorpus;
            var (selfResults, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var failures = new List<string>();
            for (var i = 0; i < corpora.Length; i++)
            {
                var csharp = CSharpReference(corpora[i]);
                var self = selfResults["INT:" + i];
                if (csharp != self)
                {
                    failures.Add($"bound-{i}: C#=[{csharp}] self=[{self}]\nSOURCE: {corpora[i].Replace("\n", "\\n")}\n-----");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string CSharpReference(string source)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            if (result.Diagnostics.HasErrors())
            {
                return "<csharp-errors>";
            }

            return result.Value?.ToString() ?? "<null>";
        }

        private static (Dictionary<string, string> Results, List<string> CompileErrors) RunSelfDriver()
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource()));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                if (result.Diagnostics.HasErrors())
                {
                    return (new Dictionary<string, string>(), result.Diagnostics.Select(d => d.Message).ToList());
                }

                var output = writer.ToString().Replace("\r\n", "\n");
                return (ParseMarkers(output), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static Dictionary<string, string> ParseMarkers(string output)
        {
            var results = new Dictionary<string, string>();
            var current = (string?)null;
            var collected = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (current != null)
                    {
                        if (collected.Count > 0 && collected[collected.Count - 1] == "")
                        {
                            collected.RemoveAt(collected.Count - 1);
                        }

                        results[current] = string.Join("\n", collected);
                    }

                    current = line.Substring(2);
                    collected.Clear();
                }
                else
                {
                    collected.Add(line);
                }
            }

            if (current != null)
            {
                if (collected.Count > 0 && collected[collected.Count - 1] == "")
                {
                    collected.RemoveAt(collected.Count - 1);
                }

                results[current] = string.Join("\n", collected);
            }

            return results;
        }

        private static string BuildDriverSource()
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing Cocoa.CodeGen\nusing System\n\n");
            sb.Append("function Main(): i32\n{\n");
            for (var i = 0; i < GoldenGenerator.BinderBoundCorpus.Length; i++)
            {
                sb.Append("    PrintInterp(\"@@INT:").Append(i).Append("\", ").Append(CoStr(GoldenGenerator.BinderBoundCorpus[i])).Append(")\n");
            }

            sb.Append("    return 0\n}\n\n");
            sb.Append(
                "function PrintInterp(marker: string, src: string): void\n" +
                "{\n" +
                "    System.Console.WriteLine(marker)\n" +
                "    let b = Cocoa.CodeAnalysis.Binding.Binder.Create(src)\n" +
                "    b.BindCompilationUnit()\n" +
                "    let interp = Cocoa.CodeGen.InterpreterBackend.FromBinder(b)\n" +
                "    let value = interp.EvaluateMain()\n" +
                "    let outText = interp.Output()\n" +
                "    System.Console.Write(value.Format())\n" +
                "    if outText.Length > 0\n" +
                "    {\n" +
                "        System.Console.Write(\"\\n\" + outText)\n" +
                "    }\n" +
                "    System.Console.Write(\"\\n\")\n" +
                "}\n");
            return sb.ToString();
        }

        private static string CoStr(string s)
            => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }
    }
}