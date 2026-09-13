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
            var files = new List<string>
            {
                Path.Combine(root, "ImTypes.co"),
                Path.Combine(root, "ImGuiID.co"),
                Path.Combine(root, "ImGuiStorage.co"),
                Path.Combine(root, "ImGuiDrawList.co"),
                Path.Combine(root, "ImGuiStyle.co"),
                Path.Combine(root, "ImGuiIO.co"),
                Path.Combine(root, "ImGuiWindow.co"),
                Path.Combine(root, "ImGui.co"),
                Path.Combine(root, "Declarative.co"),
            };
            files.AddRange(Directory.GetFiles(Path.Combine(root, "Widgets"), "*.co"));
            return files.ToArray();
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

    var style = new ImGuiStyle()
    style.MakeDark()
    Console.WriteLine(style.GetColor(1) != 0)

    var io = new ImGuiIO()
    io.SetMouseButton(0, true)
    Console.WriteLine(io.IsMouseDown(0))
    Console.WriteLine(io.IsMouseDown(1))
    io.SetKeyDown(65, true)
    Console.WriteLine(io.IsKeyDown(65))
    io.SetMouseButton(0, false)
    Console.WriteLine(io.IsMouseDown(0))

    var win = new ImGuiWindow(""W"", f32(0.0), f32(0.0), f32(100.0), f32(100.0))
    ImGuiLayout.ItemSize(win, f32(50.0), f32(20.0))
    ImGuiLayout.ItemSize(win, f32(50.0), f32(20.0))
    Console.WriteLine(i32(win.CursorY))
    return 0
}";

        private const string Expected = "True\nTrue\n7\n42\n43\nTrue\n3\n4\n6\nTrue\nTrue\nFalse\nTrue\nFalse\n20\n";

        [Fact]
        public void UiCore_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(Harness));
            AssertExpected(trees, Expected);
        }

        private const string WidgetHarness = @"using System
using System.UI

function Main(): i32
{
    var gui = new ImGui(256, 16)
    var io = new ImGuiIO()
    gui.NewFrame(io)
    gui.Begin(""W"", f32(0.0), f32(0.0), f32(300.0), f32(200.0))
    Console.WriteLine(gui.TrackBar(""v"", 1, 0, 10))
    Console.WriteLine(gui.GroupBox(""h"", 2))
    Console.WriteLine(gui.TreeView(""n"", 3))
    gui.EndTreeView()
    gui.Label(""hello"")
    gui.End()
    Console.WriteLine(gui.DrawList.VertexCount > 0)
    Console.WriteLine(gui.DrawList.TextCount)
    return 0
}";

        private const string WidgetExpected = "0\nTrue\nFalse\nTrue\n4\n";

        [Fact]
        public void ImGui_Widgets_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(WidgetHarness));
            AssertExpected(trees, WidgetExpected);
        }

        private const string InputHarness = @"using System
using System.UI

function Main(): i32
{
    var gui = new ImGui(256, 16)
    var io = new ImGuiIO()
    io.SetMouseButton(0, true)
    io.MouseX = f32(50.0)
    io.MouseY = f32(10.0)
    io.AddInputCharacter(65)
    io.AddInputCharacter(66)
    gui.NewFrame(io)
    gui.Begin(""W"", f32(0.0), f32(0.0), f32(300.0), f32(200.0))
    Console.WriteLine(gui.TextBox(""t"", 1, 16))
    gui.End()
    gui.Render()
    return 0
}";

        private const string InputExpected = "AB\n";

        [Fact]
        public void ImGui_InputText_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(InputHarness));
            AssertExpected(trees, InputExpected);
        }

        private const string ChildHarness = @"using System
using System.UI

function Main(): i32
{
    var gui = new ImGui(512, 16)
    var io = new ImGuiIO()
    io.MouseX = f32(50.0)
    io.MouseY = f32(50.0)
    io.MouseWheel = f32(0.0) - f32(2.0)
    gui.NewFrame(io)
    gui.Begin(""W"", f32(0.0), f32(0.0), f32(300.0), f32(200.0))
    gui.BeginPanel(30, f32(200.0), f32(80.0))
    gui.Label(""in child"")
    gui.EndPanel()
    gui.End()
    gui.Render()
    Console.WriteLine(gui.Storage.GetFloat(30, f32(0.0)))

    var dl = new ImGuiDrawList(64)
    dl.SetClipRect(f32(0.0), f32(0.0), f32(100.0), f32(50.0))
    dl.AddRectFilled(f32(0.0), f32(0.0), f32(100.0), f32(50.0), 0)
    Console.WriteLine(dl.VertexCount)
    dl.AddRectFilled(f32(200.0), f32(200.0), f32(300.0), f32(300.0), 0)
    Console.WriteLine(dl.VertexCount)
    dl.ClearClip()
    dl.AddRectFilled(f32(0.0), f32(0.0), f32(10.0), f32(10.0), 0)
    Console.WriteLine(dl.VertexCount)
    return 0
}";

        private const string ChildExpected = "48\n4\n4\n8\n";

        [Fact]
        public void ImGui_ChildScrollClip_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(ChildHarness));
            AssertExpected(trees, ChildExpected);
        }

        private const string StyleHarness = @"using System
using System.UI

function Main(): i32
{
    var gui = new ImGui(256, 16)
    var io = new ImGuiIO()
    gui.NewFrame(io)
    gui.Begin(""W"", f32(0.0), f32(0.0), f32(300.0), f32(200.0))
    gui.PushStyleColor(0, ImGuiStyle.Abgr(255, 0, 0, 255))
    gui.Label(""red"")
    gui.PopStyleColor()
    gui.Label(""normal"")
    gui.PushStyleVar(ImGuiStyleVar.ItemSpacingY, f32(20.0))
    gui.Label(""spaced"")
    gui.PopStyleVar()
    gui.End()
    Console.WriteLine(gui.DrawList.TextColor(0) == ImGuiStyle.Abgr(255, 0, 0, 255))
    Console.WriteLine(gui.DrawList.TextColor(1) == gui.Style.GetColor(0))
    Console.WriteLine(gui.DrawList.TextColor(2) == gui.Style.GetColor(0))
    var st = new ImGuiStyle()
    st.MakeClassic()
    Console.WriteLine(st.GetColor(1) == ImGuiStyle.Abgr(0, 0, 0, 255))
    return 0
}";

        private const string StyleExpected = "True\nTrue\nTrue\nTrue\n";

        [Fact]
        public void ImGui_PushStyle_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(StyleHarness));
            AssertExpected(trees, StyleExpected);
        }

        private const string DeclarativeHarness = @"using System
using System.UI

class Form extends UIView
{
    public override function Body(gui: ImGui): void
    {
        Ui.VStack(gui, () => {
            gui.Label(""title"")
            Ui.HStack(gui, () => {
                gui.Label(""a"")
                gui.Label(""b"")
            })
            Ui.Panel(gui, 5, f32(200.0), f32(60.0), () => {
                gui.Label(""in panel"")
            })
        })
    }
}

function Main(): i32
{
    var gui = new ImGui(512, 16)
    var io = new ImGuiIO()
    gui.NewFrame(io)
    gui.Begin(""W"", f32(0.0), f32(0.0), f32(300.0), f32(200.0))
    var form = new Form()
    Ui.Render(gui, form)
    gui.End()
    Console.WriteLine(gui.DrawList.TextX(1) < gui.DrawList.TextX(2))
    Console.WriteLine(gui.DrawList.TextY(1) == gui.DrawList.TextY(2))
    Console.WriteLine(gui.DrawList.TextCount)
    return 0
}";

        private const string DeclarativeExpected = "True\nTrue\n4\n";

        [Fact]
        public void ImGui_Declarative_Evaluator()
        {
            var trees = CoreSources().Select(p => SyntaxTree.Parse(File.ReadAllText(p))).ToList();
            trees.Add(SyntaxTree.Parse(DeclarativeHarness));
            AssertExpected(trees, DeclarativeExpected);
        }

        private static void AssertExpected(List<SyntaxTree> trees, string expected)
        {
            var references = new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, trees.ToArray());

            var original = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(expected, writer.ToString().Replace("\r\n", "\n"));
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
