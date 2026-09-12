using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Cocoa.CodeGen.Interpreter;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    public class DebuggerSessionTests
    {
        private static Compilation Compile(string text, string fileName)
        {
            var tree = SyntaxTree.Parse(SourceText.From(text, fileName), Language.Cocoa);
            return Compilation.CreateScript(null, tree);
        }

        [Fact]
        public void Breakpoint_Stops_Then_Continue_Completes()
        {
            var compilation = Compile("var a = 1\na = 2\nreturn a", "bp.co");
            var session = DebuggerSession.Create(compilation);
            session.SetBreakpoint("bp.co", 2);

            var pauses = new List<DebugPauseReason>();
            session.Paused += r => pauses.Add(r);

            session.Start();
            Assert.True(session.WaitForPause(TimeSpan.FromSeconds(10)), "应在断点处暂停");
            Assert.Equal(DebugPauseReason.Breakpoint, session.PauseReason);
            Assert.InRange(session.CallStack[0].Line, 2, 2);
            Assert.Contains(session.CurrentLocals!, l => l.Name == "a");

            session.Continue();
            Assert.True(session.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.Null(session.UnhandledException);
            Assert.Equal(DebugExecutionState.Completed, session.State);
            Assert.Equal(2, session.ReturnValue);
            Assert.Single(pauses);
        }

        [Fact]
        public void StepInto_Visits_Multiple_SequencePoints()
        {
            var session = DebuggerSession.Create(Compile("var a = 1\na = 2\na = 3\nreturn a", "step.co"));
            session.BreakAtEntry = true;

            var lines = new List<int>();
            session.Paused += _ => lines.Add(session.CallStack[0].Line);

            session.Start();
            Assert.True(session.WaitForPause(TimeSpan.FromSeconds(10)), "应在入口暂停");

            for (var i = 0; i < 6 && session.State != DebugExecutionState.Completed; i++)
            {
                session.StepInto();
                if (!session.WaitForPause(TimeSpan.FromSeconds(5))) break;
            }

            session.Stop();
            session.WaitForExit(TimeSpan.FromSeconds(5));

            Assert.True(lines.Count >= 3, $"应至少步进到 3 个语句，实际 {lines.Count}：[{string.Join(",", lines)}]");
            Assert.Contains(2, lines);
            Assert.Contains(3, lines);
        }

        [Fact]
        public void No_Breakpoint_Runs_To_Completion()
        {
            var compilation = Compile("var a = 0\na = a + 5\nreturn a", "none.co");
            var session = DebuggerSession.Create(compilation);

            var paused = false;
            session.Paused += _ => paused = true;

            session.Start();
            Assert.True(session.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.False(paused);
            Assert.Equal(5, session.ReturnValue);
        }

        [Fact]
        public void Breakpoint_On_Class_Main_Program()
        {
            // IDE 调试路径：Compilation.Create(entry, references, trees)（非脚本）
            var text =
                "namespace App\n" +
                "{\n" +
                "    public class Program\n" +
                "    {\n" +
                "        static function Main()\n" +
                "        {\n" +
                "            var a = 1\n" +
                "            var b = a + 2\n" +
                "        }\n" +
                "    }\n" +
                "}";
            var tree = SyntaxTree.Parse(SourceText.From(text, "prog.co"), Language.Cocoa);
            var compilation = Compilation.Create(Array.Empty<string>(), tree);

            var session = DebuggerSession.Create(compilation);
            session.SetBreakpoint("prog.co", 8);

            session.Start();
            Assert.True(session.WaitForPause(TimeSpan.FromSeconds(10)), "应在第 8 行断点暂停");
            Assert.Contains(session.CurrentLocals!, l => l.Name == "a");
            Assert.Equal(8, session.CallStack[0].Line);

            session.Continue();
            Assert.True(session.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.Null(session.UnhandledException);
        }
    }
}
