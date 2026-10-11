using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Contracts
{
    /// <summary>
    /// 闭包捕获跨后端语义契约（3.1）——捕获变量堆存储规范的唯一事实来源。
    /// 捕获变量必须存在堆上（宿主函数 Env 类 / lambda 环境对象），三后端读取/写入捕获变量时
    /// 一律经环境对象字段/槽，而非帧局部：
    /// <list type="number">
    /// <item>**宿主承载**：捕获变量登记在宿主函数的 <c>CapturedVariables</c>，环境类每宿主一个（<c>__Env_&lt;fn&gt;</c>）。</item>
    /// <item>**lambda 继承**：lambda 捕获继承最外层非 lambda 宿主函数的捕获集（链式环境）。</item>
    /// <item>**读**：捕获变量读 = 环境对象字段读（Evaluator 经 <c>ClosureEnvironment.Slots</c>；IL/Native 经 Env 类字段）。</item>
    /// <item>**写**：捕获变量写穿透环境对象字段，不回写帧局部。</item>
    /// <item>**独立实例**：每宿主调用建新环境实例——两个宿主实例捕获互不影响。</item>
    /// </list>
    /// 实现形态：Evaluator 用 <c>ClosureEnvironment</c>（Dictionary 槽）+ 环境栈；IL 用 Env 类实例字段；
    /// Native 用 Env 类对象（NativeObjectModel 布局）。形态各异，规则一致。
    /// </summary>
    public static class ClosureContract
    {
        /// <summary>环境类命名约定（Binder 合成；IL/Native 用此判定闭包环境类）。</summary>
        public const string EnvironmentClassPrefix = "__Env_";

        /// <summary>判定类是否为闭包环境类（三后端共用；对齐 Binder 合成命名）。</summary>
        public static bool IsEnvironmentClass(NamedTypeSymbol classType)
        {
            return classType.Name.StartsWith(EnvironmentClassPrefix, System.StringComparison.Ordinal);
        }

        /// <summary>宿主函数是否需要环境对象（捕获集非空）。</summary>
        public static bool RequiresEnvironment(FunctionSymbol function)
        {
            return function.CapturedVariables is { Count: > 0 };
        }
    }
}
