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
    }
}
