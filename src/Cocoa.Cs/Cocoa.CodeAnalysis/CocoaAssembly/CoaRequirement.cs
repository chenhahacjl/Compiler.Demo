namespace Cocoa.CodeAnalysis.CocoaAssembly
{
    /// <summary>
    /// `.coa` 后端要求（依赖清单 `requires`）。消费方后端不匹配 → 编译期报错。
    /// </summary>
    internal enum CoaRequirement
    {
        /// <summary>纯 Cocoa 函数/基础类型，双后端通用。</summary>
        Any,

        /// <summary>含 .NET API 或 OOP（class 实例化/继承），仅 IL 后端（native 需阶段 9 CLR Hosting / 对象模型后置）。</summary>
        DotNet,
    }
}
