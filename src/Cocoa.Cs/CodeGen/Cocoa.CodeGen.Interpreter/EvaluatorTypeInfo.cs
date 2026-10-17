using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>
    /// GetType() / typeof() 的求值器侧类型信息：用户类没有 CLR Type，用全名字符串承载；
    /// 基元/CLR 对象的 GetType() 仍返回真实 System.Type（Name 切分逻辑对两者统一处理）。
    /// 按 FullName **interned 单例**：同一类型的 typeof/GetType() 引用同一实例，
    /// 使 `typeof(A) == typeof(B)` / `x.GetType() == typeof(C)` 的引用相等语义成立（C# 同类型恒同一 Type 实例）。
    /// </summary>
    internal sealed record EvaluatorTypeInfo(string FullName)
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EvaluatorTypeInfo> Cache = new();

        public static EvaluatorTypeInfo Get(string fullName) => Cache.GetOrAdd(fullName, static n => new EvaluatorTypeInfo(n));
    }
}