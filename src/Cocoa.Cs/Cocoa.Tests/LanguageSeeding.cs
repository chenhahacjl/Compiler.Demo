using Cocoa.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Cocoa.Tests
{
    /// <summary>
    /// M2 语言注册种子：模块装载即触达 CO 语言实例（实例构造注册进
    /// <see cref="Language"/> 注册表），保证测试内解析默认走 CO 方言。去 C# 方言（2026-09-13）后仅 CO。
    /// </summary>
    internal static class LanguageSeeding
    {
        [ModuleInitializer]
        internal static void SeedLanguages()
        {
            _ = Language.Cocoa;
        }
    }
}
