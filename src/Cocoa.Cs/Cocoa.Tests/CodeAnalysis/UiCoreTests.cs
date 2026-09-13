using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>6e-M25 1b：System.UI 纯逻辑核心（FNV-1a ID / ImGuiStorage / ImGuiDrawList）行为验证。</summary>
    public class UiCoreTests
    {
        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "Exception.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }

        private static string[] CoreSources()
        {
            var root = Path.Combine(RepoRoot(), "src", "Cocoa.UI");
            return new[]
            {
                Path.Combine(root, "ImTypes.co"),
                Path.Combine(root, "ImGuiID.co"),
                Path.Combine(root, "ImGuiStorage.co"),
                Path.Combine(root, "ImGuiDrawList.co"),
            };
        }

        private const string Harness = @"using System
using System.UI

function Main(): i32
{
    Console.WriteLine(ImGuiID.HashString("""") == -2128831035)
    Console.WriteLine(ImGuiID.HashString(""a"") == -468965076)

    var st = new ImGuiStorage(8)
    Console.WriteLine(st.GetInt(100, 7))
    st.SetInt(100, 42)
    Console.WriteLine(st.GetInt(100, 7))
    st.SetInt(101, 43)
    Console.WriteLine(st.GetInt(101, 7))
    st.SetBool(200, true)
    Console.WriteLine(st.GetBool(200, false))
    st.SetFloat(300, f32(1.5))
    Console.WriteLine(i32(st.GetFloat(300, f32(0.0)) * f32(2.0)))

    var dl = new ImGuiDrawList(64)
    dl.AddRectFilled(f32(0.0), f32(0.0), f32(10.0), f32(20.0), 123)
    Console.WriteLine(dl.VertexCount)
    Console.WriteLine(dl.IndexCount)
    return 0
}";

        private const string Expected = "True\nTrue\n7\n42\n43\nTrue\n3\n4\n6\n";

        [Fact]
        public void UiCore_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(Harness));

            var references = new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, trees.ToArray());

            var original = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);

                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            catch (Exception ex)
            {
                Assert.True(false, "EX: " + ex.Message + "\nOUT:\n" + writer.ToString());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}
