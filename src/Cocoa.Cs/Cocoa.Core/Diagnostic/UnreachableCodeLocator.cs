using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 不可达代码位置解析（P1-3 钩子预备：自 <see cref="DiagnosticBag.ReportUnreachableCode(SyntaxNode)"/> 抽取共享实现，
    /// 语言库 <see cref="Language.GetUnreachableCodeLocation"/> 钩子在 P1 委托本器，P2-5 切语言节点后由语言库自持）。
    /// </summary>
    internal static class UnreachableCodeLocator
    {
        /// <summary>解析不可达代码的告警位置；空块等无可报告场景返回 null。</summary>
        public static TextLocation? GetLocation(SyntaxNode node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.BlockStatement:
                {
                    var firstStatement = ((BlockStatementSyntax)node).Statements.FirstOrDefault();

                    // Report just for non empty blocks.
                    if (firstStatement != null)
                    {
                        return GetLocation(firstStatement);
                    }

                    return null;
                }
                case SyntaxKind.VariableDeclaration:
                {
                    var variableDeclaration = (VariableDeclarationSyntax)node;
                    return variableDeclaration.Keyword?.Location ?? variableDeclaration.Location;
                }
                case SyntaxKind.IfStatement:
                    return ((IfStatementSyntax)node).Keyword.Location;
                case SyntaxKind.WhileStatement:
                    return ((WhileStatementSyntax)node).Keyword.Location;
                case SyntaxKind.DoWhileStatement:
                    return ((DoWhileStatementSyntax)node).DoKeyword.Location;
                case SyntaxKind.ForStatement:
                    return ((ForStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ForeachStatement:
                    return ((ForeachStatementSyntax)node).Keyword.Location;
                case SyntaxKind.SwitchStatement:
                    return ((SwitchStatementSyntax)node).Keyword.Location;
                case SyntaxKind.BreakStatement:
                    return ((BreakStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ContinueStatement:
                    return ((ContinueStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ReturnStatement:
                    return ((ReturnStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ExpressionStatement:
                    return GetLocation(((ExpressionStatementSyntax)node).Expression);
                case SyntaxKind.CallExpression:
                    return ((CallExpressionSyntax)node).Identifier.Location;
                case SyntaxKind.MemberCallExpression:
                    return ((MemberCallExpressionSyntax)node).IdentifierToken.Location;
                default:
                    throw new Exception($"Unexpected syntax {node.Kind}");
            }
        }
    }
}
