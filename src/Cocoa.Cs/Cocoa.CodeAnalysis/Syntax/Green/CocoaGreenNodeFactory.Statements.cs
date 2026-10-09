using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed partial class CocoaGreenNodeFactory
    {
        private SyntaxNode BuildEnumDeclaration(SyntaxTree syntaxTree, int position)
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
            return new EnumDeclarationSyntax(syntaxTree, attributes.ToImmutable(), modifiers.ToImmutable(), enumKeyword, identifier, openBrace, members, closeBrace);
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
            var parameters = BuildSlotArray<TypeParameterSyntax>(syntaxTree, parametersPosition, 1, _green.SlotCount - 2);
            var greaterPosition = parametersPosition;
            for (var i = 1; i < _green.SlotCount - 1; i++)
            {
                greaterPosition += _green.GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)_green.GetSlot(_green.SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new TypeParameterListSyntax(syntaxTree, lessThanToken, parameters, greaterThanToken);
        }

        private SyntaxNode BuildTypeParameter(SyntaxTree syntaxTree, int position)
        {
            SyntaxToken? varianceKeyword = null;
            var slot = 0;
            if (_green.GetSlot(0)!.Kind is SyntaxKind.InKeyword or SyntaxKind.OutKeyword)
            {
                varianceKeyword = (SyntaxToken)_green.GetSlot(0)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(0)!.Width;
                slot++;
            }

            var identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new TypeParameterSyntax(syntaxTree, varianceKeyword, identifier);
        }

        private SyntaxNode BuildClassFieldDeclaration(SyntaxTree syntaxTree, int position)
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

            return new ClassFieldDeclarationSyntax(syntaxTree, attributes.ToImmutable(), modifiers.ToImmutable(), identifier, type, equalsToken, initializer);
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

            // 6e-M22 delegate 鐪熷疄绫诲瀷鍖栵細绫诲瀷鍙傛暟鍒楄〃妲斤紙identifier 涓?openParen 涔嬮棿锛屼袱褰㈡€佺粺涓€婧愬簭锛?
            TypeParameterListSyntax? typeParameters = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.TypeParameterList)
            {
                typeParameters = (TypeParameterListSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
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
            return new DelegateDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), delegateKeyword, returnType, identifier, typeParameters, openParenToken, parameters, closeParenToken, semicolonToken);
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
            position += _green.GetSlot(slot)!.Width;
            slot++;

            // 访问器式事件：add/remove 体（与 EventDeclarationSyntax.ToGreen 的 GetChildren 槽序一致）
            BlockStatementSyntax? addBody = null, removeBody = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot) != null)
            {
                addBody = (BlockStatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            if (slot < _green.SlotCount && _green.GetSlot(slot) != null)
            {
                removeBody = (BlockStatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new EventDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), eventKeyword, identifier, handlerType, addBody, removeBody);
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

    }
}

