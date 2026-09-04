using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit
{
    /// <summary>
    /// 6e-M22 C8 / 6e-G7：集合泛型 facade（List&lt;T&gt;）经“源码方式”集成——
    /// 开放泛型含 `new T[]`，当前 .coa 序列化尚不支持（G7 待补），故以源码编译单态化验证
    /// foreach / 成员调用 与 三后端对齐。
    /// </summary>
    public class CollectionFacadeTests
    {
        private static int _exeSeq;
        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Collections", "List.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }

        private static (int ExitCode, string Stdout) EmitAndRun(string source, string name, params string[] coFiles)
        {
            return EmitAndRunCore(source, name, coFiles, allowNonZero: false);
        }

        /// <summary>编译并运行，允许非零退出码（未捕获异常场景）；返回退出码与输出供断言。</summary>
        private static (int ExitCode, string Stdout) EmitAndRunAllowNonZero(string source, string name, params string[] coFiles)
        {
            return EmitAndRunCore(source, name, coFiles, allowNonZero: true);
        }

        private static (int ExitCode, string Stdout) EmitAndRunCore(string source, string name, string[] coFiles, bool allowNonZero)
        {
            var collectionDir = Path.Combine(RepoRoot(), "src", "Cocoa.SDK", "System.Collections");
            var syntaxTrees = new List<Cocoa.CodeAnalysis.Syntax.SyntaxTree>
            {
                Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source),
            };
            // 集合接口集（Enumerable.co）始终参与编译：各集合类实现其中的泛型接口。
            syntaxTrees.Insert(0, Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(collectionDir, "Enumerable.co"))));
            // 异常基类（Exception.co）始终参与编译：throw/catch 仅接受 Exception 及其子类。
            syntaxTrees.Insert(0, Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(Path.Combine(RepoRoot(), "src", "Cocoa.SDK", "System.Core"), "Exception.co"))));

            foreach (var coFile in coFiles)
            {
                syntaxTrees.Insert(0, Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(collectionDir, coFile))));
            }

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(
                "Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                syntaxTrees.ToArray());

            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-coll-tests", $"{Environment.ProcessId:x}{Interlocked.Increment(ref _exeSeq):x3}-{name}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var diagnostics = compilation.Emit(
                name,
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                exePath,
                Cocoa.Targeting.IlTarget.Parse("net9.0"));

            Assert.Empty(string.Join("\n", diagnostics));

            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            var combined = stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "");
            if (process.ExitCode != 0 && !allowNonZero)
            {
                Assert.True(false, $"exit={process.ExitCode}\n{combined}");
            }

            return (process.ExitCode, combined);
        }

        [Fact]
        public void List_ForEach_IteratesAllElements_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var list = new List<i32>()
    list.Add(10)
    list.Add(20)
    list.Add(30)
    var sum = 0
    foreach (var x in list)
    {
        sum = sum + x
    }
    Console.WriteLine(sum)
    Console.WriteLine(list.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollForeach", "List.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("60", stdout);
            Assert.Contains("3", stdout);
        }

        [Fact]
        public void List_Indexer_ReadWrite_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var list = new List<i32>()
    list.Add(10)
    list.Add(20)
    list.Add(30)
    list[0] = 99
    Console.WriteLine(list[0])
    Console.WriteLine(list[2])
    Console.WriteLine(list.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollIndexer", "List.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("99", stdout);
            Assert.Contains("30", stdout);
            Assert.Contains("3", stdout);
        }

        [Fact]
        public void Dictionary_Indexer_ReadWrite_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var d = new Dictionary<string, i32>()
    d[""a""] = 1
    d[""b""] = 2
    Console.WriteLine(d[""a""])
    Console.WriteLine(d[""b""])
    Console.WriteLine(d.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollDict", "List.co", "Dictionary.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("1", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("2", stdout);
        }

        [Fact]
        public void Dictionary_TryGetValue_Keys_Values_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var d = new Dictionary<i32, i32>()
    d[10] = 1
    d[20] = 2
    d[30] = 3
    var v: i32
    if d.TryGetValue(30, out v) { Console.WriteLine(v) } else { Console.WriteLine(0) }
    var miss: i32
    if d.TryGetValue(99, out miss) { Console.WriteLine(miss) } else { Console.WriteLine(7) }
    var keys = d.Keys
    Console.WriteLine(keys.Length)
    var ki = 0
    while ki < keys.Length
    {
        Console.WriteLine(keys[ki])
        ki = ki + 1
    }
    var vals = d.Values
    Console.WriteLine(vals.Length)
    var vi = 0
    while vi < vals.Length
    {
        Console.WriteLine(vals[vi])
        vi = vi + 1
    }
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollDictTV", "List.co", "Dictionary.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("3", stdout);
            Assert.Contains("7", stdout);
            Assert.Contains("10", stdout);
            Assert.Contains("20", stdout);
            Assert.Contains("30", stdout);
            Assert.Contains("1", stdout);
            Assert.Contains("2", stdout);
        }

        [Fact]
        public void Dictionary_Remove_ContainsKey_Il()
        {
            // int 键：走到 SameKey 的 a.Equals(b) 分支（非 string 特判）
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var d = new Dictionary<i32, i32>()
    d[10] = 1
    d[20] = 2
    d[30] = 3
    Console.WriteLine(d.Count)
    d.Remove(20)
    Console.WriteLine(d.Count)
    Console.WriteLine(d[10])
    Console.WriteLine(d[30])
    d.Clear()
    Console.WriteLine(d.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollDictRM", "List.co", "Dictionary.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("3", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("1", stdout);
            Assert.Contains("0", stdout);
        }

        [Fact]
        public void List_Insert_RemoveAt_IndexOf_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var list = new List<i32>()
    list.Add(10)
    list.Add(30)
    list.Add(40)
    list.Insert(1, 20)
    Console.WriteLine(list[0])
    Console.WriteLine(list[1])
    Console.WriteLine(list[2])
    Console.WriteLine(list[3])
    Console.WriteLine(list.IndexOf(30))
    list.RemoveAt(1)
    Console.WriteLine(list.Count)
    Console.WriteLine(list[1])
    Console.WriteLine(list.IndexOf(999))
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollListIRI", "List.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("10", stdout);
            Assert.Contains("20", stdout);
            Assert.Contains("30", stdout);
            Assert.Contains("40", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("3", stdout);
            Assert.Contains("-1", stdout);
        }

        [Fact]
        public void HashSet_Add_Contains_Remove_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var hs = new HashSet<i32>()
    Console.WriteLine(hs.Add(10))
    Console.WriteLine(hs.Add(10))
    Console.WriteLine(hs.Add(20))
    Console.WriteLine(hs.Count)
    Console.WriteLine(hs.Contains(10))
    Console.WriteLine(hs.Contains(99))
    Console.WriteLine(hs.Remove(10))
    Console.WriteLine(hs.Remove(10))
    Console.WriteLine(hs.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollMainHashSet", "HashSet.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("True", stdout);
            Assert.Contains("False", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("1", stdout);
        }

        [Fact]
        public void Queue_Enqueue_Dequeue_Peek_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var q = new Queue<i32>()
    q.Enqueue(1)
    q.Enqueue(2)
    q.Enqueue(3)
    Console.WriteLine(q.Count)
    Console.WriteLine(q.Peek())
    Console.WriteLine(q.Dequeue())
    Console.WriteLine(q.Dequeue())
    Console.WriteLine(q.Count)
    q.Enqueue(4)
    Console.WriteLine(q.Dequeue())
    Console.WriteLine(q.Dequeue())
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollMainQueue", "Queue.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("3", stdout);
            Assert.Contains("1", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("1", stdout);
            Assert.Contains("4", stdout);
        }

        [Fact]
        public void Stack_Push_Pop_Peek_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var s = new Stack<i32>()
    s.Push(1)
    s.Push(2)
    s.Push(3)
    Console.WriteLine(s.Count)
    Console.WriteLine(s.Peek())
    Console.WriteLine(s.Pop())
    Console.WriteLine(s.Pop())
    Console.WriteLine(s.Count)
    s.Push(9)
    Console.WriteLine(s.Pop())
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollMainStack", "Stack.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("3", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("9", stdout);
            Assert.Contains("1", stdout);
        }

        [Fact]
        public void Dictionary_NegativeKey_Indexer_Il()
        {
            // 负键（含负哈希）曾在 BucketOf 取模得负桶下标 -> IndexOutOfRange 崩溃；
            // 现已在 BucketOf/Rehash 处对余数归一化，验证不再崩溃且读写正确。
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var d = new Dictionary<i32, i32>()
    d[-5] = 50
    d[-100] = 200
    Console.WriteLine(d[-5])
    Console.WriteLine(d[-100])
    Console.WriteLine(d.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollDictNeg", "List.co", "Dictionary.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("50", stdout);
            Assert.Contains("200", stdout);
            Assert.Contains("2", stdout);
        }

        [Fact]
        public void List_Implements_ICollection_Members_Il()
        {
            // 集合类声明 implements IList<T>/ICollection<T> 等，编译器在绑定期经
            // CheckInterfaceImplementation 校验成员齐全（含 Add(bool)/Contains/Remove/CopyTo/
            // IsReadOnly）；此测试以具体类型调用这些接口成员，验证形状对齐 BCL 泛型半边。
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var list = new List<i32>()
    Console.WriteLine(list.Add(10))
    Console.WriteLine(list.Add(10))
    Console.WriteLine(list.Count)
    Console.WriteLine(list.Contains(10))
    Console.WriteLine(list.Contains(99))
    Console.WriteLine(list.IndexOf(10))
    Console.WriteLine(list.Remove(10))
    Console.WriteLine(list.Remove(10))
    Console.WriteLine(list.Count)
    Console.WriteLine(list.IsReadOnly)
    var buf = new i32[4]
    list.Add(5)
    list.Add(6)
    list.CopyTo(buf, 1)
    Console.WriteLine(buf[1])
    Console.WriteLine(buf[2])
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollListColl", "List.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("True", stdout);
            Assert.Contains("False", stdout);
            Assert.Contains("2", stdout);
            Assert.Contains("0", stdout);
            Assert.Contains("5", stdout);
            Assert.Contains("6", stdout);
        }

        [Fact]
        public void HashSet_Implements_ICollection_And_Foreach_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var hs = new HashSet<i32>()
    Console.WriteLine(hs.Add(3))
    Console.WriteLine(hs.Add(3))
    Console.WriteLine(hs.Count)
    Console.WriteLine(hs.IsReadOnly)
    Console.WriteLine(hs.Contains(3))
    Console.WriteLine(hs.Contains(4))
    var buf = new i32[4]
    hs.Add(1)
    hs.Add(2)
    hs.CopyTo(buf, 0)
    Console.WriteLine(buf[0] + buf[1] + buf[2] + buf[3])
    var sum = 0
    foreach (var x in hs)
    {
        sum = sum + x
    }
    Console.WriteLine(sum)
    Console.WriteLine(hs.Remove(3))
    Console.WriteLine(hs.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollHashSet", "HashSet.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("True", stdout);
            Assert.Contains("False", stdout);
            Assert.Contains("1", stdout);
            Assert.Contains("6", stdout);
            Assert.Contains("2", stdout);
        }

        [Fact]
        public void Queue_Implements_IReadOnlyCollection_And_Foreach_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var q = new Queue<i32>()
    q.Enqueue(10)
    q.Enqueue(20)
    q.Enqueue(30)
    Console.WriteLine(q.Count)
    var sum = 0
    foreach (var x in q)
    {
        sum = sum + x
    }
    Console.WriteLine(sum)
    Console.WriteLine(q.Dequeue())
    Console.WriteLine(q.Peek())
    Console.WriteLine(q.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollQueue", "Queue.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("3", stdout);
            Assert.Contains("60", stdout);
            Assert.Contains("10", stdout);
            Assert.Contains("20", stdout);
            Assert.Contains("2", stdout);
        }

        [Fact]
        public void Stack_Implements_IReadOnlyCollection_And_Foreach_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var s = new Stack<i32>()
    s.Push(1)
    s.Push(2)
    s.Push(3)
    Console.WriteLine(s.Count)
    var sum = 0
    foreach (var x in s)
    {
        sum = sum + x
    }
    Console.WriteLine(sum)
    Console.WriteLine(s.Pop())
    Console.WriteLine(s.Peek())
    Console.WriteLine(s.Count)
}";
            var (exitCode, stdout) = EmitAndRun(source, "CollStack", "Stack.co");
            Assert.Equal(0, exitCode);
            Assert.Contains("3", stdout);
            Assert.Contains("6", stdout);
            Assert.Contains("2", stdout);
        }

        [Fact]
        public void Exception_TryCatch_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    try
    {
        throw new Exception(""boom"")
    }
    catch (e: Exception)
    {
        Console.WriteLine(e.Message)
    }
}";
            var (exitCode, stdout) = EmitAndRun(source, "ExcCatchStr");
            Assert.Equal(0, exitCode);
            Assert.Contains("boom", stdout);
        }

        [Fact]
        public void Exception_FinallyRuns_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var s = 0
    try
    {
        s = s + 1
    }
    finally
    {
        s = s + 10
    }
    Console.WriteLine(s)
}";
            var (exitCode, stdout) = EmitAndRun(source, "ExcFinally");
            Assert.Equal(0, exitCode);
            Assert.Contains("11", stdout);
        }

        [Fact]
        public void Exception_CatchFinally_Order_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    var s = 0
    try
    {
        throw new Exception(""x"")
        s = s + 1
    }
    catch (e: Exception)
    {
        s = s + 2
    }
    finally
    {
        s = s + 10
    }
    Console.WriteLine(s)
}";
            var (exitCode, stdout) = EmitAndRun(source, "ExcCatchFinally");
            Assert.Equal(0, exitCode);
            Assert.Contains("12", stdout);
        }

        [Fact]
        public void Exception_Uncaught_ExitsNonZero_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    throw new Exception(""fatal"")
}";
            var (exitCode, stdout) = EmitAndRunAllowNonZero(source, "ExcUncaught");
            Assert.NotEqual(0, exitCode);
        }

        [Fact]
        public void Exception_Subclass_CatchByBase_Il()
        {
            var source = @"using System
using System.Collections.Generic

class MyError extends Exception
{
    public constructor(message: string) extends base(message)
    {
    }
}

function Main()
{
    try
    {
        throw new MyError(""boom"")
    }
    catch (e: Exception)
    {
        Console.WriteLine(e.Message)
    }
}";
            var (exitCode, stdout) = EmitAndRun(source, "ExcSubclassBase");
            Assert.Equal(0, exitCode);
            Assert.Contains("boom", stdout);
        }

        [Fact]
        public void Exception_Subclass_CatchSpecific_Il()
        {
            var source = @"using System
using System.Collections.Generic

class MyError extends Exception
{
    public constructor(message: string) extends base(message)
    {
    }
}

function Main()
{
    try
    {
        throw new MyError(""boom"")
    }
    catch (e: MyError)
    {
        Console.WriteLine(e.Message)
    }
    Console.WriteLine(""done"")
}";
            var (exitCode, stdout) = EmitAndRun(source, "ExcSubclassSpecific");
            Assert.Equal(0, exitCode);
            Assert.Contains("boom", stdout);
            Assert.Contains("done", stdout);
        }

        [Fact]
        public void Exception_EmptyTry_Il()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    try
    {
    }
    catch (e: Exception)
    {
        Console.WriteLine(""caught"")
    }
    Console.WriteLine(""done"")
}";
            var (exitCode, stdout) = EmitAndRun(source, "ExcEmptyTry");
            Assert.Equal(0, exitCode);
            Assert.Contains("done", stdout);
        }

        [Fact]
        public void Exception_ThrowString_Diagnostic()
        {
            var source = @"using System
using System.Collections.Generic

function Main()
{
    throw ""boom""
}";
            var exceptionCo = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(Path.Combine(Path.Combine(RepoRoot(), "src", "Cocoa.SDK", "System.Core"), "Exception.co")));
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(
                Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source),
                exceptionCo);
            var diagnostics = compilation.Emit("main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, Path.Combine(Path.GetTempPath(), "cocoa-coll-tests", "throw-string-test.exe"));
            Assert.Contains(diagnostics, d => d.Message.Contains("Cannot throw"));
        }
    }
}
