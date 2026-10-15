using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// 集合表达式 spread 元素（C-集合）：`[..a, 4]` 中的 `..a` 在运行时把数组 a 的内容
    /// 逐元素拷贝进新数组。作为 <see cref="BoundArrayCreationExpression.Initializers"/> 的元素出现：
    /// 发射端遇到它时从源数组按序拷贝（长度 = 源数组 Length），普通元素则写入新数组的递增下标。
    /// Type = 源数组类型（T[]）；不单独求值，由数组创建的发射循环在正确偏移处展开。
    /// </summary>
    public sealed class BoundArraySpreadExpression : BoundExpression
    {
        public BoundArraySpreadExpression(SyntaxNode syntax, BoundExpression array)
            : base(syntax)
        {
            Array = array;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ArraySpreadExpression;
        public override TypeSymbol Type => Array.Type;

        public BoundExpression Array { get; }
    }
}