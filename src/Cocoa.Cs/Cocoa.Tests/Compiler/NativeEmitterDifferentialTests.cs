using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量五 M5-a4：自举 NativeEmitter（绑定树 → LIR 文本）与 C# 差分。
    /// 语料 = BoundCorpus（GoldenGenerator.BinderBoundCorpus）。
    /// C# 基准 = COCOA_DUMP_IR 落盘 LirPrinter.Format(program)，按源用户函数名提取段；
    /// 自举侧 = 编译器 .co + NativeEmitter.co + 驱动 Main → 打印 FUNCTION 段（@@LIR 标记）。
    /// 断言逐函数逐行一致（块结构/寄存器 id/标签 id/terminator）。
    /// </summary>
    public class NativeEmitterDifferentialTests
    {
        [Fact]
        public void SelfHosted_NativeEmitter_Matches_CSha_ForBoundCorpus()
        {
            var corpora = GoldenGenerator.BinderBoundCorpus;
            var (selfByFn, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var failures = new List<string>();
            for (var i = 0; i < corpora.Length; i++)
            {
                var source = corpora[i];
                var userFunctions = UserFunctionNames(source);
                foreach (var fn in userFunctions)
                {
                    var csharp = CShaLirSection(source, fn);
                    var self = selfByFn.TryGetValue(i + ":" + fn, out var s) ? s : "<missing>";
                    if (csharp != self)
                    {
                        failures.Add($"corpus-{i} [{fn}] mismatch\n---C#---\n{csharp}\n---SELF---\n{self}\nSOURCE: {source.Replace("\n", "\\n")}\n-----");
                    }
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string[] UserFunctionNames(string source)
        {
            return Regex.Matches(source, @"function\s+(\w+)")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
        }

        /// <summary>EmitNative + COCOA_DUMP_IR → LirPrinter.Format dump → 提取用户函数的 FUNCTION 段。</summary>
        private static string CShaLirSection(string source, string functionName)
        {
            const string target = "windows-x64";
            TargetPlatform.TryParse(target, out var platform);
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-nativelir-diff");
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "diff-" + Guid.NewGuid().ToString("N") + ".exe");
            var dumpFile = Path.ChangeExtension(exePath, ".ir.txt");

            var previous = Environment.GetEnvironmentVariable("COCOA_DUMP_IR");
            Environment.SetEnvironmentVariable("COCOA_DUMP_IR", "1");
            try
            {
                File.Delete(dumpFile);
                var diagnostics = compilation.EmitNative("test", exePath, platform);
                Assert.True(diagnostics.IsEmpty, functionName + ": " + string.Join("\n", diagnostics));
                Assert.True(File.Exists(dumpFile), "COCOA_DUMP_IR did not produce dump file.");
                var all = File.ReadAllText(dumpFile);
                return ExtractFunctionSection(all, functionName);
            }
            finally
            {
                if (previous == null)
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_IR", null);
                }
                else
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_IR", previous);
                }
            }
        }

        private static string ExtractFunctionSection(string dump, string functionName)
        {
            var lines = dump.Replace("\r\n", "\n").Split('\n');
            var start = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("FUNCTION " + functionName, StringComparison.Ordinal) &&
                    (lines[i].Length == "FUNCTION ".Length + functionName.Length || lines[i]["FUNCTION ".Length + functionName.Length] == ' '))
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                return "<no-csharp-section>";
            }

            var end = lines.Length;
            for (var i = start + 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("FUNCTION ", StringComparison.Ordinal))
                {
                    end = i;
                    break;
                }
            }

            // 压缩块间空行（LirPrinter 每个函数结尾一个空行；驱动不输出尾部空行）
            var section = lines.Skip(start).Take(end - start)
                .Where(l => l.Length > 0)
                .ToArray();
            return string.Join("\n", section);
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

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return (new Dictionary<string, string>(), result.Diagnostics.Select(d => d.Message).ToList());
                }

                var output = writer.ToString().Replace("\r\n", "\n");
                return (ParseLirMarkers(output), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static Dictionary<string, string> ParseLirMarkers(string output)
        {
            var result = new Dictionary<string, string>();
            var lines = output.Split('\n');
            string? current = null;
            var collected = new List<string>();
            var skipSymHex = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("@@SYM:", StringComparison.Ordinal))
                {
                    // 机器码段（LirToAssembler 输出）：标记行 + 下一行 hex 均不计入 LIR 差分
                    skipSymHex = true;
                    continue;
                }

                if (skipSymHex)
                {
                    skipSymHex = false;
                    continue;
                }

                if (line.StartsWith("@@LIR:", StringComparison.Ordinal))
                {
                    if (current != null)
                    {
                        result[current] = string.Join("\n", collected.Where(l => l.Length > 0));
                    }

                    current = line.Substring(6).Trim();
                    collected = new List<string>();
                }
                else
                {
                    var t = line.TrimEnd();
                    if (t.Length > 0)
                    {
                        collected.Add(t);
                    }
                }
            }

            if (current != null)
            {
                result[current] = string.Join("\n", collected.Where(l => l.Length > 0));
            }

            return result;
        }

        private static string BuildDriverSource()
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeAnalysis.Syntax\nusing Cocoa.CodeAnalysis.Binding\nusing Cocoa.CodeGen\nusing System\n\n");

            // KeyOf：FunctionSortKey（owner|ns|name|params）
            sb.Append("function KeyOf(b: Binder, i: i32): string\n{\n");
            sb.Append("    var s = \"||\"\n");
            sb.Append("    s = s + b.GetFunctionName(i)\n");
            sb.Append("    s = s + \"|\"\n");
            sb.Append("    let pc = b.GetFunctionParamCount(i)\n");
            sb.Append("    var k = 0\n");
            sb.Append("    while k < pc\n");
            sb.Append("    {\n");
            sb.Append("        s = s + b.GetFunctionParamName(i, k)\n");
            sb.Append("        if k < pc - 1\n");
            sb.Append("        {\n");
            sb.Append("            s = s + \",\"\n");
            sb.Append("        }\n");
            sb.Append("        k = k + 1\n");
            sb.Append("    }\n");
            sb.Append("    return s\n");
            sb.Append("}\n\n");

            sb.Append("function StrCmp(a: string, b: string): i32\n{\n");
            sb.Append("    var i = 0\n");
            sb.Append("    while i < a.Length && i < b.Length\n");
            sb.Append("    {\n");
            sb.Append("        if a[i] != b[i]\n");
            sb.Append("        {\n");
            sb.Append("            if i32(a[i]) > i32(b[i])\n");
            sb.Append("            {\n");
            sb.Append("                return 1\n");
            sb.Append("            }\n");
            sb.Append("            return -1\n");
            sb.Append("        }\n");
            sb.Append("        i = i + 1\n");
            sb.Append("    }\n");
            sb.Append("    if a.Length > b.Length\n");
            sb.Append("    {\n");
            sb.Append("        return 1\n");
            sb.Append("    }\n");
            sb.Append("    if a.Length < b.Length\n");
            sb.Append("    {\n");
            sb.Append("        return -1\n");
            sb.Append("    }\n");
            sb.Append("    return 0\n");
            sb.Append("}\n\n");

            sb.Append("function Main(): i32\n{\n");
            sb.Append("    var i = 0\n");
            sb.Append("    while i < 13\n");
            sb.Append("    {\n");
            sb.Append("        let src = CorpusSource(i)\n");
            sb.Append("        let b = Cocoa.CodeAnalysis.Binding.Binder.Create(src)\n");
            sb.Append("        b.BindCompilationUnit()\n");
            sb.Append("        let n = b.GetFunctionCount()\n");
            sb.Append("        var keys = new string[8]\n");
            sb.Append("        var order = new i32[8]\n");
            sb.Append("        var k = 0\n");
            sb.Append("        while k < n\n");
            sb.Append("        {\n");
            sb.Append("            keys[k] = KeyOf(b, k)\n");
            sb.Append("            order[k] = k\n");
            sb.Append("            k = k + 1\n");
            sb.Append("        }\n");
            sb.Append("        var s = 0\n");
            sb.Append("        while s < n\n");
            sb.Append("        {\n");
            sb.Append("            var best = s\n");
            sb.Append("            var t = s + 1\n");
            sb.Append("            while t < n\n");
            sb.Append("            {\n");
            sb.Append("                if StrCmp(keys[order[t]], keys[order[best]]) < 0\n");
            sb.Append("                {\n");
            sb.Append("                    best = t\n");
            sb.Append("                }\n");
            sb.Append("                t = t + 1\n");
            sb.Append("            }\n");
            sb.Append("            let tmp = order[s]\n");
            sb.Append("            order[s] = order[best]\n");
            sb.Append("            order[best] = tmp\n");
            sb.Append("            s = s + 1\n");
            sb.Append("        }\n");

            sb.Append("        let em = new Cocoa.CodeGen.NativeEmitter()\n");
            sb.Append("        var mn = new string[8]\n");
            sb.Append("        var m = 0\n");
            sb.Append("        while m < n\n");
            sb.Append("        {\n");
            sb.Append("            mn[m] = b.GetFunctionName(order[m])\n");
            sb.Append("            m = m + 1\n");
            sb.Append("        }\n");
            sb.Append("        em.SetMethods(mn, n)\n");

            sb.Append("        var f = 0\n");
            sb.Append("        while f < n\n");
            sb.Append("        {\n");
            sb.Append("            let idx = order[f]\n");
            sb.Append("            let name = b.GetFunctionName(idx)\n");
            sb.Append("            let pc = b.GetFunctionParamCount(idx)\n");
            sb.Append("            var pn = new string[16]\n");
            sb.Append("            var q = 0\n");
            sb.Append("            while q < pc\n");
            sb.Append("            {\n");
            sb.Append("                pn[q] = b.GetFunctionParamName(idx, q)\n");
            sb.Append("                q = q + 1\n");
            sb.Append("            }\n");
            sb.Append("            System.Console.WriteLine(\"@@LIR:\" + string(i) + \":\" + name)\n");
            sb.Append("            var hdr = \"FUNCTION \" + name\n");
            sb.Append("            if pc > 0\n");
            sb.Append("            {\n");
            sb.Append("                hdr = hdr + \" (\"\n");
            sb.Append("                var q2 = 0\n");
            sb.Append("                while q2 < pc\n");
            sb.Append("                {\n");
            sb.Append("                    hdr = hdr + pn[q2]\n");
            sb.Append("                    if q2 < pc - 1\n");
            sb.Append("                    {\n");
            sb.Append("                        hdr = hdr + \", \"\n");
            sb.Append("                    }\n");
            sb.Append("                    q2 = q2 + 1\n");
            sb.Append("                }\n");
            sb.Append("                hdr = hdr + \")\"\n");
            sb.Append("            }\n");
            sb.Append("            System.Console.WriteLine(hdr)\n");
            sb.Append("            em.EmitFunction(b.GetFunctionBody(idx), pn, pc, name == \"Main\", 4, name)\n");
            sb.Append("            var j = 0\n");
            sb.Append("            while j < em.Count()\n");
            sb.Append("            {\n");
            sb.Append("                System.Console.WriteLine(em.Line(j))\n");
            sb.Append("                j = j + 1\n");
            sb.Append("            }\n");
            sb.Append("            let lt = new Cocoa.CodeGen.LirToAssembler(em)\n");
            sb.Append("            lt.SetFunction(name, name == \"Main\", 4)\n");
            sb.Append("            lt.Emit()\n");
            sb.Append("            System.Console.WriteLine(\"@@SYM:\" + string(i) + \":\" + name)\n");
            sb.Append("            System.Console.WriteLine(lt.SymbolicHex())\n");
            sb.Append("            f = f + 1\n");
            sb.Append("        }\n");
            sb.Append("        if i == 0\n");
            sb.Append("        {\n");
            sb.Append("            var stubs = new string[5]\n");
            sb.Append("            stubs[0] = \"ExitProcess\"\n");
            sb.Append("            stubs[1] = \"TickCount\"\n");
            sb.Append("            stubs[2] = \"WriteStr\"\n");
            sb.Append("            stubs[3] = \"DivByZero\"\n");
            sb.Append("            stubs[4] = \"StackOverflow\"\n");
            sb.Append("            var si = 0\n");
            sb.Append("            while si < 5\n");
            sb.Append("            {\n");
            sb.Append("                let stubEmitter = new Cocoa.CodeGen.LirToAssembler(em)\n");
            sb.Append("                stubEmitter.SetRuntimeStub(stubs[si])\n");
            sb.Append("                stubEmitter.Emit()\n");
            sb.Append("                System.Console.WriteLine(\"@@SYM:0:\" + stubs[si])\n");
            sb.Append("                System.Console.WriteLine(stubEmitter.SymbolicHex())\n");
            sb.Append("                si = si + 1\n");
            sb.Append("            }\n");
            sb.Append("        }\n");
            sb.Append("        i = i + 1\n");
            sb.Append("    }\n");
            sb.Append("    return 0\n");
            sb.Append("}\n\n");

            // CorpusSource：与 GoldenGenerator.BinderBoundCorpus 同序
            sb.Append("function CorpusSource(i: i32): string\n{\n");
            var corpus = GoldenGenerator.BinderBoundCorpus;
            for (var i = 0; i < corpus.Length; i++)
            {
                var embedded = corpus[i].Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                sb.Append("    if i == ").Append(i).Append(" { return \"").Append(embedded).Append("\" }\n");
            }

            sb.Append("    return \"\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

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

        // ------------------------------------------------------------------
        // M5-a4 续作：LirToAssembler 布局无关机器码差分（COCOA_DUMP_SYM vs 自举符号化 hex）
        // ------------------------------------------------------------------

        [Fact]
        public void SelfHosted_LirToAssembler_Matches_CShaSymbolic_ForBoundCorpus()
        {
            var corpora = GoldenGenerator.BinderBoundCorpus;
            var (selfByFn, compileErrors) = RunSelfDriverSym();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var failures = new List<string>();
            for (var i = 0; i < corpora.Length; i++)
            {
                var source = corpora[i];
                var userFunctions = UserFunctionNames(source);
                var csharpSym = CShaSymbolicFunctions(source);
                foreach (var fn in userFunctions)
                {
                    if (!csharpSym.TryGetValue(fn, out var csharp))
                    {
                        failures.Add($"corpus-{i} [{fn}] no C# symbolic dump");
                        continue;
                    }

                    var self = selfByFn.TryGetValue(i + ":" + fn, out var s) ? s : "<missing>";
                    if (csharp != self)
                    {
                        failures.Add($"corpus-{i} [{fn}] mismatch\n---C#-sym---\n{csharp}\n---SELF-sym---\n{self}\nSOURCE: {source.Replace("\n", "\\n")}\n-----");
                    }
                }

                // runtime stub（corpus[0] 参照：内容与语料无关，只差分一次）
                if (i == 0)
                {
                    foreach (var stub in new[] { "ExitProcess", "TickCount", "WriteStr", "DivByZero", "StackOverflow" })
                    {
                        if (!csharpSym.TryGetValue(stub, out var csharpStub))
                        {
                            failures.Add($"corpus-0 [{stub}] no C# symbolic dump");
                            continue;
                        }

                        var selfStub = selfByFn.TryGetValue("0:" + stub, out var ss) ? ss : "<missing>";
                        if (csharpStub != selfStub)
                        {
                            failures.Add($"corpus-0 [{stub} stub] mismatch\n---C#-sym---\n{csharpStub}\n---SELF-sym---\n{selfStub}\n-----");
                        }
                    }
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        /// <summary>提取 C# COCOA_DUMP_SYM 中用户函数的符号化 hex。</summary>
        private static Dictionary<string, string> CShaSymbolicFunctions(string source)
        {
            const string target = "windows-x64";
            TargetPlatform.TryParse(target, out var platform);
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-nativelir-diff");
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "sym-" + Guid.NewGuid().ToString("N") + ".exe");
            var dumpFile = Path.Combine(Path.GetTempPath(), "cocoa-sym-x64.txt");

            var previous = Environment.GetEnvironmentVariable("COCOA_DUMP_SYM");
            Environment.SetEnvironmentVariable("COCOA_DUMP_SYM", "1");
            try
            {
                File.Delete(dumpFile);
                var diagnostics = compilation.EmitNative("test", exePath, platform);
                Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics));
                Assert.True(File.Exists(dumpFile), "COCOA_DUMP_SYM did not produce dump file.");
                return ParseSymbolDump(File.ReadAllText(dumpFile));
            }
            finally
            {
                if (previous == null)
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_SYM", null);
                }
                else
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_SYM", previous);
                }
            }
        }

        /// <summary>符号化 dump：`@@FUNC <name>` + 下一行 hex → 解析为 name→hex。</summary>
        private static Dictionary<string, string> ParseSymbolDump(string dump)
        {
            var result = new Dictionary<string, string>();
            var lines = dump.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("@@FUNC ", StringComparison.Ordinal))
                {
                    var name = lines[i].Substring(7).Trim();
                    var hex = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                    if (!result.ContainsKey(name))
                    {
                        result[name] = hex;
                    }
                }
            }

            return result;
        }

        private static (Dictionary<string, string> Results, List<string> CompileErrors) RunSelfDriverSym()
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

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return (new Dictionary<string, string>(), result.Diagnostics.Select(d => d.Message).ToList());
                }

                var output = writer.ToString().Replace("\r\n", "\n");
                return (ParseSymMarkers(output), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static Dictionary<string, string> ParseSymMarkers(string output)
        {
            var result = new Dictionary<string, string>();
            var lines = output.Split('\n');
            string? current = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("@@SYM:", StringComparison.Ordinal))
                {
                    current = line.Substring(6).Trim();
                    result[current] = "";
                }
                else if (current != null)
                {
                    var t = line.Trim();
                    if (t.Length > 0 && result[current]!.Length == 0)
                    {
                        result[current] = t;
                    }
                }
            }

            return result;
        }
    }
}