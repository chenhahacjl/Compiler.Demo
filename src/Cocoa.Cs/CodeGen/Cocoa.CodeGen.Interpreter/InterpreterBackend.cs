using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>
    /// 解释器后端（4.1：Evaluation 迁出 Core）：经 <see cref="Register"/> 把求值实现注入
    /// <c>Cocoa.CodeAnalysis.Compilation</c>（Core 不引用后端，发射/求值能力经委托接入，
    /// 与 ManagedBackend/NativeBackend 同构）。宿主启动时调用 <see cref="Register"/>。
    /// </summary>
    public static class InterpreterBackend
    {
        /// <summary>
        /// 进程级输出拦截（CocoaEngine Output 事件底座，嵌入式引擎场景；WriteLine/Write 先经
        /// 此委托再落 Console）。进程级静态状态是引擎文档记录的已知边界（docs/嵌入式引擎API.md §10）。
        /// </summary>
        public static Action<string>? OutputWriter
        {
            get => Evaluator.OutputInterceptor;
            set => Evaluator.OutputInterceptor = value;
        }

        /// <summary>把解释器求值实现注册到 Core（宿主/测试模块初始化时调用）。</summary>
        public static void Register()
        {
            Compilation.RegisterInterpreterEvaluator(Evaluate);
            Compilation.RegisterInterpreterFunctionEvaluator(EvaluateFunction);
        }

        private static object? Evaluate(BoundProgram program, string[]? args, Dictionary<VariableSymbol, object> variables)
        {
            var evaluator = new Evaluator(program, variables);

            return args == null ? evaluator.Evaluate() : evaluator.Evaluate(args);
        }

        private static object? EvaluateFunction(BoundProgram program, Cocoa.CodeAnalysis.Symbols.FunctionSymbol function, object?[] args, Dictionary<VariableSymbol, object> variables)
        {
            var evaluator = new Evaluator(program, variables);
            return evaluator.EvaluateFunction(function, args);
        }
    }
}
