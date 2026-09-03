using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点（A-4 公开化：供 SemanticModel.GetOperation 等对外暴露绑定树；具体节点类仍 internal）。
    /// </summary>
    public abstract class BoundNode
    {
        protected BoundNode(SyntaxNode syntax)
        {
            Syntax = syntax;
        }

        public abstract BoundNodeKind Kind { get; }

        public SyntaxNode Syntax { get; }

        public override string ToString()
        {
            using (var writer = new StringWriter())
            {
                this.WriteTo(writer);

                return writer.ToString();
            }
        }
    }
}
