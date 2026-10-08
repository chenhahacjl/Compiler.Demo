using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed partial class CocoaGreenNodeFactory
    {
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

            // 瀹炲弬妲斤細openParen 涓?closeParen锛堟湯妲斤級涔嬮棿锛宯ode/sep 浜ゆ浛锛岀洿鎺?SeparatedSyntaxList
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

        private SyntaxNode BuildTupleExpression(SyntaxTree syntaxTree, int position)
        {
            var openParenthesis = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(0)!.Width;

            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = 1; i < _green.SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            var closeParenthesis = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            var elements = new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
            return new TupleExpressionSyntax(syntaxTree, openParenthesis, elements, closeParenthesis);
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

        private SyntaxNode BuildWithExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var expression = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var withKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
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
            var assignments = new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
            return new WithExpressionSyntax(syntaxTree, expression, withKeyword, openBrace, assignments, closeBrace);
        }

        private SyntaxNode BuildCollectionExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var openBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < _green.SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(_green.GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(i)!.Width;
            }

            var closeBracket = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            var elements = new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
            return new CollectionExpressionSyntax(syntaxTree, openBracket, elements, closeBracket);
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

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? defaultValue = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                defaultValue = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new ParameterSyntax(syntaxTree, modifier, identifier, type, equalsToken, defaultValue);
        }

        private SyntaxNode BuildFunctionDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var attributes = ImmutableArray.CreateBuilder<AttributeSyntax>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.Attribute)
            {
                attributes.Add((AttributeSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

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
            return new FunctionDeclarationSyntax(syntaxTree, attributes.ToImmutable(), modifiers.ToImmutable(), functionKeyword, identifier, typeParameters, openParenthesis, parameters, closeParenthesis, type, body);
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
            or SyntaxKind.ExternKeyword or SyntaxKind.ReadonlyKeyword or SyntaxKind.ParamsKeyword;

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

        private SyntaxNode BuildDeclarationExpression(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var identifierPosition = position + _green.GetSlot(0)!.Width;
            var identifier = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
            return new DeclarationExpressionSyntax(syntaxTree, keyword, identifier);
        }

        private SyntaxNode BuildNamedArgument(SyntaxTree syntaxTree, int position)
        {
            var identifier = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(0)!.Width;
            var colonToken = (SyntaxToken)_green.GetSlot(1)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(1)!.Width;
            var expression = (ExpressionSyntax)_green.GetSlot(2)!.CreateTypedRed(syntaxTree, position);
            return new NamedArgumentExpressionSyntax(syntaxTree, identifier, colonToken, expression);
        }

        private SyntaxNode BuildLocalFunctionDeclarationStatement(SyntaxTree syntaxTree, int position)
        {
            var declaration = (FunctionDeclarationSyntax)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new LocalFunctionDeclarationStatementSyntax(syntaxTree, declaration);
        }

    }
}

