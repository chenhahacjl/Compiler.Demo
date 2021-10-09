namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点类型
    /// </summary>
    internal enum BoundNodeKind
    {
        // Statement
        BlockStatement,
        VariableDeclaration,
        IfStatement,
        WhileStatement,
        ForStatement,
        ExpressionStatement,

        // Expression
        LiteralExpression,
        VariableExpression,
        AssignmentExpression,
        UnaryExpression,
        BinaryExpression,
    }
}
