using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法绿节点（Phase 4：不可变、无父链、无引用，可跨树共享；对齐 Roslyn
    /// <see cref="Microsoft.CodeAnalysis.Syntax.InternalSyntax.GreenNode"/>）。
    /// 红树（<see cref="SyntaxNode"/>）可经绿节点惰性实现；当前先落地绿层 + <see cref="SyntaxFactory"/>，
    /// 解析器迁移为后续里程碑。
    /// </summary>
    public abstract class GreenNode
    {
        private protected GreenNode(SyntaxKind kind)
        {
            Kind = kind;
        }

        public SyntaxKind Kind { get; }

        /// <summary>文本宽度（含子节点/trivia）。</summary>
        public abstract int Width { get; }

        /// <summary>直接子槽位数。</summary>
        public abstract int SlotCount { get; }

        public abstract GreenNode? GetSlot(int index);

        public abstract void WriteTo(TextWriter writer);

        public override string ToString()
        {
            using var writer = new StringWriter();
            WriteTo(writer);
            return writer.ToString();
        }

        /// <summary>绿→红（真·惰性红视图）：产出一个包裹本绿节点的 <see cref="RedNode"/>，
        /// 子节点经 <see cref="GreenNode.GetSlot"/> 惰性实现。</summary>
        public RedNode CreateRed(SyntaxTree syntaxTree, int position = 0, RedNode? parent = null)
        {
            return new RedNode(syntaxTree, this, position, parent);
        }

        /// <summary>绿→类型化红节点（逐类型迁移）：按 <see cref="Kind"/> 派发到具体类型（BinaryExpression/NameExpression/
        /// LiteralExpression 等，子节点递归类型化）；未覆盖的 Kind 回落通用 <see cref="RedNode"/>。</summary>
        public SyntaxNode CreateTypedRed(SyntaxTree syntaxTree, int position = 0)
        {
            if (this is GreenToken token)
            {
                return token.ToRed(syntaxTree, position);
            }

            return Kind switch
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
                SyntaxKind.ForStatement => BuildForStatement(syntaxTree, position),
                SyntaxKind.ArrayCreationExpression => BuildArrayCreationExpression(syntaxTree, position),
                SyntaxKind.NamespaceDeclaration => BuildNamespaceDeclaration(syntaxTree, position),
                SyntaxKind.UsingDirective => BuildUsingDirective(syntaxTree, position),
                SyntaxKind.ClassDeclaration => BuildClassLikeDeclaration(syntaxTree, position, isInterface: false),
                SyntaxKind.InterfaceDeclaration => BuildClassLikeDeclaration(syntaxTree, position, isInterface: true),
                SyntaxKind.CSStyleForStatement => BuildCSStyleForStatement(syntaxTree, position),
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
                _ => CreateRed(syntaxTree, position),
            };
        }

        private SyntaxNode BuildNameExpression(SyntaxTree syntaxTree, int position)
        {
            var identifier = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new NameExpressionSyntax(syntaxTree, identifier);
        }

        private SyntaxNode BuildBinaryExpression(SyntaxTree syntaxTree, int position)
        {
            var left = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operatorPosition = position + GetSlot(0)!.Width;
            var operatorToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, operatorPosition);
            var rightPosition = operatorPosition + GetSlot(1)!.Width;
            var right = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, rightPosition);
            return new BinaryExpressionSyntax(syntaxTree, left, operatorToken, right);
        }

        private SyntaxNode BuildLiteralExpression(SyntaxTree syntaxTree, int position)
        {
            var literalToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new LiteralExpressionSyntax(syntaxTree, literalToken);
        }

        private SyntaxNode BuildUnaryExpression(SyntaxTree syntaxTree, int position)
        {
            var operatorToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operandPosition = position + GetSlot(0)!.Width;
            var operand = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, operandPosition);
            return new UnaryExpressionSyntax(syntaxTree, operatorToken, operand);
        }

        private SyntaxNode BuildParenthesizedExpression(SyntaxTree syntaxTree, int position)
        {
            var openParenthesis = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            var closePosition = expressionPosition + GetSlot(1)!.Width;
            var closeParenthesis = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, closePosition);
            return new ParenthesizedExpressionSyntax(syntaxTree, openParenthesis, expression, closeParenthesis);
        }

        private SyntaxNode BuildExpressionStatement(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new ExpressionStatementSyntax(syntaxTree, expression);
        }

        private SyntaxNode BuildAssignmentExpression(SyntaxTree syntaxTree, int position)
        {
            var target = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var tokenPosition = position + GetSlot(0)!.Width;
            var assignmentToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, tokenPosition);
            var expressionPosition = tokenPosition + GetSlot(1)!.Width;
            var expression = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new AssignmentExpressionSyntax(syntaxTree, target, assignmentToken, expression);
        }

        private SyntaxNode BuildMemberAccessExpression(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var dotPosition = position + GetSlot(0)!.Width;
            var dotToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, dotPosition);
            var identifierPosition = dotPosition + GetSlot(1)!.Width;
            var identifierToken = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, identifierPosition);
            return new MemberAccessExpressionSyntax(syntaxTree, expression, dotToken, identifierToken);
        }

        private SyntaxNode BuildReturnStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            ExpressionSyntax? expression = null;
            if (SlotCount > 1)
            {
                var expressionPosition = position + GetSlot(0)!.Width;
                expression = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            }

            return new ReturnStatementSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildWhileStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var conditionPosition = position + GetSlot(0)!.Width;
            var condition = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, conditionPosition);
            var bodyPosition = conditionPosition + GetSlot(1)!.Width;
            var body = (StatementSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new WhileStatementSyntax(syntaxTree, keyword, condition, body);
        }

        private SyntaxNode BuildBlockStatement(SyntaxTree syntaxTree, int position)
        {
            var openBrace = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + GetSlot(0)!.Width;
            var statements = BuildSlotArray<StatementSyntax>(syntaxTree, bodyPosition, 1, SlotCount - 2);
            var closePosition = bodyPosition;
            for (var i = 1; i < SlotCount - 1; i++)
            {
                closePosition += GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new BlockStatementSyntax(syntaxTree, openBrace, statements, closeBrace);
        }

        private SyntaxNode BuildIfStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var conditionPosition = position + GetSlot(0)!.Width;
            var condition = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, conditionPosition);
            var thenPosition = conditionPosition + GetSlot(1)!.Width;
            var thenStatement = (StatementSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, thenPosition);
            ElseClauseSyntax? elseClause = null;
            if (SlotCount > 3)
            {
                var elsePosition = thenPosition + GetSlot(2)!.Width;
                elseClause = (ElseClauseSyntax)GetSlot(3)!.CreateTypedRed(syntaxTree, elsePosition);
            }

            return new IfStatementSyntax(syntaxTree, keyword, condition, thenStatement, elseClause);
        }

        private SyntaxNode BuildElseClause(SyntaxTree syntaxTree, int position)
        {
            var elseKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var statementPosition = position + GetSlot(0)!.Width;
            var elseStatement = (StatementSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, statementPosition);
            return new ElseClauseSyntax(syntaxTree, elseKeyword, elseStatement);
        }

        private SyntaxNode BuildVariableDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? keyword = null;
            if (GetSlot(slot)!.Kind is SyntaxKind.VarKeyword or SyntaxKind.LetKeyword)
            {
                keyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var identifier = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            TypeClauseSyntax? typeClause = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.TypeClause)
            {
                typeClause = (TypeClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? equalsToken = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            ExpressionSyntax? initializer = null;
            if (slot < SlotCount)
            {
                initializer = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            return new VariableDeclarationSyntax(syntaxTree, keyword, identifier, typeClause, equalsToken, initializer);
        }

        private SyntaxNode BuildTypeClause(SyntaxTree syntaxTree, int position)
        {
            if (SlotCount == 2)
            {
                var colonToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
                var identifierPosition = position + GetSlot(0)!.Width;
                var identifier = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
                return new TypeClauseSyntax(syntaxTree, colonToken, identifier);
            }

            var typeIdentifier = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new TypeClauseSyntax(syntaxTree, null, typeIdentifier);
        }

        private SyntaxNode BuildCallExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            TypeArgumentListSyntax? typeArguments = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.TypeArgumentList)
            {
                typeArguments = (TypeArgumentListSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            // 实参槽：openParen 与 closeParen（末槽）之间，node/sep 交替，直构 SeparatedSyntaxList
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(i)!.Width;
            }

            var closeParenthesis = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            var arguments = new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
            return new CallExpressionSyntax(syntaxTree, identifier, typeArguments, openParenthesis, arguments, closeParenthesis);
        }

        private SyntaxNode BuildTypeArgumentList(SyntaxTree syntaxTree, int position)
        {
            var lessThanToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var argumentsPosition = position + GetSlot(0)!.Width;
            var arguments = BuildSlotArray<TypeClauseSyntax>(syntaxTree, argumentsPosition, 1, SlotCount - 2);
            var greaterPosition = argumentsPosition;
            for (var i = 1; i < SlotCount - 1; i++)
            {
                greaterPosition += GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new TypeArgumentListSyntax(syntaxTree, lessThanToken, arguments, greaterThanToken);
        }

        private SyntaxNode BuildMemberCallExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var expression = (ExpressionSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var dotToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var callTail = BuildCallTail(syntaxTree, position, slot);
            return new MemberCallExpressionSyntax(syntaxTree, expression, dotToken, identifier, callTail.TypeArguments, callTail.OpenParenthesis, callTail.Arguments, callTail.CloseParenthesis);
        }

        private SyntaxNode BuildObjectCreationExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var newKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var callTail = BuildCallTail(syntaxTree, position, slot);
            return new ObjectCreationExpressionSyntax(syntaxTree, newKeyword, identifier, callTail.TypeArguments, callTail.OpenParenthesis, callTail.Arguments, callTail.CloseParenthesis);
        }

        private SyntaxNode BuildElementAccessExpression(SyntaxTree syntaxTree, int position)
        {
            var expression = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var openPosition = position + GetSlot(0)!.Width;
            var openBracket = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, openPosition);
            var indexPosition = openPosition + GetSlot(1)!.Width;
            var index = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, indexPosition);
            var closePosition = indexPosition + GetSlot(2)!.Width;
            var closeBracket = (SyntaxToken)GetSlot(3)!.CreateTypedRed(syntaxTree, closePosition);
            return new ElementAccessExpressionSyntax(syntaxTree, expression, openBracket, index, closeBracket);
        }

        /// <summary>调用尾段（typeArgs? + openParen + 实参 SeparatedSyntaxList + closeParen）——Call/MemberCall/ObjectCreation 共用。</summary>
        private (TypeArgumentListSyntax? TypeArguments, SyntaxToken OpenParenthesis, SeparatedSyntaxList<ExpressionSyntax> Arguments, SyntaxToken CloseParenthesis) BuildCallTail(
            SyntaxTree syntaxTree, int position, int slot)
        {
            TypeArgumentListSyntax? typeArguments = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.TypeArgumentList)
            {
                typeArguments = (TypeArgumentListSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(i)!.Width;
            }

            var closeParenthesis = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            return (typeArguments, openParenthesis, new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable()), closeParenthesis);
        }

        private SyntaxNode BuildParameter(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? modifier = null;
            if (slot < SlotCount && (IsModifierToken(GetSlot(slot)!.Kind) || IsByRefModifierToken(GetSlot(slot)!.Kind)))
            {
                modifier = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken identifier;
            TypeClauseSyntax type;
            if (slot < SlotCount && IsTypeLikeSlot(GetSlot(slot)!.Kind))
            {
                // 类型前置（.cs：`int x`）
                type = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }
            else
            {
                // 名称前置（.co：`x: i32`）
                identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                type = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            }

            return new ParameterSyntax(syntaxTree, modifier, identifier, type);
        }

        private SyntaxNode BuildFunctionDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? functionKeyword = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.FunctionKeyword)
            {
                functionKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            TypeParameterListSyntax? typeParameters = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.TypeParameterList)
            {
                typeParameters = (TypeParameterListSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            TypeClauseSyntax? type = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.TypeClause)
            {
                type = (TypeClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            BlockStatementSyntax? body = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.BlockStatement)
            {
                body = (BlockStatementSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            return new FunctionDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), functionKeyword, identifier, typeParameters, openParenthesis, parameters, closeParenthesis, type, body);
        }

        private SyntaxNode BuildCompilationUnit(SyntaxTree syntaxTree, int position)
        {
            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, 0, SlotCount - 2);
            var endOfFilePosition = position;
            for (var i = 0; i < SlotCount - 1; i++)
            {
                endOfFilePosition += GetSlot(i)!.Width;
            }

            var endOfFile = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, endOfFilePosition);
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
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return Kind == SyntaxKind.BreakStatement
                ? new BreakStatementSyntax(syntaxTree, keyword)
                : new ContinueStatementSyntax(syntaxTree, keyword);
        }

        private SyntaxNode BuildKeywordExpression(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return Kind == SyntaxKind.ThisExpression
                ? new ThisExpressionSyntax(syntaxTree, keyword)
                : new BaseExpressionSyntax(syntaxTree, keyword);
        }

        private SyntaxNode BuildThrowStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new ThrowStatementSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildDoWhileStatement(SyntaxTree syntaxTree, int position)
        {
            var doKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + GetSlot(0)!.Width;
            var body = (StatementSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, bodyPosition);
            var whilePosition = bodyPosition + GetSlot(1)!.Width;
            var whileKeyword = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, whilePosition);
            var conditionPosition = whilePosition + GetSlot(2)!.Width;
            var condition = (ExpressionSyntax)GetSlot(3)!.CreateTypedRed(syntaxTree, conditionPosition);
            return new DoWhileStatementSyntax(syntaxTree, doKeyword, body, whileKeyword, condition);
        }

        private SyntaxNode BuildCastExpression(SyntaxTree syntaxTree, int position)
        {
            var openParenthesis = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var typePosition = position + GetSlot(0)!.Width;
            var typeName = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, typePosition);
            var closePosition = typePosition + GetSlot(1)!.Width;
            var closeParenthesis = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, closePosition);
            var expressionPosition = closePosition + GetSlot(2)!.Width;
            var expression = (ExpressionSyntax)GetSlot(3)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new CastExpressionSyntax(syntaxTree, openParenthesis, typeName, closeParenthesis, expression);
        }

        private SyntaxNode BuildAsIsExpression(SyntaxTree syntaxTree, int position, bool isAs)
        {
            var expression = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var keywordPosition = position + GetSlot(0)!.Width;
            var keyword = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, keywordPosition);
            var typePosition = keywordPosition + GetSlot(1)!.Width;
            var typeName = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, typePosition);
            return isAs
                ? new AsExpressionSyntax(syntaxTree, expression, keyword, typeName)
                : new IsExpressionSyntax(syntaxTree, expression, keyword, typeName);
        }

        private SyntaxNode BuildPostfixIncrementExpression(SyntaxTree syntaxTree, int position)
        {
            var operand = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var operatorPosition = position + GetSlot(0)!.Width;
            var operatorToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, operatorPosition);
            return new PostfixIncrementExpressionSyntax(syntaxTree, operand, operatorToken);
        }

        private SyntaxNode BuildByRefArgumentExpression(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var expressionPosition = position + GetSlot(0)!.Width;
            var expression = (ExpressionSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, expressionPosition);
            return new ByRefArgumentExpressionSyntax(syntaxTree, keyword, expression);
        }

        private SyntaxNode BuildEnumDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var enumKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var openBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            for (var i = slot; i < SlotCount - 1; i++)
            {
                nodesAndSeparators.Add(GetSlot(i)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, position);
            var members = new SeparatedSyntaxList<EnumMemberSyntax>(nodesAndSeparators.ToImmutable());
            return new EnumDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), enumKeyword, identifier, openBrace, members, closeBrace);
        }

        private SyntaxNode BuildEnumMember(SyntaxTree syntaxTree, int position)
        {
            var identifier = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? value = null;
            if (SlotCount > 1)
            {
                var equalsPosition = position + GetSlot(0)!.Width;
                equalsToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, equalsPosition);
                value = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, equalsPosition + GetSlot(1)!.Width);
            }

            return new EnumMemberSyntax(syntaxTree, identifier, equalsToken, value);
        }

        private SyntaxNode BuildGlobalStatement(SyntaxTree syntaxTree, int position)
        {
            var statement = (StatementSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new GlobalStatementSyntax(syntaxTree, statement);
        }

        private SyntaxNode BuildConditionalExpression(SyntaxTree syntaxTree, int position)
        {
            var condition = (ExpressionSyntax)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var questionPosition = position + GetSlot(0)!.Width;
            var questionToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, questionPosition);
            var whenTruePosition = questionPosition + GetSlot(1)!.Width;
            var whenTrue = (ExpressionSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, whenTruePosition);
            var colonPosition = whenTruePosition + GetSlot(2)!.Width;
            var colonToken = (SyntaxToken)GetSlot(3)!.CreateTypedRed(syntaxTree, colonPosition);
            var whenFalsePosition = colonPosition + GetSlot(3)!.Width;
            var whenFalse = (ExpressionSyntax)GetSlot(4)!.CreateTypedRed(syntaxTree, whenFalsePosition);
            return new ConditionalExpressionSyntax(syntaxTree, condition, questionToken, whenTrue, colonToken, whenFalse);
        }

        private SyntaxNode BuildTypeParameterList(SyntaxTree syntaxTree, int position)
        {
            var lessThanToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var parametersPosition = position + GetSlot(0)!.Width;
            var parameters = BuildSlotArray<SyntaxToken>(syntaxTree, parametersPosition, 1, SlotCount - 2);
            var greaterPosition = parametersPosition;
            for (var i = 1; i < SlotCount - 1; i++)
            {
                greaterPosition += GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new TypeParameterListSyntax(syntaxTree, lessThanToken, parameters, greaterThanToken);
        }

        private SyntaxNode BuildClassFieldDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var type = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                if (slot < SlotCount)
                {
                    initializer = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                }
            }

            return new ClassFieldDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), identifier, type, equalsToken, initializer);
        }

        private SyntaxNode BuildArrayTypeClause(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? colonToken = null;
            if (GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                colonToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            // 基类 TypeClause.Identifier 槽（= elementType.Identifier）直接跳过
            var elementPosition = position + GetSlot(slot)!.Width;
            slot++;
            var elementType = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, elementPosition);
            var openPosition = elementPosition + GetSlot(slot)!.Width;
            slot++;
            var openBracket = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, openPosition);
            var closePosition = openPosition + GetSlot(slot)!.Width;
            slot++;
            var closeBracket = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, closePosition);
            return new ArrayTypeClauseSyntax(syntaxTree, colonToken, elementType, openBracket, closeBracket);
        }

        private SyntaxNode BuildFunctionType(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var openParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var parameterTypesBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parameterTypesBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var arrowToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var returnType = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var parameterTypes = new SeparatedSyntaxList<TypeClauseSyntax>(parameterTypesBuilder.ToImmutable());
            return new FunctionTypeSyntax(syntaxTree, openParenthesis, parameterTypes, closeParenthesis, arrowToken, returnType);
        }

        private SyntaxNode BuildGenericTypeClause(SyntaxTree syntaxTree, int position)
        {
            SyntaxToken? colonToken = null;
            var slot = 0;
            if (GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                colonToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var lessThanToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var typeArguments = BuildSlotArray<TypeClauseSyntax>(syntaxTree, position, slot, SlotCount - 2);
            var greaterPosition = position;
            for (var i = slot; i < SlotCount - 1; i++)
            {
                greaterPosition += GetSlot(i)!.Width;
            }

            var greaterThanToken = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, greaterPosition);
            return new GenericTypeClauseSyntax(syntaxTree, colonToken, identifier, lessThanToken, typeArguments, greaterThanToken);
        }

        private SyntaxNode BuildDelegateDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var delegateKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            // 源序槽布局（与 DelegateDeclarationSyntax.ToGreen 一致）：
            // .co：delegate 名 ( 参数 ) [: 返回类型]；.cs：delegate 返回类型 名 ( 参数 ) [;]
            // 判别：`.cs` 前置返回类型槽为类型族；`.co` 恒为标识符
            var isCoForm = !IsTypeLikeSlot(GetSlot(slot)!.Kind);
            TypeClauseSyntax? returnType = null;
            SyntaxToken identifier;

            if (isCoForm)
            {
                identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }
            else
            {
                returnType = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var openParenToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeParenToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            if (isCoForm && slot < SlotCount && IsTypeLikeSlot(GetSlot(slot)!.Kind))
            {
                returnType = (TypeClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? semicolonToken = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolonToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            return new DelegateDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), delegateKeyword, returnType, identifier, openParenToken, parameters, closeParenToken, semicolonToken);
        }

        private SyntaxNode BuildEventDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var eventKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var handlerType = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new EventDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), eventKeyword, identifier, handlerType);
        }

        private SyntaxNode BuildPropertyAccessor(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var keyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            BlockStatementSyntax? body = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.BlockStatement)
            {
                body = (BlockStatementSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? semicolonToken = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolonToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            return new PropertyAccessorSyntax(syntaxTree, modifiers.ToImmutable(), keyword, body, semicolonToken);
        }

        private SyntaxNode BuildWhereClause(SyntaxTree syntaxTree, int position)
        {
            var whereKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var identifierPosition = position + GetSlot(0)!.Width;
            var identifier = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
            var colonPosition = identifierPosition + GetSlot(1)!.Width;
            var colonToken = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, colonPosition);
            var constraintsPosition = colonPosition + GetSlot(2)!.Width;
            var constraintTypes = BuildSlotArray<TypeClauseSyntax>(syntaxTree, constraintsPosition, 3, SlotCount - 1);
            return new WhereClauseSyntax(syntaxTree, whereKeyword, identifier, colonToken, constraintTypes);
        }

        private SyntaxNode BuildDefaultClause(SyntaxTree syntaxTree, int position)
        {
            var defaultKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var colonPosition = position + GetSlot(0)!.Width;
            var colonToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, colonPosition);
            var bodyPosition = colonPosition + GetSlot(1)!.Width;
            var body = (StatementSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new DefaultClauseSyntax(syntaxTree, defaultKeyword, colonToken, body);
        }

        private SyntaxNode BuildFinallyClause(SyntaxTree syntaxTree, int position)
        {
            var finallyKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var bodyPosition = position + GetSlot(0)!.Width;
            var body = (BlockStatementSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new FinallyClauseSyntax(syntaxTree, finallyKeyword, body);
        }

        private SyntaxNode BuildTryStatement(SyntaxTree syntaxTree, int position)
        {
            var keyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var blockPosition = position + GetSlot(0)!.Width;
            var tryBlock = (BlockStatementSyntax)GetSlot(1)!.CreateTypedRed(syntaxTree, blockPosition);
            position += GetSlot(1)!.Width;

            var catches = ImmutableArray.CreateBuilder<CatchClauseSyntax>();
            var slot = 2;
            while (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CatchClause)
            {
                catches.Add((CatchClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            FinallyClauseSyntax? finallyClause = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.FinallyClause)
            {
                finallyClause = (FinallyClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            return new TryStatementSyntax(syntaxTree, keyword, tryBlock, catches.ToImmutable(), finallyClause);
        }

        private SyntaxNode BuildCatchClause(SyntaxTree syntaxTree, int position)
        {
            var catchKeyword = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var identifierPosition = position + GetSlot(0)!.Width;
            var identifier = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, identifierPosition);
            var typePosition = identifierPosition + GetSlot(1)!.Width;
            var type = (TypeClauseSyntax)GetSlot(2)!.CreateTypedRed(syntaxTree, typePosition);
            var bodyPosition = typePosition + GetSlot(2)!.Width;
            var body = (BlockStatementSyntax)GetSlot(3)!.CreateTypedRed(syntaxTree, bodyPosition);
            return new CatchClauseSyntax(syntaxTree, catchKeyword, identifier, type, body);
        }

        private SyntaxNode BuildForeachStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? varKeyword = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.VarKeyword)
            {
                varKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var inKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var collection = (ExpressionSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? closeParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var body = (StatementSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new ForeachStatementSyntax(syntaxTree, keyword, openParenthesis, varKeyword, identifier, inKeyword, collection, closeParenthesis, body);
        }

        private SyntaxNode BuildForStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? varKeyword = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.VarKeyword)
            {
                varKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? identifier = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.IdentifierToken)
            {
                identifier = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? equalsToken = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var lowerBound = (ExpressionSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var toKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var upperBound = (ExpressionSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? stepKeyword = null;
            ExpressionSyntax? step = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.StepKeyword)
            {
                stepKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken && GetSlot(slot)!.Kind != SyntaxKind.BlockStatement)
                {
                    step = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                    position += GetSlot(slot).Width;
                    slot++;
                }
            }

            SyntaxToken? closeParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var body = (StatementSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new ForStatementSyntax(syntaxTree, keyword, openParenthesis, varKeyword, identifier, equalsToken, lowerBound, toKeyword, upperBound, stepKeyword, step, closeParenthesis, body);
        }

        private SyntaxNode BuildArrayCreationExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var newKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var openBracket = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            ExpressionSyntax? size = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseBracketToken)
            {
                size = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeBracket = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? openBrace = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenBraceToken)
            {
                openBrace = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var elementsBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseBraceToken)
            {
                elementsBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? closeBrace = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseBraceToken)
            {
                closeBrace = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            var elements = new SeparatedSyntaxList<ExpressionSyntax>(elementsBuilder.ToImmutable());
            return new ArrayCreationExpressionSyntax(syntaxTree, newKeyword, identifier, openBracket, size, closeBracket, openBrace, elements, closeBrace);
        }

        private SyntaxNode BuildNamespaceDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var namespaceKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.OpenBraceToken)
            {
                nameTokens.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var openBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, slot, SlotCount - 2);
            var closePosition = position;
            for (var i = slot; i < SlotCount - 1; i++)
            {
                closePosition += GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new NamespaceDeclarationSyntax(syntaxTree, namespaceKeyword, nameTokens.ToImmutable(), openBrace, members, closeBrace);
        }

        private SyntaxNode BuildUsingDirective(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var usingKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? staticKeyword = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.StaticKeyword)
            {
                staticKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            // 别名：`using Alias = Foo.Bar` → aliasToken + EqualsToken 前缀
            SyntaxToken? aliasToken = null;
            SyntaxToken? equalsToken = null;
            if (slot + 1 < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.IdentifierToken && GetSlot(slot + 1)!.Kind == SyntaxKind.EqualsToken)
            {
                aliasToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                equalsToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            for (var i = slot; i < SlotCount; i++)
            {
                nameTokens.Add((SyntaxToken)GetSlot(i).CreateTypedRed(syntaxTree, position));
                position += GetSlot(i)!.Width;
            }

            return new UsingDirectiveSyntax(syntaxTree, usingKeyword, staticKeyword, aliasToken, equalsToken, nameTokens.ToImmutable());
        }

        private static bool IsBaseTypeSlot(SyntaxKind kind) => kind is
            SyntaxKind.TypeClause or SyntaxKind.ArrayTypeClause or SyntaxKind.GenericTypeClause;

        private SyntaxNode BuildClassLikeDeclaration(SyntaxTree syntaxTree, int position, bool isInterface)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var keyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            TypeParameterListSyntax? typeParameters = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.TypeParameterList)
            {
                typeParameters = (TypeParameterListSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();
            while (slot < SlotCount && IsBaseTypeSlot(GetSlot(slot)!.Kind))
            {
                baseTypes.Add((TypeClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var whereClauses = ImmutableArray.CreateBuilder<WhereClauseSyntax>();
            while (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.WhereClause)
            {
                whereClauses.Add((WhereClauseSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var openBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, slot, SlotCount - 2);
            var closePosition = position;
            for (var i = slot; i < SlotCount - 1; i++)
            {
                closePosition += GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return isInterface
                ? new InterfaceDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), keyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses.ToImmutable(), openBrace, members, closeBrace)
                : new ClassDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), keyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses.ToImmutable(), openBrace, members, closeBrace);
        }

        private SyntaxNode BuildCSStyleForStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var openParen = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            StatementSyntax? init = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.SemicolonToken)
            {
                init = (StatementSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? semicolon1 = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolon1 = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            ExpressionSyntax? condition = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.SemicolonToken)
            {
                condition = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? semicolon2 = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.SemicolonToken)
            {
                semicolon2 = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            ExpressionSyntax? update = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                update = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeParen = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var body = (StatementSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new CSStyleForStatementSyntax(syntaxTree, keyword, openParen, init, semicolon1, condition, semicolon2, update, closeParen, body);
        }

        private SyntaxNode BuildConstructorDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? constructorKeyword = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.ConstructorKeyword)
            {
                constructorKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var openParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeParenthesis = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                position += GetSlot(slot).Width;
                slot++;
                initializerKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                position += GetSlot(slot).Width; // 跳过 initializer openParen
                slot++;
                var initArgsBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
                while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    initArgsBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                    position += GetSlot(slot).Width;
                    slot++;
                }

                position += GetSlot(slot).Width; // 跳过 initializer closeParen
                slot++;
                initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(initArgsBuilder.ToImmutable());
            }

            var body = (BlockStatementSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            return new ConstructorDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), constructorKeyword, openParenthesis, parameters, closeParenthesis, initializerKeyword, initializerArguments, body);
        }

        private SyntaxNode BuildPropertyDeclaration(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (slot < SlotCount && IsModifierToken(GetSlot(slot)!.Kind))
            {
                modifiers.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? propertyKeyword = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.PropertyKeyword)
            {
                propertyKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var identifier = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var type = (TypeClauseSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var openBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            PropertyAccessorSyntax? getter = null;
            PropertyAccessorSyntax? setter = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.PropertyAccessor)
            {
                getter = (PropertyAccessorSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.PropertyAccessor)
            {
                setter = (PropertyAccessorSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new PropertyDeclarationSyntax(syntaxTree, modifiers.ToImmutable(), propertyKeyword, identifier, type, openBrace, getter, setter, closeBrace);
        }

        private SyntaxNode BuildCaseClause(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var caseKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var valuesBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.WhenKeyword && GetSlot(slot)!.Kind != SyntaxKind.ColonToken)
            {
                valuesBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? whenKeyword = null;
            ExpressionSyntax? whenCondition = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.WhenKeyword)
            {
                whenKeyword = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.ColonToken)
                {
                    whenCondition = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                    position += GetSlot(slot).Width;
                    slot++;
                }
            }

            var colonToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var body = (StatementSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var values = new SeparatedSyntaxList<ExpressionSyntax>(valuesBuilder.ToImmutable());
            return new CaseClauseSyntax(syntaxTree, caseKeyword, values, whenKeyword, whenCondition, colonToken, body);
        }

        private SyntaxNode BuildSwitchStatement(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var keyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var expression = (ExpressionSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? closeParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var openBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var sectionsBuilder = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseBraceToken)
            {
                sectionsBuilder.Add((SwitchSectionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            var closeBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            return new SwitchStatementSyntax(syntaxTree, keyword, openParenthesis, expression, closeParenthesis, openBrace, sectionsBuilder.ToImmutable(), closeBrace);
        }

        private SyntaxNode BuildLambdaExpression(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            SyntaxToken? openParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var parametersBuilder = ImmutableArray.CreateBuilder<SyntaxNode>();
            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.FatArrowToken && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
            {
                parametersBuilder.Add(GetSlot(slot)!.CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? closeParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var arrowToken = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var body = GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            var parameters = new SeparatedSyntaxList<ParameterSyntax>(parametersBuilder.ToImmutable());
            var hasExplicitParameterTypes = parameters.Count > 0 && parameters[0].Type != null;
            return new LambdaExpressionSyntax(syntaxTree, openParenthesis, parameters, closeParenthesis, hasExplicitParameterTypes, arrowToken, body);
        }

        private SyntaxNode BuildInterpolatedStringExpression(SyntaxTree syntaxTree, int position)
        {
            var interpolatedToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var contents = ImmutableArray.CreateBuilder<InterpolatedStringContentSyntax>();
            var contentPosition = position + GetSlot(0)!.Width;
            for (var i = 1; i < SlotCount; i++)
            {
                contents.Add((InterpolatedStringContentSyntax)GetSlot(i)!.CreateTypedRed(syntaxTree, contentPosition));
                contentPosition += GetSlot(i)!.Width;
            }

            return new InterpolatedStringExpressionSyntax(syntaxTree, interpolatedToken, contents.ToImmutable());
        }

        private SyntaxNode BuildInterpolatedStringText(SyntaxTree syntaxTree, int position)
        {
            var textToken = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            return new InterpolatedStringTextSyntax(syntaxTree, textToken);
        }

        private SyntaxNode BuildInterpolation(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var expression = (ExpressionSyntax)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? commaToken = null;
            ExpressionSyntax? alignment = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CommaToken)
            {
                commaToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.ColonToken)
                {
                    alignment = (ExpressionSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                    position += GetSlot(slot).Width;
                    slot++;
                }
            }

            SyntaxToken? colonToken = null;
            SyntaxToken? formatToken = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.ColonToken)
            {
                colonToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                if (slot < SlotCount)
                {
                    formatToken = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                }
            }

            return new InterpolationSyntax(syntaxTree, expression, commaToken, alignment, colonToken, formatToken);
        }

        private SyntaxNode BuildImportClause(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var importKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            for (var i = slot; i < SlotCount; i++)
            {
                nameTokens.Add((SyntaxToken)GetSlot(i).CreateTypedRed(syntaxTree, position));
                position += GetSlot(i)!.Width;
            }

            return new ImportClauseSyntax(syntaxTree, importKeyword, nameTokens.ToImmutable());
        }

        private SyntaxNode BuildExternMetadataArgument(SyntaxTree syntaxTree, int position)
        {
            var key = (SyntaxToken)GetSlot(0)!.CreateTypedRed(syntaxTree, position);
            var equalsPosition = position + GetSlot(0)!.Width;
            var equalsToken = (SyntaxToken)GetSlot(1)!.CreateTypedRed(syntaxTree, equalsPosition);
            var valuePosition = equalsPosition + GetSlot(1)!.Width;
            var value = (SyntaxToken)GetSlot(2)!.CreateTypedRed(syntaxTree, valuePosition);
            return new ExternMetadataArgumentSyntax(syntaxTree, key, equalsToken, value);
        }

        private SyntaxNode BuildExternMetadata(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var externKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            SyntaxToken? openParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
            }

            var arguments = ImmutableArray.CreateBuilder<ExternMetadataArgumentSyntax>();
            while (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.ExternMetadataArgument)
            {
                arguments.Add((ExternMetadataArgumentSyntax)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            SyntaxToken? closeParenthesis = null;
            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
            {
                closeParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
            }

            return new ExternMetadataSyntax(syntaxTree, externKeyword, openParenthesis, arguments.ToImmutable(), closeParenthesis);
        }

        private SyntaxNode BuildImportBlock(SyntaxTree syntaxTree, int position)
        {
            var slot = 0;
            var importKeyword = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;

            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
            SyntaxToken? openParenthesis = null;
            SyntaxToken? charsetKey = null;
            SyntaxToken? charsetValue = null;
            SyntaxToken? closeParenthesis = null;

            while (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.OpenParenthesisToken && GetSlot(slot)!.Kind != SyntaxKind.OpenBraceToken)
            {
                nameTokens.Add((SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position));
                position += GetSlot(slot).Width;
                slot++;
            }

            if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                position += GetSlot(slot).Width;
                slot++;
                if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    charsetKey = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                    position += GetSlot(slot).Width;
                    slot++;
                    if (slot < SlotCount && GetSlot(slot)!.Kind != SyntaxKind.CloseParenthesisToken)
                    {
                        charsetValue = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                        position += GetSlot(slot).Width;
                        slot++;
                    }
                }

                if (slot < SlotCount && GetSlot(slot)!.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    closeParenthesis = (SyntaxToken)GetSlot(slot).CreateTypedRed(syntaxTree, position);
                    position += GetSlot(slot).Width;
                    slot++;
                }
            }

            var openBrace = (SyntaxToken)GetSlot(slot)!.CreateTypedRed(syntaxTree, position);
            position += GetSlot(slot).Width;
            slot++;
            var members = BuildSlotArray<MemberSyntax>(syntaxTree, position, slot, SlotCount - 2);
            var closePosition = position;
            for (var i = slot; i < SlotCount - 1; i++)
            {
                closePosition += GetSlot(i)!.Width;
            }

            var closeBrace = (SyntaxToken)GetSlot(SlotCount - 1)!.CreateTypedRed(syntaxTree, closePosition);
            return new ImportBlockSyntax(syntaxTree, importKeyword, nameTokens.ToImmutable(), openParenthesis, charsetKey, charsetValue, closeParenthesis, openBrace, members, closeBrace);
        }

        /// <summary>把 [startIndex..endIndex] 槽位批量转为类型化红节点数组（用于 Block 语句 / 集合子节点）。</summary>
        private ImmutableArray<T> BuildSlotArray<T>(SyntaxTree syntaxTree, int startPosition, int startIndex, int endIndex)
            where T : SyntaxNode
        {
            var builder = ImmutableArray.CreateBuilder<T>();
            var position = startPosition;
            for (var i = startIndex; i <= endIndex; i++)
            {
                var slot = GetSlot(i)!;
                builder.Add((T)slot.CreateTypedRed(syntaxTree, position));
                position += slot.Width;
            }

            return builder.ToImmutable();
        }
    }
}