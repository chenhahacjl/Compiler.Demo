using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeGen.Interpreter
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
}
