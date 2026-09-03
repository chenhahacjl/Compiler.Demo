using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// C# 绿→类型化红节点工厂（S-5 P2-4 随迁语言库）：按 <see cref="GreenNode.RawKind"/> 派发到语言节点类。
    /// </summary>
    internal sealed class CSharpGreenNodeFactory
    {
        private readonly GreenNode _green;

        public CSharpGreenNodeFactory(GreenNode green)
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
                SyntaxKind.MemberCallExpression => BuildMemberCallExpression(syntaxTree, position),
                SyntaxKind.ObjectCreationExpression => BuildObjectCreationExpression(syntaxTree, position),
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
                SyntaxKind.EnumDeclaration => BuildEnumDeclaration(syntaxTree, position),
                SyntaxKind.EnumMember => BuildEnumMember(syntaxTree, position),
                SyntaxKind.GlobalStatement => BuildGlobalStatement(syntaxTree, position),
                SyntaxKind.ConditionalExpression => BuildConditionalExpression(syntaxTree, position),
                SyntaxKind.TypeParameterList => BuildTypeParameterList(syntaxTree, position),
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
                SyntaxKind.ArrayCreationExpression => BuildArrayCreationExpression(syntaxTree, position),
                SyntaxKind.NamespaceDeclaration => BuildNamespaceDeclaration(syntaxTree, position),
                SyntaxKind.UsingDirective => BuildUsingDirective(syntaxTree, position),
                SyntaxKind.ClassDeclaration => BuildClassLikeDeclaration(syntaxTree, position, isInterface: false),
                SyntaxKind.InterfaceDeclaration => BuildClassLikeDeclaration(syntaxTree, position, isInterface: true),
                SyntaxKind.ForStatement => BuildForStatement(syntaxTree, position),
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
                _ => _green.CreateRed(syntaxTree, position),
            };
        }

        private SyntaxNode BuildNameExpression(SyntaxTree syntaxTree, int position)
        {
            var identifier = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new NameExpressionSyntax(syntaxTree, identifier);
        }

        private SyntaxNode BuildBinaryExpression(SyntaxTree syntaxTree, int position)
        {
            var left = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operatorPosition = position + _green.GetSlot(0)!.Width;
            var operatorToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, operatorPosition);
            var rightPosition = operatorPosition + _green.GetSlot(1)!.Width;
            var right = (ExpressionSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, rightPosition);
            return new BinaryExpressionSyntax(syntaxTree, left, operatorToken, right);
        }

        private SyntaxNode BuildLiteralExpression(SyntaxTree syntaxTree, int position)
        {
            var literalToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new LiteralExpressionSyntax(syntaxTree, literalToken);
        }

        private SyntaxNode BuildUnaryExpression(SyntaxTree syntaxTree, int position)
        {
            var operatorToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operandPosition = position + _green.GetSlot(0)!.Width;
            var operand = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, operandPosition);
            return new UnaryExpressionSyntax(syntaxTree, operatorToken, operand);
        }

        private SyntaxNode BuildParenthesizedExpression(SyntaxTree syntaxTree, int position)
        {
            var openParenthesis = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + _green.GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            var closePosition = expressionPosition + _green.GetSlot(1)!.Width;
            var closeParenthesis = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, closePosition);
            return new ParenthesizedExpressionSyntax(syntaxTree, openParenthesis, expression, closeParenthesis);
        }

        private SyntaxNode BuildExpressionStatement(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new ExpressionStatementSyntax(syntaxTree, expression);
        }

        private SyntaxNode BuildAssignmentExpression(SyntaxTree syntaxTree, int position)
        {
            var target = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var tokenPosition = position + _green.GetSlot(0)!.Width;
            var assignmentToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, tokenPosition);
            var expressionPosition = tokenPosition + _green.GetSlot(1)!.Width;
            var expression = (ExpressionSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new AssignmentExpressionSyntax(syntaxTree, target, assignmentToken, expression);
        }

        private SyntaxNode BuildMemberAccessExpression(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var dotPosition = position + _green.GetSlot(0)!.Width;
            var dotToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, dotPosition);
            var identifierPosition = dotPosition + _green.GetSlot(1)!.Width;
            var identifierToken = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, identifierPosition);
            return new MemberAccessExpressionSyntax(syntaxTree, expression, dotToken, identifierToken);
        }

        private SyntaxNode BuildReturnStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            ExpressionSyntax? expression = null;
            if (_green.SlotCount > 1)
            {
                var expressionPosition = position + _green.GetSlot(0)!.Width;
                expression = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            }

            return new ReturnStatementSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildWhileStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var conditionPosition = position + _green.GetSlot(0)!.Width;
            var condition = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, conditionPosition);
            var bodyPosition = conditionPosition + _green.GetSlot(1)!.Width;
            var body = (StatementSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new WhileStatementSyntax(syntaxTree, keyword, condition, body);
        }

        private SyntaxNode BuildBlockStatement(SyntaxTree syntaxTree, int position)
        {
            var openBrace = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + _green.GetSlot(0)!.Width;
            var statements = BuildSlotArray<StatementSyntax>(syntaxTree, bodyPosition, 1, _green.SlotCount - 2);
            var closePosition = bodyPosition;
            for (var i = 1; i < _green.SlotCount - 1; i++)
            {
                closePosition += _green.GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new BlockStatementSyntax(syntaxTree, openBrace, statements, closeBrace);
        }

        private SyntaxNode BuildIfStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var conditionPosition = position + _green.GetSlot(0)!.Width;
            var condition = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, conditionPosition);
            var thenPosition = conditionPosition + _green.GetSlot(1)!.Width;
            var thenStatement = (StatementSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, thenPosition);
            ElseClauseSyntax? elseClause = null;
            if (_green.SlotCount > 3)
            {
                var elsePosition = thenPosition + _green.GetSlot(2)!.Width;
                elseClause = (ElseClauseSyntax)_green.GetSlot(3)!.CreateTypedRed(syntaxTree, elsePosition);
            }

            return new IfStatementSyntax(syntaxTree, keyword, condition, thenStatement, elseClause);
        }

        private SyntaxNode BuildElseClause(SyntaxTree syntaxTree, int position)
        {
            var elseKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var statementPosition = position + _green.GetSlot(0)!.Width;
            var elseStatement = (StatementSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, statementPosition);
            return new ElseClauseSyntax(syntaxTree, elseKeyword, elseStatement);
        }

        private SyntaxNode BuildVariableDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? keyword = null;
            if (_green.GetSlot(slot)!.Kind is SyntaxKind.VarKeyword or SyntaxKind.LetKeyword)
            {
                keyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            TypeClauseSyntax? typeClause = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeClause)
            {
                typeClause = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? equalsToken = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            ExpressionSyntax? initializer = null;
            if (slot < _green.SlotCount)
            {
                initializer = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new VariableDeclarationSyntax(syntaxTree, keyword, identifier, typeClause, equalsToken, initializer);
        }

        private SyntaxNode BuildTypeClause(SyntaxTree syntaxTree, int position)
        {
            if (_green.SlotCount == 2)
            {
                var colonToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
                var identifierPosition = position + _green.GetSlot(0)!.Width;
                var identifier = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
                return new TypeClauseSyntax(syntaxTree, colonToken, identifier);
            }

            var typeIdentifier = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new TypeClauseSyntax(syntaxTree, null, typeIdentifier);
        }

        private SyntaxNode BuildCallExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            TypeArgumentListSyntax? typeArguments = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeArgumentList)
            {
                typeArguments = (TypeArgumentListSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            // 瀹炲弬妲斤細openParen 涓?closeParen锛堟湯妲斤級涔嬮棿锛宯ode/sep 浜ゆ浛锛岀洿鏋?SeparatedSyntaxList
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            var closeParenthesis = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            var arguments = new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
            return new CallExpressionSyntax(syntaxTree, identifier, typeArguments, openParenthesis, arguments, closeParenthesis);
        }

        private SyntaxNode BuildTypeArgumentList(SyntaxTree syntaxTree, int position)
        {
            var lessThanToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var argumentsPosition = position + _green.GetSlot(0)!.Width;
            var arguments = BuildSlotArray<TypeClauseSyntax>(syntaxTree, argumentsPosition, 1, _green.SlotCount - 2);
            var greaterPosition = argumentsPosition;
            for (var i = 1; i < _green.SlotCount - 1; i++)
            {
                greaterPosition += _green.GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new TypeArgumentListSyntax(syntaxTree, lessThanToken, arguments, greaterThanToken);
        }

        private SyntaxNode BuildMemberCallExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var expression = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var dotToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var callTail = BuildCallTail(syntaxTree, position, slot);
            return new MemberCallExpressionSyntax(syntaxTree, expression, dotToken, identifier, callTail.TypeArguments, callTail.OpenParenthesis, callTail.Arguments, callTail.CloseParenthesis);
        }

        private SyntaxNode BuildObjectCreationExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var newKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var callTail = BuildCallTail(syntaxTree, position, slot);
            return new ObjectCreationExpressionSyntax(syntaxTree, newKeyword, identifier, callTail.TypeArguments, callTail.OpenParenthesis, callTail.Arguments, callTail.CloseParenthesis);
        }

        private SyntaxNode BuildElementAccessExpression(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var openPosition = position + _green.GetSlot(0)!.Width;
            var openBracket = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, openPosition);
            var indexPosition = openPosition + _green.GetSlot(1)!.Width;
            var index = (ExpressionSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, indexPosition);
            var closePosition = indexPosition + _green.GetSlot(2)!.Width;
            var closeBracket = (SyntaxToken)_green.GetSlot(3)!.CreateTypedRed(syntaxTree, closePosition);
            return new ElementAccessExpressionSyntax(syntaxTree, expression, openBracket, index, closeBracket);
        }

        /// <summary>璋冪敤灏炬锛坱ypeArgs? + openParen + 瀹炲弬 SeparatedSyntaxList + closeParen锛夆€斺€擟all/MemberCall/ObjectCreation 鍏辩敤銆?/summary>
        private (TypeArgumentListSyntax? TypeArguments, SyntaxToken OpenParenthesis, SeparatedSyntaxList<ExpressionSyntax> Arguments, SyntaxToken CloseParenthesis) BuildCallTail(
            SyntaxTree syntaxTree, int position, int slot)
        {
            TypeArgumentListSyntax? typeArguments = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeArgumentList)
            {
                typeArguments = (TypeArgumentListSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            var closeParenthesis = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            return (typeArguments, openParenthesis, new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable()), closeParenthesis);
        }

        private SyntaxNode BuildParameter(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? modifier = null;
            if (slot < _green.SlotCount && (IsModifierToken(_green.GetSlot(slot)!.Kind) || IsByRefModifierToken(_green.GetSlot(slot)!.Kind)))
            {
                modifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken identifier;
            TypeClauseSyntax type;
            if (slot < _green.SlotCount && IsTypeLikeSlot(_green.GetSlot(slot)!.Kind))
            {
                // 绫诲瀷鍓嶇疆锛?cs锛歚int x`锛?
                type = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }
            else
            {
                // 鍚嶇О鍓嶇疆锛?co锛歚x: i32`锛?
                identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                type = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new ParameterSyntax(syntaxTree, modifier, identifier, type);
        }

        private SyntaxNode BuildFunctionDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? functionKeyword = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.FunctionKeyword)
            {
                functionKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            TypeParameterListSyntax? typeParameters = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeParameterList)
            {
                typeParameters = (TypeParameterListSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            TypeClauseSyntax? type = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeClause)
            {
                type = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            BlockStatementSyntax? body = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.BlockStatement)
            {
                body = (BlockStatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            return new FunctionDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), functionKeyword, identifier, typeParameters, openParenthesis, parameters, closeParenthesis, type, body);
        }

        private SyntaxNode BuildCompilationUnit(SyntaxTree syntaxTree, int position)
        {
            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, 0, _green.SlotCount - 2);
            var endOfFilePosition = position;
            for (var i = 0; i < _green.SlotCount - 1; i++)
            {
                endOfFilePosition += _green.GetSlot(i)!.Width;
            }

            var endOfFile = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, endOfFilePosition);
            return new CompilationUnitSyntax(syntaxTree, members, endOfFile);
        }

        private static bool IsModifierToken(SyntaxKind kind) => kind is
            SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ProtectedKeyword
            or SyntaxKind.StaticKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.SealedKeyword
            or SyntaxKind.ExternKeyword or SyntaxKind.ReadonlyKeyword;

        private static bool IsByRefModifierToken(SyntaxKind kind) => kind is
            SyntaxKind.RefKeyword or SyntaxKind.OutKeyword;

        private static bool IsTypeLikeSlot(SyntaxKind kind) => kind is
            SyntaxKind.TypeClause or SyntaxKind.ArrayTypeClause or SyntaxKind.GenericTypeClause or SyntaxKind.FunctionType;

        private SyntaxNode BuildKeywordStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return _green.Kind == SyntaxKind.BreakStatement
                ? new BreakStatementSyntax(syntaxTree, keyword)
                : new ContinueStatementSyntax(syntaxTree, keyword);
        }

        private SyntaxNode BuildKeywordExpression(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return _green.Kind == SyntaxKind.ThisExpression
                ? new ThisExpressionSyntax(syntaxTree, keyword)
                : new BaseExpressionSyntax(syntaxTree, keyword);
        }

        private SyntaxNode BuildThrowStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + _green.GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new ThrowStatementSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildDoWhileStatement(SyntaxTree syntaxTree, int position)
        {
            var doKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + _green.GetSlot(0)!.Width;
            var body = (StatementSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, bodyPosition);
            var whilePosition = bodyPosition + _green.GetSlot(1)!.Width;
            var whileKeyword = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, whilePosition);
            var conditionPosition = whilePosition + _green.GetSlot(2)!.Width;
            var condition = (ExpressionSyntax)_green.GetSlot(3)!.CreateTypedRed(syntaxTree, conditionPosition);
            return new DoWhileStatementSyntax(syntaxTree, doKeyword, body, whileKeyword, condition);
        }

        private SyntaxNode BuildCastExpression(SyntaxTree syntaxTree, int position)
        {
            var openParenthesis = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var typePosition = position + _green.GetSlot(0)!.Width;
            var typeName = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, typePosition);
            var closePosition = typePosition + _green.GetSlot(1)!.Width;
            var closeParenthesis = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, closePosition);
            var expressionPosition = closePosition + _green.GetSlot(2)!.Width;
            var expression = (ExpressionSyntax)_green.GetSlot(3)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new CastExpressionSyntax(syntaxTree, openParenthesis, typeName, closeParenthesis, expression);
        }

        private SyntaxNode BuildAsIsExpression(SyntaxTree syntaxTree, int position, bool isAs)
        {
            var expression = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var keywordPosition = position + _green.GetSlot(0)!.Width;
            var keyword = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, keywordPosition);
            var typePosition = keywordPosition + _green.GetSlot(1)!.Width;
            var typeName = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, typePosition);
            return isAs
                ? new AsExpressionSyntax(syntaxTree, expression, keyword, typeName)
                : new IsExpressionSyntax(syntaxTree, expression, keyword, typeName);
        }

        private SyntaxNode BuildPostfixIncrementExpression(SyntaxTree syntaxTree, int position)
        {
            var operand = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operatorPosition = position + _green.GetSlot(0)!.Width;
            var operatorToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, operatorPosition);
            return new PostfixIncrementExpressionSyntax(syntaxTree, operand, operatorToken);
        }

        private SyntaxNode BuildByRefArgumentExpression(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + _green.GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new ByRefArgumentExpressionSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildEnumDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var enumKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            var members = new SeparatedSyntaxList<EnumMemberSyntax>(nodesAndSeparators.ToImmutable());
            return new EnumDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), enumKeyword, identifier, openBrace, members, closeBrace);
        }

        private SyntaxNode BuildEnumMember(SyntaxTree syntaxTree, int position)
        {
            var identifier = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? value = null;
            if (_green.SlotCount > 1)
            {
                var equalsPosition = position + _green.GetSlot(0)!.Width;
                equalsToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, equalsPosition);
                value = (ExpressionSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, equalsPosition + _green.GetSlot(1)!.Width);
            }

            return new EnumMemberSyntax(syntaxTree, identifier, equalsToken, value);
        }

        private SyntaxNode BuildGlobalStatement(SyntaxTree syntaxTree, int position)
        {
            var statement = (StatementSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new GlobalStatementSyntax(syntaxTree, statement);
        }

        private SyntaxNode BuildConditionalExpression(SyntaxTree syntaxTree, int position)
        {
            var condition = (ExpressionSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var questionPosition = position + _green.GetSlot(0)!.Width;
            var questionToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, questionPosition);
            var whenTruePosition = questionPosition + _green.GetSlot(1)!.Width;
            var whenTrue = (ExpressionSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, whenTruePosition);
            var colonPosition = whenTruePosition + _green.GetSlot(2)!.Width;
            var colonToken = (SyntaxToken)_green.GetSlot(3)!.CreateTypedRed(syntaxTree, colonPosition);
            var whenFalsePosition = colonPosition + _green.GetSlot(3)!.Width;
            var whenFalse = (ExpressionSyntax)_green.GetSlot(4)!.CreateTypedRed(syntaxTree, whenFalsePosition);
            return new ConditionalExpressionSyntax(syntaxTree, condition, questionToken, whenTrue, colonToken, whenFalse);
        }

        private SyntaxNode BuildTypeParameterList(SyntaxTree syntaxTree, int position)
        {
            var lessThanToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var parametersPosition = position + _green.GetSlot(0)!.Width;
            var parameters = BuildSlotArray<SyntaxToken>(syntaxTree, parametersPosition, 1, _green.SlotCount - 2);
            var greaterPosition = parametersPosition;
            for (var i = 1; i < _green.SlotCount - 1; i++)
            {
                greaterPosition += _green.GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new TypeParameterListSyntax(syntaxTree, lessThanToken, parameters, greaterThanToken);
        }

        private SyntaxNode BuildClassFieldDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var type = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                if (slot < _green.SlotCount)
                {
                    initializer = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                }
            }

            return new ClassFieldDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), identifier, type, equalsToken, initializer);
        }

        private SyntaxNode BuildArrayTypeClause(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? colonToken = null;
            if (_green.GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                colonToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            // 鍩虹被 TypeClause.Identifier 妲斤紙= elementType.Identifier锛夌洿鎺ヨ烦杩?
            var elementPosition = position + _green.GetSlot(slot)!.Width;
            slot++;
            var elementType = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, elementPosition);
            var openPosition = elementPosition + _green.GetSlot(slot)!.Width;
            slot++;
            var openBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, openPosition);
            var closePosition = openPosition + _green.GetSlot(slot)!.Width;
            slot++;
            var closeBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, closePosition);
            return new ArrayTypeClauseSyntax(syntaxTree, colonToken, elementType, openBracket, closeBracket);
        }

        private SyntaxNode BuildFunctionType(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var parameterTypesBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parameterTypesBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var arrowToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var returnType = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var parameterTypes = new SeparatedSyntaxList<TypeClauseSyntax>(parameterTypesBuilder.ToImmutable());
            return new FunctionTypeSyntax(syntaxTree, openParenthesis, parameterTypes, closeParenthesis, arrowToken, returnType);
        }

        private SyntaxNode BuildGenericTypeClause(SyntaxTree syntaxTree, int position)
        {
            SyntaxToken? colonToken = null;
            var slot = 0;
            if (_green.GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                colonToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var lessThanToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var typeArguments = BuildSlotArray<TypeClauseSyntax>(syntaxTree, position, slot, _green.SlotCount - 2);
            var greaterPosition = position;
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                greaterPosition += _green.GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new GenericTypeClauseSyntax(syntaxTree, colonToken, identifier, lessThanToken, typeArguments, greaterThanToken);
        }

        private SyntaxNode BuildDelegateDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var delegateKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            // 婧愬簭妲藉竷灞€锛堜笌 DelegateDeclarationSyntax.ToGreen 涓€鑷达級锛?
            // .co锛歞elegate 鍚?( 鍙傛暟 ) [: 杩斿洖绫诲瀷]锛?cs锛歞elegate 杩斿洖绫诲瀷 鍚?( 鍙傛暟 ) [;]
            // 鍒ゅ埆锛歚.cs` 鍓嶇疆杩斿洖绫诲瀷妲戒负绫诲瀷鏃忥紱`.co` 鎭掍负鏍囪瘑绗?
            var isCoForm = !IsTypeLikeSlot(_green.GetSlot(slot)!.Kind);
            TypeClauseSyntax? returnType = null;
            SyntaxToken identifier;

            if (isCoForm)
            {
                identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }
            else
            {
                returnType = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openParenToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeParenToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            if (isCoForm && slot < _green.SlotCount && IsTypeLikeSlot(_green.GetSlot(slot)!.Kind))
            {
                returnType = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? semicolonToken = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolonToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            return new DelegateDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), delegateKeyword, returnType, identifier, openParenToken, parameters, closeParenToken, semicolonToken);
        }

        private SyntaxNode BuildEventDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var eventKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var handlerType = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new EventDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), eventKeyword, identifier, handlerType);
        }

        private SyntaxNode BuildPropertyAccessor(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var keyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            BlockStatementSyntax? body = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.BlockStatement)
            {
                body = (BlockStatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? semicolonToken = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolonToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new PropertyAccessorSyntax(syntaxTree, modifiers.ToImmutable(), keyword, body, semicolonToken);
        }

        private SyntaxNode BuildWhereClause(SyntaxTree syntaxTree, int position)
        {
            var whereKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var identifierPosition = position + _green.GetSlot(0)!.Width;
            var identifier = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
            var colonPosition = identifierPosition + _green.GetSlot(1)!.Width;
            var colonToken = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, colonPosition);
            var constraintsPosition = colonPosition + _green.GetSlot(2)!.Width;
            var constraintTypes = BuildSlotArray<TypeClauseSyntax>(syntaxTree, constraintsPosition, 3, _green.SlotCount - 1);
            return new WhereClauseSyntax(syntaxTree, whereKeyword, identifier, colonToken, constraintTypes);
        }

        private SyntaxNode BuildDefaultClause(SyntaxTree syntaxTree, int position)
        {
            var defaultKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var colonPosition = position + _green.GetSlot(0)!.Width;
            var colonToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, colonPosition);
            var bodyPosition = colonPosition + _green.GetSlot(1)!.Width;
            var body = (StatementSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new DefaultClauseSyntax(syntaxTree, defaultKeyword, colonToken, body);
        }

        private SyntaxNode BuildFinallyClause(SyntaxTree syntaxTree, int position)
        {
            var finallyKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + _green.GetSlot(0)!.Width;
            var body = (BlockStatementSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new FinallyClauseSyntax(syntaxTree, finallyKeyword, body);
        }

        private SyntaxNode BuildTryStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var blockPosition = position + _green.GetSlot(0)!.Width;
            var tryBlock = (BlockStatementSyntax)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, blockPosition);
            position += _green.GetSlot(1)!.Width;

            var catches = ImmutableArray.CreateBuilder<CatchClauseSyntax>();
            var slot = 2;
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CatchClause)
            {
                catches.Add((CatchClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            FinallyClauseSyntax? finallyClause = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.FinallyClause)
            {
                finallyClause = (FinallyClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new TryStatementSyntax(syntaxTree, keyword, tryBlock, catches.ToImmutable(), finallyClause);
        }

        private SyntaxNode BuildCatchClause(SyntaxTree syntaxTree, int position)
        {
            var catchKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var identifierPosition = position + _green.GetSlot(0)!.Width;
            var identifier = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
            var typePosition = identifierPosition + _green.GetSlot(1)!.Width;
            var type = (TypeClauseSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, typePosition);
            var bodyPosition = typePosition + _green.GetSlot(2)!.Width;
            var body = (BlockStatementSyntax)_green.GetSlot(3)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new CatchClauseSyntax(syntaxTree, catchKeyword, identifier, type, body);
        }

        private SyntaxNode BuildForeachStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? varKeyword = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.VarKeyword)
            {
                varKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var inKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var collection = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? closeParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var body = (StatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new ForeachStatementSyntax(syntaxTree, keyword, openParenthesis, varKeyword, identifier, inKeyword, collection, closeParenthesis, body);
        }

        private SyntaxNode BuildArrayCreationExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var newKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var openBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            ExpressionSyntax? size = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseBracketToken)
            {
                size = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? openBrace = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenBraceToken)
            {
                openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var elementsBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseBraceToken)
            {
                elementsBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? closeBrace = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseBraceToken)
            {
                closeBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            var elements = new SeparatedSyntaxList<ExpressionSyntax>(elementsBuilder.ToImmutable());
            return new ArrayCreationExpressionSyntax(syntaxTree, newKeyword, identifier, openBracket, size, closeBracket, openBrace, elements, closeBrace);
        }

        private SyntaxNode BuildNamespaceDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var namespaceKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.OpenBraceToken)
            {
                nameTokens.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, slot, _green.SlotCount - 2);
            var closePosition = position;
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                closePosition += _green.GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new NamespaceDeclarationSyntax(syntaxTree, namespaceKeyword, nameTokens.ToImmutable(), openBrace, members, closeBrace);
        }

        private SyntaxNode BuildUsingDirective(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var usingKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? staticKeyword = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.StaticKeyword)
            {
                staticKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            // 鍒悕锛歚using Alias = Foo.Bar` 鈫?aliasToken + EqualsToken 鍓嶇紑
            SyntaxToken? aliasToken = null;
            SyntaxToken? equalsToken = null;
            if (slot + 1 < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.IdentifierToken && _green.GetSlot(slot + 1)!.Kind == SyntaxKind.EqualsToken)
            {
                aliasToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                equalsToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            for (var i = slot; i < _green.SlotCount; i++)
            {
                nameTokens.Add((SyntaxToken)_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            return new UsingDirectiveSyntax(syntaxTree, usingKeyword, staticKeyword, aliasToken, equalsToken, nameTokens.ToImmutable());
        }

        private static bool IsBaseTypeSlot(SyntaxKind kind) => kind is
            SyntaxKind.TypeClause or SyntaxKind.ArrayTypeClause or SyntaxKind.GenericTypeClause;

        private SyntaxNode BuildClassLikeDeclaration(SyntaxTree syntaxTree, int position, bool isInterface)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var keyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            TypeParameterListSyntax? typeParameters = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeParameterList)
            {
                typeParameters = (TypeParameterListSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
            while (slot < _green.SlotCount && IsBaseTypeSlot(_green.GetSlot(slot)!.Kind))
            {
                baseTypes.Add((TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var whereClauses = ImmutableArray.CreateBuilder<WhereClauseSyntax>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.WhereClause)
            {
                whereClauses.Add((WhereClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, slot, _green.SlotCount - 2);
            var closePosition = position;
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                closePosition += _green.GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return isInterface
                ? new InterfaceDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), keyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses.ToImmutable(), openBrace, members, closeBrace)
                : new ClassDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), keyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses.ToImmutable(), openBrace, members, closeBrace);
        }

        private SyntaxNode BuildForStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? openParen = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParen = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            VariableDeclarationSyntax? initDeclaration = null;
            var initializerNodes = ImmutableArray.CreateBuilder<SyntaxNode>();
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.VariableDeclaration)
            {
                initDeclaration = (VariableDeclarationSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }
            else
            {
                while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.SemicolonToken)
                {
                    initializerNodes.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }
            }

            var initializers = new SeparatedSyntaxList<ExpressionSyntax>(initializerNodes.ToImmutable());

            SyntaxToken? semicolon1 = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolon1 = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            ExpressionSyntax? condition = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.SemicolonToken)
            {
                condition = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? semicolon2 = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolon2 = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var incrementorNodes = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                incrementorNodes.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var incrementors = new SeparatedSyntaxList<ExpressionSyntax>(incrementorNodes.ToImmutable());

            SyntaxToken? closeParen = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParen = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var body = (StatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new ForStatementSyntax(syntaxTree, keyword, openParen, initDeclaration, initializers, semicolon1, condition, semicolon2, incrementors, closeParen, body);
        }

        private SyntaxNode BuildConstructorDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? constructorKeyword = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.ConstructorKeyword)
            {
                constructorKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                position += _green.GetSlot(slot)!.Width;
                slot++;
                initializerKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                position += _green.GetSlot(slot)!.Width; // 跳过 initializer openParen
                slot++;
                var initArgsBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
                while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    initArgsBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }

                position += _green.GetSlot(slot)!.Width; // 跳过 initializer closeParen
                slot++;
                initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(initArgsBuilder.ToImmutable());
            }

            var body = (BlockStatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            return new ConstructorDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), constructorKeyword, openParenthesis, parameters, closeParenthesis, initializerKeyword, initializerArguments, body);
        }

        private SyntaxNode BuildPropertyDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && IsModifierToken(_green.GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? propertyKeyword = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.PropertyKeyword)
            {
                propertyKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var type = (TypeClauseSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            PropertyAccessorSyntax? getter = null;
            PropertyAccessorSyntax? setter = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.PropertyAccessor)
            {
                getter = (PropertyAccessorSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.PropertyAccessor)
            {
                setter = (PropertyAccessorSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new PropertyDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), propertyKeyword, identifier, type, openBrace, getter, setter, closeBrace);
        }

        private SyntaxNode BuildCaseClause(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var caseKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var valuesBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.WhenKeyword && _green.GetSlot(slot)!.Kind != SyntaxKind.ColonToken)
            {
                valuesBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? whenKeyword = null;
            ExpressionSyntax? whenCondition = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.WhenKeyword)
            {
                whenKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.ColonToken)
                {
                    whenCondition = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }
            }

            var colonToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var body = (StatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var values = new SeparatedSyntaxList<ExpressionSyntax>(valuesBuilder.ToImmutable());
            return new CaseClauseSyntax(syntaxTree, caseKeyword, values, whenKeyword, whenCondition, colonToken, body);
        }

        private SyntaxNode BuildSwitchStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var expression = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? closeParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var sectionsBuilder = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseBraceToken)
            {
                sectionsBuilder.Add((SwitchSectionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new SwitchStatementSyntax(syntaxTree, keyword, openParenthesis, expression, closeParenthesis, openBrace, sectionsBuilder.ToImmutable(), closeBrace);
        }

        private SyntaxNode BuildLambdaExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? openParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.FatArrowToken && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? closeParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var arrowToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var body = (CSharpSyntaxNode)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            var hasExplicitParameterTypes = parameters.Count > 0 && parameters[0].Type != null;
            return new LambdaExpressionSyntax(syntaxTree, openParenthesis, parameters, closeParenthesis, hasExplicitParameterTypes, arrowToken, body);
        }

        private SyntaxNode BuildInterpolatedStringExpression(SyntaxTree syntaxTree, int position)
        {
            var interpolatedToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var contents = ImmutableArray.CreateBuilder<InterpolatedStringContentSyntax>();
            var contentPosition = position + _green.GetSlot(0)!.Width;
            for (var i = 1; i < _green.SlotCount; i++)
            {
                contents.Add((InterpolatedStringContentSyntax)_green.GetSlot(i)!.CreateTypedRed(syntaxTree, contentPosition));
                contentPosition += _green.GetSlot(i)!.Width;
            }

            return new InterpolatedStringExpressionSyntax(syntaxTree, interpolatedToken, contents.ToImmutable());
        }

        private SyntaxNode BuildInterpolatedStringText(SyntaxTree syntaxTree, int position)
        {
            var textToken = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new InterpolatedStringTextSyntax(syntaxTree, textToken);
        }

        private SyntaxNode BuildInterpolation(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var expression = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? commaToken = null;
            ExpressionSyntax? alignment = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CommaToken)
            {
                commaToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.ColonToken)
                {
                    alignment = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }
            }

            SyntaxToken? colonToken = null;
            SyntaxToken? formatToken = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                colonToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                if (slot < _green.SlotCount)
                {
                    formatToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                }
            }

            return new InterpolationSyntax(syntaxTree, expression, commaToken, alignment, colonToken, formatToken);
        }

        private SyntaxNode BuildImportClause(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var importKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            for (var i = slot; i < _green.SlotCount; i++)
            {
                nameTokens.Add((SyntaxToken)_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            return new ImportClauseSyntax(syntaxTree, importKeyword, nameTokens.ToImmutable());
        }

        private SyntaxNode BuildExternMetadataArgument(SyntaxTree syntaxTree, int position)
        {
            var key = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var equalsPosition = position + _green.GetSlot(0)!.Width;
            var equalsToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, equalsPosition);
            var valuePosition = equalsPosition + _green.GetSlot(1)!.Width;
            var value = (SyntaxToken)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, valuePosition);
            return new ExternMetadataArgumentSyntax(syntaxTree, key, equalsToken, value);
        }

        private SyntaxNode BuildExternMetadata(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var externKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var arguments = ImmutableArray.CreateBuilder<ExternMetadataArgumentSyntax>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.ExternMetadataArgument)
            {
                arguments.Add((ExternMetadataArgumentSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            SyntaxToken? closeParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new ExternMetadataSyntax(syntaxTree, externKeyword, openParenthesis, arguments.ToImmutable(), closeParenthesis);
        }

        private SyntaxNode BuildImportBlock(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var importKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            SyntaxToken? openParenthesis = null;
            SyntaxToken? charsetKey = null;
            SyntaxToken? charsetValue = null;
            SyntaxToken? closeParenthesis = null;

            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.OpenParenthesisToken && _green.GetSlot(slot)!.Kind != SyntaxKind.OpenBraceToken)
            {
                nameTokens.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    charsetKey = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                    if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                    {
                        charsetValue = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                        position += _green.GetSlot(slot)!.Width;
                        slot++;
                    }
                }

                if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }
            }

            var openBrace = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, slot, _green.SlotCount - 2);
            var closePosition = position;
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                closePosition += _green.GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new ImportBlockSyntax(syntaxTree, importKeyword, nameTokens.ToImmutable(), openParenthesis, charsetKey, charsetValue, closeParenthesis, openBrace, members, closeBrace);
        }

        /// <summary>鎶?[startIndex..endIndex] 妲戒綅鎵归噺杞负绫诲瀷鍖栫孩鑺傜偣鏁扮粍锛堢敤浜?Block 璇彞 / 闆嗗悎瀛愯妭鐐癸級銆?/summary>
        private ImmutableArray<T> BuildSlotArray<T>(SyntaxTree syntaxTree, int startPosition, int startIndex, int endIndex)
            where T : SyntaxNode
        {
            var builder = ImmutableArray.CreateBuilder<T>();
            var position = startPosition;
            for (var i = startIndex; i <= endIndex; i++)
            {
                var slot = _green.GetSlot(i)!;
                builder.Add((T)slot.CreateTypedRed(syntaxTree, position));
                position += slot.Width;
            }

            return builder.ToImmutable();
        }
    }
}
