using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Evaluation
{
    /// <summary>
    /// Evaluator 用户类实例的运行时表示（6e-M19 M3-c）：类符号 + 扁平化字段槽（基类在前、声明序）。
    /// 虚调用沿 <see cref="Class"/> 继承链找最近实现（镜像 IL/CLR vtable 槽复用语义，为 M4 native 打样）。
    /// </summary>
    internal sealed class EvaluatorObject
    {
        public EvaluatorObject(NamedTypeSymbol @class, object?[] fields)
        {
            Class = @class;
            Fields = fields;
        }

        public NamedTypeSymbol Class { get; }

        public object?[] Fields { get; }
    }

    /// <summary>
    /// GetType() 的求值器侧类型信息：用户类没有 CLR Type，用全名字符串承载；
    /// 基元/CLR 对象的 GetType() 仍返回真实 System.Type（Name 切分逻辑对两者统一处理）。
    /// </summary>
    internal sealed record EvaluatorTypeInfo(string FullName);
}
