using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 方言双副本漂移检测（阶段 3a 决策：保留手写双份，用测试防静默漂移）。
    /// 节点文件：CSharp 与 Cocoa 对应文件必须"CSharp→Cocoa 归一后逐字节相同"，
    /// 白名单放行已知方言差异；Green 工厂 switch 入口集合必须等价（Cocoa ⊇ CSharp，差集仅 ForRangeStatement）。
    /// 若本测试变红：要么是把单侧改动忘了同步到另一方言（修掉），要么是蓄意分化（把文件加入白名单并写明原因）。
    /// </summary>
    public sealed class SyntaxDuplicationDriftTests
    {
        /// <summary>蓄意分化的节点文件（两侧都存在但允许内容不同）。</summary>
        private static readonly HashSet<string> DivergentNodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ForStatementSyntax.cs",
            "LambdaExpressionSyntax.cs",
        };

        /// <summary>Cocoa 独有节点（CSharp 侧无对应文件）。</summary>
        private static readonly HashSet<string> CocoaOnlyNodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ForRangeStatementSyntax.cs",
        };

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

        private static string NodesDir(string dialect)
            => Path.Combine(RepoRoot(), "src", "Cocoa.Cs", $"Cocoa.CodeAnalysis.{dialect}", "Syntax", "Nodes");

        private static string FactoryPath(string dialect)
            => Path.Combine(RepoRoot(), "src", "Cocoa.Cs", $"Cocoa.CodeAnalysis.{dialect}", "Syntax", "Green", $"{dialect}GreenNodeFactory.cs");

        /// <summary>蓄意分化的 Binder partial（Cocoa 特有 for..range 绑定）。同步义务仍适用于其余代码。</summary>
        private static readonly HashSet<string> DivergentBinderFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "CSharpBinder.Statements.cs",
        };

        /// <summary>Roslyn 式按语言独立的 Binder/Compilation/SemanticModel partial（3c 决策：不提取 BinderBase，双写+漂移防护）。</summary>
        private static readonly string[] BinderSyncFiles = new[]
        {
            Path.Combine("Binder", "Impl", "CSharpBinder.cs"),
            Path.Combine("Binder", "Impl", "CSharpBinder.Declarations.cs"),
            Path.Combine("Binder", "Impl", "CSharpBinder.Expressions.cs"),
            Path.Combine("Binder", "Impl", "CSharpBinder.Statements.cs"),
            Path.Combine("Binder", "Impl", "CSharpBinder.TypeResolution.cs"),
            Path.Combine("Compilation", "CSharpCompilation.cs"),
            Path.Combine("Compilation", "CSharpSemanticModel.cs"),
        };

        /// <summary>规范化比对文本：去掉纯注释行、using 行、空行——免疫注释语言/风格/位置与文件头差异，代码必须一致。</summary>
        private static string StripCommentLines(string text)
        {
            var sb = new StringBuilder();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("using ", StringComparison.Ordinal)
                    || trimmed.Length == 0)
                {
                    continue;
                }

                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        [Fact]
        public void Binder_Files_Stay_In_Sync_Modulo_Prefix_And_Comments()
        {
            var csharpRoot = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.CodeAnalysis.CSharp");
            var cocoaRoot = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.CodeAnalysis.Cocoa");

            foreach (var rel in BinderSyncFiles)
            {
                var name = Path.GetFileName(rel);
                var csharpPath = Path.Combine(csharpRoot, rel);
                var cocoaPath = Path.Combine(cocoaRoot, rel.Replace("CSharp", "Cocoa"));
                Assert.True(File.Exists(cocoaPath), $"Cocoa 侧缺少对应文件：{rel.Replace("CSharp", "Cocoa")}");

                if (DivergentBinderFiles.Contains(name))
                {
                    continue;
                }

                var csharp = StripCommentLines(File.ReadAllText(csharpPath, Encoding.UTF8).Replace("CSharp", "Cocoa"));
                var cocoa = StripCommentLines(File.ReadAllText(cocoaPath, Encoding.UTF8));
                Assert.True(csharp == cocoa,
                    $"Binder 文件代码漂移（注释外不再相同）：{rel}。单侧修复必须双写到另一方言；蓄意分化时加入 DivergentBinderFiles 白名单。");
            }
        }

        [Fact]
        public void Dialect_SyntaxFacts_Stay_Aligned_With_Shared()
        {
            var sharedPath = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.CodeAnalysis", "Syntax", "SyntaxFacts.cs");
            var shared = StripCommentLines(File.ReadAllText(sharedPath, Encoding.UTF8));

            foreach (var dialect in new[] { "CSharp", "Cocoa" })
            {
                var dialectPath = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", $"Cocoa.CodeAnalysis.{dialect}", "Syntax", $"{dialect}SyntaxFacts.cs");
                var text = File.ReadAllText(dialectPath, Encoding.UTF8)
                    .Replace("CSharp", "Cocoa")
                    .Replace("CocoaSyntaxFacts", "SyntaxFacts")
                    .Replace("namespace Cocoa.CodeAnalysis.Cocoa.Syntax", "namespace Cocoa.CodeAnalysis.Syntax");
                var dialectCode = StripCommentLines(text);
                Assert.True(dialectCode == shared,
                    $"方言 SyntaxFacts 与共享 SyntaxFacts 漂移：{dialect}SyntaxFacts.cs。该类是 Roslyn 式公开 API 面，必须与共享实现保持同步（同步实现，或在两侧蓄意分化时更新本测试）。");
            }
        }

        [Fact]
        public void Node_Files_Are_Identical_Modulo_Dialect_Prefix()
        {
            var csharpDir = NodesDir("CSharp");
            var cocoaDir = NodesDir("Cocoa");
            var csharpFiles = Directory.GetFiles(csharpDir, "*.cs");
            Assert.NotEmpty(csharpFiles);

            foreach (var csharpPath in csharpFiles)
            {
                var name = Path.GetFileName(csharpPath);
                var cocoaPath = Path.Combine(cocoaDir, name);
                Assert.True(File.Exists(cocoaPath), $"Cocoa 侧缺少对应节点文件：{name}");

                if (DivergentNodes.Contains(name))
                {
                    continue;
                }

                var csharp = File.ReadAllText(csharpPath, Encoding.UTF8).Replace("CSharp", "Cocoa");
                var cocoa = File.ReadAllText(cocoaPath, Encoding.UTF8);
                Assert.True(csharp == cocoa,
                    $"节点文件漂移（CSharp 与 Cocoa 不再相同）：{name}。同步两份或蓄意分化时加入白名单。");
            }

            foreach (var cocoaPath in Directory.GetFiles(cocoaDir, "*.cs"))
            {
                var name = Path.GetFileName(cocoaPath);
                Assert.True(DivergentNodes.Contains(name) || CocoaOnlyNodes.Contains(name)
                            || File.Exists(Path.Combine(csharpDir, name)),
                    $"Cocoa 侧出现 CSharp 没有的新节点：{name}。若蓄意分化请更新白名单。");
            }
        }

        [Fact]
        public void Green_Factory_Switch_Entries_Are_Equivalent()
        {
            var csharp = ParseFactoryEntries(FactoryPath("CSharp"));
            var cocoa = ParseFactoryEntries(FactoryPath("Cocoa"));

            Assert.NotEmpty(csharp);
            Assert.NotEmpty(cocoa);

            foreach (var pair in csharp)
            {
                Assert.True(cocoa.ContainsKey(pair.Key),
                    $"Cocoa 工厂缺少 switch 入口：SyntaxKind.{pair.Key} => {pair.Value}（会静默回落默认红节点路径）");
                Assert.Equal(pair.Value, cocoa[pair.Key]);
            }

            foreach (var kind in cocoa.Keys)
            {
                if (kind == "ForRangeStatement")
                {
                    continue;
                }

                Assert.True(csharp.ContainsKey(kind),
                    $"Cocoa 工厂多出 CSharp 没有的入口：SyntaxKind.{kind}（若蓄意分化请更新本测试）");
            }
        }

        [Fact]
        public void Dialect_Kind_Enums_Stay_Value_Aligned()
        {
            var csharpEnum = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.CodeAnalysis.CSharp", "CSharpSyntaxKind.cs");
            var cocoaEnum = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.CodeAnalysis.Cocoa", "CocoaSyntaxKind.cs");
            var sharedEnum = Path.Combine(RepoRoot(), "src", "Cocoa.Cs", "Cocoa.CodeAnalysis", "Syntax", "SyntaxKind.cs");
            var csharpMembers = ParseKindMembers(csharpEnum);
            var cocoaMembers = ParseKindMembers(cocoaEnum);
            var sharedMembers = ParseKindMembers(sharedEnum);

            Assert.NotEmpty(csharpMembers);
            Assert.NotEmpty(cocoaMembers);
            Assert.NotEmpty(sharedMembers);

            foreach (var pair in csharpMembers)
            {
                Assert.True(cocoaMembers.TryGetValue(pair.Key, out var cocoaValue),
                    $"CocoaSyntaxKind 缺少成员：{pair.Key}（RawKind 值域须与共享 SyntaxKind 对齐）");
                Assert.True(cocoaValue == pair.Value,
                    $"SyntaxKind 值漂移：{pair.Key} CSharp={pair.Value} Cocoa={cocoaValue}（(int)Kind == RawKind 约定被破坏）");
                Assert.True(sharedMembers.TryGetValue(pair.Key, out var sharedValue),
                    $"共享 SyntaxKind 缺少方言成员：{pair.Key}（三枚举一致性收口被破坏）");
                Assert.True(sharedValue == pair.Value,
                    $"SyntaxKind 值漂移：{pair.Key} CSharp={pair.Value} Shared={sharedValue}（(int)Kind == RawKind 约定被破坏）");
            }

            foreach (var pair in cocoaMembers)
            {
                Assert.True(sharedMembers.TryGetValue(pair.Key, out var sharedValue),
                    $"共享 SyntaxKind 缺少方言成员：{pair.Key}（三枚举一致性收口被破坏）");
                Assert.True(sharedValue == pair.Value,
                    $"SyntaxKind 值漂移：{pair.Key} Cocoa={pair.Value} Shared={sharedValue}（(int)Kind == RawKind 约定被破坏）");
            }
        }

        /// <summary>提取工厂 switch 中 "SyntaxKind.X => BuildY" 入口（kind 名 → Build 方法名）。</summary>
        private static Dictionary<string, string> ParseFactoryEntries(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var pattern = new Regex("SyntaxKind\\.(\\w+)\\s*=>\\s*(Build\\w+)", RegexOptions.Compiled);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var m = pattern.Match(line);
                if (m.Success)
                {
                    map[m.Groups[1].Value] = m.Groups[2].Value;
                }
            }

            return map;
        }

        /// <summary>提取 kind 枚举成员（支持显式 "Name = N," 与隐式 "Name,"（值递增），容忍行内注释）。</summary>
        private static Dictionary<string, int> ParseKindMembers(string path)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var pattern = new Regex("^\\s*(\\w+)\\s*(?:=\\s*(\\d+))?\\s*,\\s*(?://.*)?$", RegexOptions.Compiled);
            var next = 0;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var m = pattern.Match(line);
                if (m.Success)
                {
                    var value = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : next;
                    map[m.Groups[1].Value] = value;
                    next = value + 1;
                }
            }

            return map;
        }
    }
}
