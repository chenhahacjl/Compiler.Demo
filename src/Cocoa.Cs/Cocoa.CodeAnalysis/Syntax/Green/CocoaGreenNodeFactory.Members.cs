using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed partial class CocoaGreenNodeFactory
    {
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

        private SyntaxNode BuildForRangeStatement(SyntaxTree syntaxTree, int position)
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

            SyntaxToken? identifier = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.IdentifierToken)
            {
                identifier = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
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

            var lowerBound = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var toKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var upperBound = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? stepKeyword = null;
            ExpressionSyntax? step = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.StepKeyword)
            {
                stepKeyword = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
                if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken && _green.GetSlot(slot)!.Kind != SyntaxKind.BlockStatement)
                {
                    step = (ExpressionSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }
            }

            SyntaxToken? closeParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var body = (StatementSyntax)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new ForRangeStatementSyntax(syntaxTree, keyword, openParenthesis, varKeyword, identifier, equalsToken, lowerBound, toKeyword, upperBound, stepKeyword, step, closeParenthesis, body);
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

            // 前置 Modifiers（global using 的 GlobalKeyword；GetChildren 先 yield Modifiers）
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.GlobalKeyword)
            {
                modifiers.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

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

            return new UsingDirectiveSyntax(syntaxTree, usingKeyword, staticKeyword, aliasToken, equalsToken, nameTokens.ToImmutable(), modifiers.ToImmutable());
        }

        private static bool IsBaseTypeSlot(SyntaxKind kind) => kind is
            SyntaxKind.TypeClause or SyntaxKind.ArrayTypeClause or SyntaxKind.GenericTypeClause;

        private SyntaxNode BuildClassLikeDeclaration(SyntaxTree syntaxTree, int position, bool isInterface)
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
                ? new InterfaceDeclarationSyntax(syntaxTree, attributes.ToImmutable(), modifiers.ToImmutable(), keyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses.ToImmutable(), openBrace, members, closeBrace)
                : new ClassDeclarationSyntax(syntaxTree, attributes.ToImmutable(), modifiers.ToImmutable(), keyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses.ToImmutable(), openBrace, members, closeBrace);
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
                position += _green.GetSlot(slot)!.Width; // 璺宠繃 initializer openParen
                slot++;
                var initArgsBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
                while (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    initArgsBuilder.Add(_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                    position += _green.GetSlot(slot)!.Width;
                    slot++;
                }

                position += _green.GetSlot(slot)!.Width; // 璺宠繃 initializer closeParen
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
            return new PropertyDeclarationSyntax(syntaxTree, attributes.ToImmutable(), modifiers.ToImmutable(), propertyKeyword, identifier, type, openBrace, getter, setter, closeBrace);
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
            var body = (SyntaxNode)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
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

        private SyntaxNode BuildAttribute(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var openBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;
            var name = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += _green.GetSlot(slot)!.Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < _green.SlotCount && _green.GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += _green.GetSlot(slot)!.Width;
                slot++;
            }

            var arguments = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < _green.SlotCount &&
                   (_green.GetSlot(slot)!.Kind == SyntaxKind.StringToken || _green.GetSlot(slot)!.Kind == SyntaxKind.CommaToken))
            {
                arguments.Add((SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
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

            var closeBracket = (SyntaxToken)_green.GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new AttributeSyntax(syntaxTree, openBracket, name, openParenthesis, arguments.ToImmutable(), closeParenthesis, closeBracket);
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

