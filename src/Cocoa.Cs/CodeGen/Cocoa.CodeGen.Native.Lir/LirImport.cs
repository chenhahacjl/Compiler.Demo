namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>导入规格：DLL 名 + 函数名（DLL 导出名，已含 entry 别名）+ x86 调用约定（cdecl 调用方清理）。x64 约定统一，Cdecl 忽略。</summary>
    public readonly record struct LirImport(string DllName, string Name, bool Cdecl)
    {
        public override string ToString() => DllName + "!" + Name;
    }
}