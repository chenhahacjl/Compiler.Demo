using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>switch 鑺傚熀绫伙細case 瀛愬彞鎴?default 瀛愬彞銆?/summary>
    public abstract partial class SwitchSectionSyntax : CocoaSyntaxNode
    {
        private protected SwitchSectionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

