using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>
    /// GetType() 的求值器侧类型信息：用户类没有 CLR Type，用全名字符串承载；
    /// 基元/CLR 对象的 GetType() 仍返回真实 System.Type（Name 切分逻辑对两者统一处理）。
    /// </summary>
    internal sealed record EvaluatorTypeInfo(string FullName);
}