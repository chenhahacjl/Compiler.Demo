namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点类型
    /// </summary>
    public enum BoundNodeKind
    {
        // Statement
        BlockStatement,
        NopStatement,
        VariableDeclaration,
        IfStatement,
        WhileStatement,
        DoWhileStatement,
        ForRangeStatement,
        LabelStatement,
        GotoStatement,
        ConditionalGotoStatement,
        ReturnStatement,
        ExpressionStatement,
        SequencePointStatement,
        ThrowStatement,
        TryStatement,

        // Expression
ErrorExpression,
LiteralExpression,
VariableExpression,
AssignmentExpression,
CompoundAssignmentExpression,
UnaryExpression,
BinaryExpression,
ConditionalExpression,
CallExpression,
ConversionExpression,
ArrayCreationExpression,
ObjectCreationExpression,
ThisExpression,
BaseExpression,
StaticTypeExpression,
ElementAccessExpression,
ElementAssignmentExpression,
MemberAccessExpression,
MemberCallExpression,
MemberAssignmentExpression,
        ConstructorChainExpression,
        FormatExpression,
        InterpolatedStringExpression,   // 插值字符串高 Bound（Y A2-F1：绑定后、规范化前的临时形态）
        IsExpression,
AsExpression,
        FunctionValueExpression,   // 函数值：lambda 字面量 / 方法组（6e-M22 C4）
        InvocationExpression,      // 函数值间接调用 f(x)（6e-M22 C4）
        ByRefArgument,             // byref 实参 out x / ref a[i]（6e-M23 R3）
        ConditionalAccessExpression, // expr?.Member 空条件访问
        DeclarationPattern,        // 声明模式：expr is int n
        RelationalPattern,         // 关系模式：expr is > 0
        LogicalPattern,            // 逻辑模式：expr is > 0 and < 10
        PropertyPattern,           // 属性模式：expr is { Length: > 0 }
    }
}
