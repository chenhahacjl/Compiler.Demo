namespace Compiler.Demo.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点
    /// </summary>
    internal abstract class BoundNode
    {
        public abstract BoundNodeKind Kind { get; }
    }
}
