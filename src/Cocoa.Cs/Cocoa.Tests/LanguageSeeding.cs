using Cocoa.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Cocoa.Tests
{
    /// <summary>
    /// M2 语言注册种子：模块装载即触达 CO/C# 语言实例（实例构造注册进
    /// <see cref="Language"/> 注册表），保证测试内 <c>ParseCs</c>/<c>SyntaxTree.Load(.cs)</c>
    /// 无需显式播种即可解析 C# 方言。
    /// </summary>
    internal static class LanguageSeeding
    {
        [ModuleInitializer]
        internal static void SeedLanguages()
        {
            _ = CocoaLanguage.Instance;
            _ = CSharpLanguage.Instance;
        }
    }
}