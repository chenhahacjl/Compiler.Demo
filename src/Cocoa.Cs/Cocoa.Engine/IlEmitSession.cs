using System.Reflection;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Managed.Writer;
using Cocoa.Targeting;

namespace Cocoa.Engine
{
    /// <summary>
    /// IlEmit 后端会话：脚本逐次发射为内存程序集（临时 dll + Assembly.Load），反射调用。
    /// 与 Interpreter 后端共享 submission 链语义（<see cref="Compilation.CreateScript"/> + Previous 链），
    /// 但每次提交重新发射整个程序（函数跨提交经链合并；顶层 final 表达式值经反射取 `$eval` 返回）。
    /// 已知限制（docs/嵌入式引擎API.md §9/§12）：无 Output 拦截；全局变量跨提交保持不支持
    /// （script 顶层变量在 IL 中是单函数局部，IlEmitter 未提升为静态字段）。
    /// </summary>
    internal sealed class IlEmitSession : IDisposable
    {
        private Compilation? _latest;
        private readonly List<string> _references = new();
        private readonly IlTarget _target;
        private Assembly? _assembly;
        private readonly string _tempDir;
        private int _emitSeq;

        public IlEmitSession(IlTarget? target = null)
        {
            _target = target ?? IlTarget.Default;
            _tempDir = Path.Combine(Path.GetTempPath(), "cocoa-engine-il", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // IlEmit 后端需 .NET BCL 引用（netcore 共享框架；netfx 产物无法被现代宿主加载，API §3.3）。
            var resolved = IlReferenceResolver.ResolveDefaultReferences(_target);
            _references.AddRange(resolved ?? ResolveHostReferences());
        }

        public Compilation? Latest => _latest;

        public void SetReferences(IEnumerable<string> references)
        {
            foreach (var reference in references)
            {
                if (reference.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // .coa 系统库由 CreateScript 自动装载；仅 .NET 程序集作为 IL 引用
                }

                if (!_references.Contains(reference, StringComparer.OrdinalIgnoreCase))
                {
                    _references.Add(reference);
                }
            }
        }

        public EngineResult DoString(string code)
        {
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.CreateScript(_latest, (string[]?)null, syntaxTree);

            var result = EmitAndInvoke(compilation, invokeEval: true);

            // 编译/绑定错误不持久化（对齐 REPL：仅成功提交进链）
            if (!result.Diagnostics.HasErrors())
            {
                _latest = compilation;
            }

            return result;
        }

        public object? Call(string functionName, params object?[] args)
        {
            if (_latest == null || _assembly == null)
            {
                throw new ArgumentException($"未找到可调用的顶层函数 '{functionName}'（引擎尚无任何脚本提交）", nameof(functionName));
            }

            var topLevel = _assembly.GetType("<CocoaTopLevel>");
            if (topLevel == null)
            {
                throw new ArgumentException($"未找到可调用的顶层函数 '{functionName}'（产物无顶层类型）", nameof(functionName));
            }

            var method = topLevel.GetMethod(functionName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                throw new ArgumentException($"未找到可调用的顶层函数 '{functionName}'", nameof(functionName));
            }

            return method.Invoke(null, args);
        }

        private EngineResult EmitAndInvoke(Compilation compilation, bool invokeEval)
        {
            // 发射到临时 dll（netcore 库形态：无 apphost/runtimeconfig，可反射加载）
            var parseDiagnostics = compilation.SyntaxTrees.SelectMany(st => st.Diagnostics);
            var path = Path.Combine(_tempDir, $"e{_emitSeq++}.dll");

            var diagnostics = compilation.Emit("Cocoa.Engine", _references.ToArray(), path, _target, emitLibrary: true);
            if (diagnostics.HasErrors())
            {
                return new EngineResult(parseDiagnostics.Concat(diagnostics).ToImmutableArray(), null);
            }

            // 内存加载（共享默认上下文，避免 LoadFile 独立上下文跨程序集 CLR 内部错误）
            var bytes = File.ReadAllBytes(path);
            _assembly = Assembly.Load(bytes);

            if (!invokeEval)
            {
                return new EngineResult(parseDiagnostics.Concat(diagnostics).ToImmutableArray(), null);
            }

            var topLevel = _assembly.GetType("<CocoaTopLevel>");
            if (topLevel == null)
            {
                return new EngineResult(parseDiagnostics.Concat(diagnostics).ToImmutableArray(), null);
            }

            var eval = topLevel.GetMethod("$eval", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var value = eval?.Invoke(null, Array.Empty<object>());
            return new EngineResult(parseDiagnostics.Concat(diagnostics).ToImmutableArray(), value);
        }

        private static IEnumerable<string> ResolveHostReferences()
        {
            yield return typeof(object).Assembly.Location;
            yield return typeof(System.Console).Assembly.Location;
        }

        public void Reset()
        {
            _latest = null;
            _assembly = null;
        }

        public void Dispose()
        {
            _assembly = null;
            if (Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
                catch
                {
                    // 程序集已加载到内存，临时文件解锁可能延迟；清理失败可接受
                }
            }
        }
    }
}