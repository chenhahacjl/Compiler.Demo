using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量一 M7-a2：自举 Lexer 与 C# 版 <see cref="CocoaLexer"/>（共享 LexerBase）逐 token 差分。
    /// 口径：**非 trivia 逐 token 严格**（kind 映射名 / offset / 长度 / 行列）；trivia（空白/注释/文档注释）
    /// 本轮只记录差异清单（见 自举实施计划.md），不断言。
    /// 语料：samples/ 下全部 .co + 内嵌边界用例。
    /// 传输协议：自举侧 DescribeDetail() 输出 `KIND offset len line:col`（不含 text，规避含换行 text 歧义）。
    /// </summary>
    public class LexerDifferentialTests
    {
        private sealed class TokenRec
        {
            public string Kind;
            public int Offset;
            public int Len;
            public int Line;
            public int Col;

            public TokenRec(string kind, int offset, int len, int line, int col)
            {
                Kind = kind;
                Offset = offset;
                Len = len;
                Line = line;
                Col = col;
            }

            public override string ToString() => $"{Kind} offset={Offset} len={Len} {Line}:{Col}";
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

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

        private static IEnumerable<(string Name, string Text)> Corpora()
        {
            var root = RepoRoot();
            var samplesDir = Path.Combine(root, "samples");
            foreach (var file in Directory.EnumerateFiles(samplesDir, "*.co", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(samplesDir, file).Replace('\\', '/');
                yield return (name, File.ReadAllText(file));
            }

            var curated = new[]
            {
                // 字符串家族
                ("edge-strings",
                    "var a = \"plain\"\nvar b = \"esc \\n \\t \\\\ \\\" \\u0041 \\x42 \\U00000043\"\nvar c = \"unterminated\n"),
                ("edge-verbatim",
                    "var v = @\"verbatim \"\" quote\"\nsecond line\"\nvar w = @\"unterminated\n"),
                ("edge-raw",
                    "var r = \"\"\"raw\n    content\n    \"\"\"\nvar s = \"\"\"unterminated\nvar t = \"\"\"\"raw4\"\"\"\"\n"),
                ("edge-interpolated",
                    "var i = $\"val={x} and {{brace}} and }}brace2}\"\nvar j = $\"hole string {\"a}\"} done\"\nvar k = @$\"verb {y}\"\nvar m = $\"{'}'} char hole\"\n"),
                // 字符字面量
                ("edge-chars",
                    "var c1 = 'a'\nvar c2 = '\\n'\nvar c3 = '\\u0041'\nvar c4 = '\\x41'\nvar c5 = ''\nvar c6 = 'ab'\n"),
                // 数字
                ("edge-numbers",
                    "var n1 = 0xFF\nvar n2 = 0x_FF\nvar n3 = 0b1010\nvar n4 = 0b1010_1\nvar n5 = 1_000_000\nvar n6 = 42L\nvar n7 = 42UL\nvar n8 = 42lu\nvar n9 = 1.5f\nvar n10 = 1e-5F\nvar n11 = 3.14\nvar n12 = 0xFFul\nvar n13 = 1u\nvar n14 = 1.5F\nvar n15 = 1234long\n"),
                // 运算符（三/两/单字符）
                ("edge-operators",
                    "a <<= b\nc >>= d\ne ??= f\ng?.h\ni ?? j\nk -> l\nm..n\no++\np--\nq += r\ns -= t\nu *= v\nw /= x\ny %= z\naa &= bb\ncc |= dd\nee ^= ff\ngg == hh\nii != jj\nkk << ll\nmm >> nn\n= => < <= > >= && || ! ? . ; , : ( ) [ ] { } ~ ^ & | % + - * /\n"),
                // 非法字符 / 错误 token
                ("edge-badchars", "# @ $ ` ^~`\n"),
                // 注释 / trivia
                ("edge-comments", "// line\n/* block */\n/// doc\n/* unterminated\n"),
                // 标识符 / 关键字全集
                ("edge-keywords",
                    "@ident @class @if _private public function class interface struct enum namespace using static var let const\n" +
                    "abstract as base break case cdecl continue default else false for foreach get set property event delegate\n" +
                    "constructor import in is internal new null nameof out override partial params protected readonly ref sealed\n" +
                    "step switch this virtual when while return stdcall syscall to true do extends extern where facade throw try\n" +
                    "catch finally and or not lock checked unchecked yield field\n"),
            };

            foreach (var (name, text) in curated)
            {
                yield return (name, text);
            }
        }

        private static string MapKind(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.EndOfFileToken: return "EOF";
                case SyntaxKind.IdentifierToken: return "Identifier";
                case SyntaxKind.NumberToken: return "Number";
                case SyntaxKind.DoubleToken: return "Double";
                case SyntaxKind.StringToken: return "String";
                case SyntaxKind.VerbatimStringToken: return "VerbatimString";
                case SyntaxKind.RawStringToken: return "RawString";
                case SyntaxKind.InterpolatedStringToken: return "InterpolatedString";
                case SyntaxKind.CharToken: return "Char";
                case SyntaxKind.BadToken: return "Error";
                default:
                    if (kind.ToString().EndsWith("Keyword")) return "Keyword";
                    return "Symbol";
            }
        }

        private static List<TokenRec> ReferenceTokens(string text)
        {
            var sourceText = SourceText.From(text);
            var tokens = SyntaxTree.ParseTokens(sourceText, includeEndOfFile: true);
            var result = new List<TokenRec>();
            foreach (var t in tokens)
            {
                if (t.Kind.ToString().EndsWith("Trivia"))
                {
                    continue;
                }

                var lineIndex = sourceText.GetLineIndex(t.Position);
                var line = sourceText.Lines[lineIndex];
                result.Add(new TokenRec(MapKind(t.Kind), t.Position, t.Text.Length, lineIndex + 1, t.Position - line.Start + 1));
            }

            return result;
        }

        private static string MainSource(string embedded)
        {
            return "using Cocoa.CodeAnalysis.Syntax\nusing System\n\nfunction Main(): i32\n{\n    let lex = Cocoa.CodeAnalysis.Syntax.Lexer.Create(\"" + embedded + "\")\n    while true\n    {\n        let t = lex.Next()\n        System.Console.WriteLine(t.DescribeDetail())\n        if t.Kind() == \"EOF\"\n        {\n            break\n        }\n    }\n    return 0\n}";
        }

        private static ImmutableArray<SyntaxTree> BuildTrees(string program)
        {
            var lexerCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Lexer.co"));
            var tokenCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Compiler", "Syntax", "Token.co"));
            return ImmutableArray.Create(SyntaxTree.Parse(lexerCo), SyntaxTree.Parse(tokenCo), SyntaxTree.Parse(MainSource(program)));
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static List<TokenRec> SelfTokens(string text)
        {
            var embedded = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), BuildTrees(embedded).ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                var output = writer.ToString().Replace("\r\n", "\n");
                return ParseRecords(output);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static List<TokenRec> ParseRecords(string output)
        {
            var result = new List<TokenRec>();
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Trim().Split(' ');
                if (parts.Length != 4)
                {
                    continue;
                }

                var offset = int.Parse(parts[1]);
                var len = int.Parse(parts[2]);
                var lc = parts[3].Split(':');
                result.Add(new TokenRec(parts[0], offset, len, int.Parse(lc[0]), int.Parse(lc[1])));
            }

            return result;
        }

        [Fact]
        public void NonTrivia_TokenStream_Matches_CocoaLexer_ForAllCorpora()
        {
            var failures = new List<string>();
            foreach (var (name, raw) in Corpora())
            {
                var text = Normalize(raw);
                var reference = ReferenceTokens(text);
                var self = SelfTokens(text);

                if (reference.Count != self.Count)
                {
                    var common = Math.Min(reference.Count, self.Count);
                    var firstDivergence = Enumerable.Range(0, common).FirstOrDefault(i =>
                        reference[i].Kind != self[i].Kind ||
                        reference[i].Offset != self[i].Offset ||
                        reference[i].Len != self[i].Len);
                    var ctx = "";
                    if (firstDivergence > 0)
                    {
                        ctx = SourceContext(text, reference[firstDivergence].Offset);
                    }

                    failures.Add($"[{name}] token count mismatch: C#={reference.Count} self={self.Count}, first divergence at #{firstDivergence}: " +
                        $"C#={(firstDivergence < reference.Count ? reference[firstDivergence].ToString() : "<end>")} " +
                        $"self={(firstDivergence < self.Count ? self[firstDivergence].ToString() : "<end>")}\n{ctx}");
                    continue;
                }

                for (var i = 0; i < reference.Count; i++)
                {
                    var r = reference[i];
                    var s = self[i];
                    if (r.Kind != s.Kind || r.Offset != s.Offset || r.Len != s.Len || r.Line != s.Line || r.Col != s.Col)
                    {
                        failures.Add($"[{name}] token #{i} mismatch: C#={r} self={s}\n{SourceContext(text, r.Offset)}");
                    }
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string SourceContext(string text, int offset)
        {
            var lineIndex = 0;
            var lineStart = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    if (offset < i)
                    {
                        break;
                    }

                    lineStart = i + 1;
                    lineIndex++;
                }
            }

            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            return $"  source line {lineIndex + 1}: \"{text.Substring(lineStart, lineEnd - lineStart)}\"";
        }
    }
}
