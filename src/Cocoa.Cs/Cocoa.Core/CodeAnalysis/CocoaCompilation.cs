using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// CO 侧语言编译对象（Y §6.7 A0 骨架：双 <c>Compilation</c> 子类之一，行为等价）。
    /// 对标 Roslyn <c>CSharpCompilation : Compilation</c>；本类为 CO 宿主语言编译，
    /// 后续 Phase（A3 CO 显式化）为其挂载 CO 专属成员/入口。
    /// </summary>
    public sealed class CocoaCompilation : Compilation
    {
        internal CocoaCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            : base(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees)
        {
        }
    }
}