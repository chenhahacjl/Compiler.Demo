using Cocoa.CodeAnalysis;

namespace Cocoa.CocCompiler
{
    /// <summary>
    /// `coc` — Cocoa 语言薄编译器入口（M3，对标 Roslyn csc 的独立编译器 exe，产出 DLL + apphost）。
    /// 强制以 Cocoa 语言解析全部源文件（忽略扩展名），其余参数语义与 `cocoa <sources>` 一致。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            _ = CocoaLanguage.Instance;
            return Cocoa.Compiler.Program.CompileForLanguage(args, Language.Cocoa);
        }
    }
}