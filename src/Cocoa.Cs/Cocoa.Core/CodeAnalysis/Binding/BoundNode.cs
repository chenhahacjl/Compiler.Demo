using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点
    /// </summary>
    internal abstract class BoundNode
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
