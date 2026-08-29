using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 方言编译对象（Y §6.7 A0 骨架：双 <c>Compilation</c> 子类之一，行为等价）。
    /// 对标 Roslyn <c>CSharpCompilation : Compilation</c>；后续 Phase（B2 C#Binder）
    /// 为其挂载 C# 专属成员/入口。
    /// </summary>
    public sealed class CSharpCompilation : Compilation
    {
        internal CSharpCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            : base(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees)
        {
        }
    }
}