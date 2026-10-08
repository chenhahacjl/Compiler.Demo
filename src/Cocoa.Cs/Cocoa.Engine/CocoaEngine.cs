using System.Collections.Immutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Interpreter;

namespace Cocoa.Engine
{
    /// <summary>
    /// 嵌入式 Cocoa 脚本引擎：C# 宿主进程内执行 `.co` 脚本（Lua 嵌入模型，见 docs/嵌入式引擎API.md）。
    /// 实例隔离：submission 链 / 全局变量字典 / 回调表独立；连续 <see cref="DoString"/> 共享函数与顶层变量。
    /// `System.Core.coa` 系统库随编译自动装载，开箱可用。
    /// </summary>
    public sealed class CocoaEngine : IDisposable
    {
        private Compilation? _previous;
        private readonly Dictionary<VariableSymbol, object> _variables = new();
        private readonly List<string> _references = new();
        private readonly EngineBackend _backend;
        private bool _disposed;

        public CocoaEngine(EngineBackend backend = EngineBackend.Interpreter)
        {
            _backend = backend;
            InterpreterBackend.Register();
            if (backend == EngineBackend.IlEmit)
            {
                Cocoa.CodeGen.Managed.Writer.ManagedBackend.Register();
            }
        }

        public CocoaEngine(EngineOptions options)
            : this(options?.Backend ?? EngineBackend.Interpreter)
        {
            if (options?.References != null)
            {
                foreach (var reference in options.References)
                {
                    if (!_references.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    {
                        _references.Add(reference);
                    }
                }
            }
        }

        /// <summary>脚本输出（仅 Interpreter 后端；WriteLine/Write 拦截后发往订阅者）。</summary>
        public event EventHandler<string>? Output;

        /// <summary>运行期/编译错误回调（异常也照常抛出，宿主可选订阅）。</summary>
        public event EventHandler<EngineError>? Error;

        /// <summary>执行后端。</summary>
        public EngineBackend Backend => _backend;

        /// <summary>
        /// 执行一段 Co 代码。语义对齐 REPL 单次提交：语法树解析 → 绑定 → 求值；
        /// 编译错误返回 <see cref="EngineResult.Diagnostics"/> 且不改变引擎状态（该提交不持久化）；
        /// 顶层最后表达式值经 <see cref="EngineResult.Value"/> 返回。
        /// Interpretor 后端另捕获 WriteLine/Write 输出并经 <see cref="Output"/> 事件广播。
        /// </summary>
        public EngineResult DoString(string code)
        {
            ThrowIfDisposed();

            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.CreateScript(
                _previous,
                _references.Count > 0 ? _references.ToArray() : null,
                syntaxTree);

            // Interpretor 后端：拦截 WriteLine/Write；IlEmit 后端无拦截（Output 仅 Interpreter，API §4.1/§9）。
            var captured = _backend == EngineBackend.Interpreter ? new List<string>() : null;
            var previous = InterpreterBackend.OutputWriter;
            if (captured != null)
            {
                InterpreterBackend.OutputWriter = text => captured.Add(text);
            }

            try
            {
                var result = compilation.Evaluate(_variables);

                // 编译/绑定错误不持久化（对齐 REPL：仅成功提交进链）
                if (!result.Diagnostics.HasErrors())
                {
                    _previous = compilation;
                }

                if (captured != null && Output != null)
                {
                    foreach (var text in captured)
                    {
                        Output(this, text);
                    }
                }

                return new EngineResult(result.Diagnostics, result.Value);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, new EngineError(ex.Message));
                throw;
            }
            finally
            {
                InterpreterBackend.OutputWriter = previous;
            }
        }

        /// <summary>读取脚本已声明的顶层全局变量；未声明名称返回 null。</summary>
        public object? GetGlobal(string name)
        {
            ThrowIfDisposed();

            var symbol = FindVariable(name);
            if (symbol == null)
            {
                return null;
            }

            _variables.TryGetValue(symbol, out var value);
            return value;
        }

        /// <summary>
        /// 写入已声明的顶层全局变量。Cocoa 为静态语言：未声明变量抛 <see cref="ArgumentException"/>
        /// （不自动创建隐式全局）。
        /// </summary>
        public void SetGlobal(string name, object? value)
        {
            ThrowIfDisposed();

            var symbol = FindVariable(name);
            if (symbol == null)
            {
                throw new ArgumentException($"未声明的全局变量 '{name}'（Cocoa 静态语言，SetGlobal 不创建隐式全局）", nameof(name));
            }

            _variables[symbol] = value!;
        }

        /// <summary>重置引擎状态：清空 submission 链与全局变量字典。</summary>
        public void Reset()
        {
            _previous = null;
            _variables.Clear();
        }

        private VariableSymbol? FindVariable(string name)
        {
            var submission = _previous;
            while (submission != null)
            {
                var variable = submission.Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
                if (variable != null)
                {
                    return variable;
                }

                submission = submission.Previous;
            }

            return null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CocoaEngine));
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}