namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语言方言：`.co` 宽松主方言（Cocoa + C# 式写法兼收）与 `.cs` 严格 C# 方言子集（6e-M15 双前端拆分）。
    /// 两方言共享表达式引擎 / 语法节点 / Binder / 三后端，差异只在核心拼写层（参数/局部/成员/分号/for/foreach）。
    /// </summary>
    public enum LanguageDialect
    {
        /// <summary>Cocoa 主方言（`.co`）：Cocoa 写法 + C# 式兼容写法，分号可选。</summary>
        Cocoa,

        /// <summary>严格 C# 方言（`.cs`）：仅 C# 式拼写、分号必选；拒绝 Cocoa 专属拼写（`function`/`let`/`name: Type` 等）。</summary>
        CSharp,
    }
}
