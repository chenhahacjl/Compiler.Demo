using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>M7 调试器：一次函数/脚本调用的帧（函数符号 + 该帧的局部变量字典）。</summary>
    internal sealed class DebugFrame
    {
        public DebugFrame(FunctionSymbol function, Dictionary<VariableSymbol, object> locals)
        {
            Function = function;
            Locals = locals;
        }

        public FunctionSymbol Function { get; }

        public Dictionary<VariableSymbol, object> Locals { get; }
    }
}
