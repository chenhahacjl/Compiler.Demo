namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点类型
    /// </summary>
    internal enum BoundNodeKind
    {
        // Statement
        BlockStatement,
        NopStatement,
        VariableDeclaration,
        IfStatement,
        WhileStatement,
        DoWhileStatement,
        ForStatement,
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
IsExpression,
AsExpression,
FunctionValueExpression,   // 函数值：lambda 字面量 / 方法组（6e-M22 C4）
InvocationExpression,      // 函数值间接调用 f(x)（6e-M22 C4）
ByRefArgument,             // byref 实参 out x / ref a[i]（6e-M23 R3）
    }
}
