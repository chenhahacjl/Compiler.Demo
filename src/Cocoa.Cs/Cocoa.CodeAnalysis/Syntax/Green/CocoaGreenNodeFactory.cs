using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// Cocoa 缁库啋绫诲瀷鍖栫孩鑺傜偣宸ュ巶锛圫-5 P2-4 闅忚縼璇█搴擄級锛氭寜 <see cref="GreenNode.RawKind"/> 娲惧彂鍒拌瑷€鑺傜偣绫汇€?
    /// </summary>
    internal sealed partial class CocoaGreenNodeFactory
    {
        private readonly GreenNode _green;

        public CocoaGreenNodeFactory(GreenNode green)
        {
            _green = green;
        }

        public SyntaxNode CreateTypedRed(SyntaxTree syntaxTree, int position)
        {
            if (_green is GreenToken token)
            {
                return token.ToRed(syntaxTree, position);
            }

            return _green.Kind switch
            {
                SyntaxKind.NameExpression => BuildNameExpression(syntaxTree, position),
                SyntaxKind.BinaryExpression => BuildBinaryExpression(syntaxTree, position),
                SyntaxKind.UnaryExpression => BuildUnaryExpression(syntaxTree, position),
                SyntaxKind.ParenthesizedExpression => BuildParenthesizedExpression(syntaxTree, position),
                SyntaxKind.LiteralExpression => BuildLiteralExpression(syntaxTree, position),
                SyntaxKind.ExpressionStatement => BuildExpressionStatement(syntaxTree, position),
                SyntaxKind.AssignmentExpression => BuildAssignmentExpression(syntaxTree, position),
                SyntaxKind.MemberAccessExpression => BuildMemberAccessExpression(syntaxTree, position),
                SyntaxKind.ReturnStatement => BuildReturnStatement(syntaxTree, position),
                SyntaxKind.WhileStatement => BuildWhileStatement(syntaxTree, position),
                SyntaxKind.BlockStatement => BuildBlockStatement(syntaxTree, position),
                SyntaxKind.IfStatement => BuildIfStatement(syntaxTree, position),
                SyntaxKind.ElseClause => BuildElseClause(syntaxTree, position),
                SyntaxKind.VariableDeclaration => BuildVariableDeclaration(syntaxTree, position),
                SyntaxKind.TypeClause => BuildTypeClause(syntaxTree, position),
                SyntaxKind.CallExpression => BuildCallExpression(syntaxTree, position),
                SyntaxKind.TupleExpression => BuildTupleExpression(syntaxTree, position),
                SyntaxKind.MemberCallExpression => BuildMemberCallExpression(syntaxTree, position),
                SyntaxKind.ObjectCreationExpression => BuildObjectCreationExpression(syntaxTree, position),
                SyntaxKind.WithExpression => BuildWithExpression(syntaxTree, position),
                SyntaxKind.ElementAccessExpression => BuildElementAccessExpression(syntaxTree, position),
                SyntaxKind.TypeArgumentList => BuildTypeArgumentList(syntaxTree, position),
                SyntaxKind.Parameter => BuildParameter(syntaxTree, position),
                SyntaxKind.FunctionDeclaration => BuildFunctionDeclaration(syntaxTree, position),
                SyntaxKind.CompilationUnit => BuildCompilationUnit(syntaxTree, position),
                SyntaxKind.BreakStatement => BuildKeywordStatement(syntaxTree, position),
                SyntaxKind.ContinueStatement => BuildKeywordStatement(syntaxTree, position),
                SyntaxKind.ThrowStatement => BuildThrowStatement(syntaxTree, position),
                SyntaxKind.DoWhileStatement => BuildDoWhileStatement(syntaxTree, position),
                SyntaxKind.ThisExpression => BuildKeywordExpression(syntaxTree, position),
                SyntaxKind.BaseExpression => BuildKeywordExpression(syntaxTree, position),
                SyntaxKind.CastExpression => BuildCastExpression(syntaxTree, position),
                SyntaxKind.AsExpression => BuildAsIsExpression(syntaxTree, position, isAs: true),
                SyntaxKind.IsExpression => BuildAsIsExpression(syntaxTree, position, isAs: false),
                SyntaxKind.PostfixIncrementExpression => BuildPostfixIncrementExpression(syntaxTree, position),
                SyntaxKind.ByRefArgument => BuildByRefArgumentExpression(syntaxTree, position),
                SyntaxKind.DeclarationExpression => BuildDeclarationExpression(syntaxTree, position),
                SyntaxKind.NamedArgument => BuildNamedArgument(syntaxTree, position),
                SyntaxKind.LocalFunctionDeclaration => BuildLocalFunctionDeclarationStatement(syntaxTree, position),
                SyntaxKind.EnumDeclaration => BuildEnumDeclaration(syntaxTree, position),
                SyntaxKind.EnumMember => BuildEnumMember(syntaxTree, position),
                SyntaxKind.GlobalStatement => BuildGlobalStatement(syntaxTree, position),
                SyntaxKind.ConditionalExpression => BuildConditionalExpression(syntaxTree, position),
                SyntaxKind.TypeParameterList => BuildTypeParameterList(syntaxTree, position),
                SyntaxKind.TypeParameter => BuildTypeParameter(syntaxTree, position),
                SyntaxKind.ClassFieldDeclaration => BuildClassFieldDeclaration(syntaxTree, position),
                SyntaxKind.ArrayTypeClause => BuildArrayTypeClause(syntaxTree, position),
                SyntaxKind.FunctionType => BuildFunctionType(syntaxTree, position),
                SyntaxKind.GenericTypeClause => BuildGenericTypeClause(syntaxTree, position),
                SyntaxKind.DelegateDeclaration => BuildDelegateDeclaration(syntaxTree, position),
                SyntaxKind.EventDeclaration => BuildEventDeclaration(syntaxTree, position),
                SyntaxKind.PropertyAccessor => BuildPropertyAccessor(syntaxTree, position),
                SyntaxKind.WhereClause => BuildWhereClause(syntaxTree, position),
                SyntaxKind.DefaultClause => BuildDefaultClause(syntaxTree, position),
                SyntaxKind.FinallyClause => BuildFinallyClause(syntaxTree, position),
                SyntaxKind.TryStatement => BuildTryStatement(syntaxTree, position),
                SyntaxKind.CatchClause => BuildCatchClause(syntaxTree, position),
                SyntaxKind.ForeachStatement => BuildForeachStatement(syntaxTree, position),
                SyntaxKind.ForStatement => BuildForStatement(syntaxTree, position),
                SyntaxKind.ForRangeStatement => BuildForRangeStatement(syntaxTree, position),
                SyntaxKind.ArrayCreationExpression => BuildArrayCreationExpression(syntaxTree, position),
                SyntaxKind.NamespaceDeclaration => BuildNamespaceDeclaration(syntaxTree, position),
                SyntaxKind.UsingDirective => BuildUsingDirective(syntaxTree, position),
                SyntaxKind.ClassDeclaration => BuildClassLikeDeclaration(syntaxTree, position, isInterface: false),
                SyntaxKind.InterfaceDeclaration => BuildClassLikeDeclaration(syntaxTree, position, isInterface: true),
                SyntaxKind.ConstructorDeclaration => BuildConstructorDeclaration(syntaxTree, position),
                SyntaxKind.PropertyDeclaration => BuildPropertyDeclaration(syntaxTree, position),
                SyntaxKind.CaseClause => BuildCaseClause(syntaxTree, position),
                SyntaxKind.SwitchStatement => BuildSwitchStatement(syntaxTree, position),
                SyntaxKind.LambdaExpression => BuildLambdaExpression(syntaxTree, position),
                SyntaxKind.InterpolatedStringExpression => BuildInterpolatedStringExpression(syntaxTree, position),
                SyntaxKind.InterpolatedStringText => BuildInterpolatedStringText(syntaxTree, position),
                SyntaxKind.Interpolation => BuildInterpolation(syntaxTree, position),
                SyntaxKind.ImportClause => BuildImportClause(syntaxTree, position),
                SyntaxKind.ExternMetadata => BuildExternMetadata(syntaxTree, position),
                SyntaxKind.ExternMetadataArgument => BuildExternMetadataArgument(syntaxTree, position),
                SyntaxKind.ImportBlock => BuildImportBlock(syntaxTree, position),
                SyntaxKind.Attribute => BuildAttribute(syntaxTree, position),
                _ => _green.CreateRed(syntaxTree, position),
            };
        }

    }
}
