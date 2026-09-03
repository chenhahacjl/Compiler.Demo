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
            var csharpMembers = ParseKindMembers(csharpEnum);
            var cocoaMembers = ParseKindMembers(cocoaEnum);

            Assert.NotEmpty(csharpMembers);
            Assert.NotEmpty(cocoaMembers);

            foreach (var pair in csharpMembers)
            {
                Assert.True(cocoaMembers.TryGetValue(pair.Key, out var cocoaValue),
                    $"CocoaSyntaxKind 缺少成员：{pair.Key}（RawKind 值域须与共享 SyntaxKind 对齐）");
                Assert.True(cocoaValue == pair.Value,
                    $"SyntaxKind 值漂移：{pair.Key} CSharp={pair.Value} Cocoa={cocoaValue}（(int)Kind == RawKind 约定被破坏）");
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

        /// <summary>提取 kind 枚举成员 "Name = N," 的名字→值映射。</summary>
        private static Dictionary<string, int> ParseKindMembers(string path)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var pattern = new Regex("^\\s*(\\w+)\\s*=\\s*(\\d+)\\s*,?\\s*$", RegexOptions.Compiled);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var m = pattern.Match(line);
                if (m.Success)
                {
                    map[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
                }
            }

            return map;
        }
    }
}
