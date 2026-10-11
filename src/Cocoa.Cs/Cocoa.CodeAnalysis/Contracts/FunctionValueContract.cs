using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Contracts
{
    /// <summary>
    /// 函数值（方法组/委托/函数类型）跨后端语义契约（3.1）——函数值表示、接收者、调用列表的唯一事实来源。
    /// 三后端（Evaluator / IL / Native）在函数值创建与调用路径必须遵守：
    /// <list type="number">
    /// <item>**表示**：函数值 = 目标函数 + 接收者（实例方法持有接收者；静态方法接收者忽略）。</item>
    /// <item>**lambda 环境**：lambda 函数的接收者承载闭包环境（<c>ClosureEnvironment</c>/Env 类），
    /// 调用时不再把接收者当实例传递（<c>IsLambdaWithEnvironment</c> 判别）。</item>
    /// <item>**多目标**：重载函数值（方法组经重载解析归一为单函数；多播委托为调用列表）逐目标调用。</item>
    /// <item>**调用**：经函数值调用 = 普通调用同一函数，参数与返回类型按函数签名。</item>
    /// </list>
    /// 实现形态：Evaluator 用 <c>EvaluatorFunctionValue</c>（Function+Receiver，lambda 环境作 Receiver）；
    /// IL 用委托对象/方法 token；Native 用委托对象（vtable 槽）。形态各异，规则一致。
    /// </summary>
    public static class FunctionValueContract
    {
        /// <summary>函数值是否需要闭包环境承载（lambda 捕获 → 环境类非空）。</summary>
        public static bool CarriesEnvironment(FunctionSymbol function)
        {
            return function.IsLambdaWithEnvironment;
        }

        /// <summary>接收者语义：lambda 环境函数的环境对象即"接收者"，但调用时不作实例目标传参。</summary>
        public static bool ReceiverIsEnvironment(FunctionSymbol function)
        {
            return function.IsLambdaWithEnvironment;
        }

        /// <summary>函数值是否可调用（具名函数或 lambda 均可调用）。</summary>
        public static bool IsCallable(FunctionSymbol function)
        {
            return function != null;
        }
    }
}
