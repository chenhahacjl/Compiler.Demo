using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    internal sealed partial class CocoaParser
    {
        // ==================== Types ====================

        private TypeClauseSyntax? ParseOptionalTypeClause()
        {
            if (Current.Kind != SyntaxKind.ColonToken)
            {
                return null;
            }

            return ParseTypeClause();
        }

        private TypeClauseSyntax ParseTypeClause()
        {
            var colonToken = MatchToken(SyntaxKind.ColonToken);

            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsFunctionTypeStart())
            {
                return ParseFunctionTypeClause();
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(colonToken, identifier);
            type = WrapArrayTypeClause(colonToken, type);

            return type;
        }

        private TypeClauseSyntax WrapArrayTypeClause(SyntaxToken? colonToken, TypeClauseSyntax elementType)
        {
            var type = elementType;
            while (Current.Kind == SyntaxKind.OpenBracketToken &&
                   Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, colonToken, type, openBracketToken, closeBracketToken);
            }

            return type;
        }

        private TypeClauseSyntax ParseGenericTypeSuffix(SyntaxToken? colonToken, SyntaxToken identifier)
        {
            if (Current.Kind != SyntaxKind.LessToken)
            {
                return new TypeClauseSyntax(_syntaxTree, colonToken, identifier);
            }

            var lessThanToken = NextToken();
            var arguments = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

            while (true)
            {
                if (Current.Kind != SyntaxKind.IdentifierToken)
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
                    break;
                }

                arguments.Add(ParseSingleTypeArgument());

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    continue;
                }

                break;
            }

            var greaterThanToken = ParseClosingAngle();
            return new GenericTypeClauseSyntax(_syntaxTree, colonToken, identifier, lessThanToken, arguments.ToImmutable(), greaterThanToken);
        }

        private TypeClauseSyntax ParseSingleTypeArgument()
        {
            var argIdentifier = NextToken();
            TypeClauseSyntax arg = ParseGenericTypeSuffix(null, argIdentifier);

            while (Current.Kind == SyntaxKind.OpenBracketToken &&
                   Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracket = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
                arg = new ArrayTypeClauseSyntax(_syntaxTree, null, arg, openBracket, closeBracket);
            }

            return arg;
        }

        private TypeClauseSyntax ParseFunctionTypeClause()
        {
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ImmutableArray.CreateBuilder<SyntaxNode>();

            if (Current.Kind != SyntaxKind.CloseParenthesisToken)
            {
                while (true)
                {
                    parameters.Add(
                        Current.Kind == SyntaxKind.OpenParenthesisToken && IsFunctionTypeStart()
                            ? ParseFunctionTypeClause()
                            : ParseSingleTypeArgument());

                    if (Current.Kind == SyntaxKind.CommaToken)
                    {
                        parameters.Add(MatchToken(SyntaxKind.CommaToken));
                        continue;
                    }

                    break;
                }
            }

            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var arrowToken = MatchToken(SyntaxKind.ArrowToken);

            var returnType = Current.Kind == SyntaxKind.OpenParenthesisToken && IsFunctionTypeStart()
                ? ParseFunctionTypeClause()
                : ParseSingleTypeArgument();

            return new FunctionTypeSyntax(
                _syntaxTree,
                openParenthesisToken,
                new SeparatedSyntaxList<TypeClauseSyntax>(parameters.ToImmutable()),
                closeParenthesisToken,
                arrowToken,
                returnType);
        }

        private bool IsFunctionTypeStart()
        {
            if (Peek(0).Kind != SyntaxKind.OpenParenthesisToken)
            {
                return false;
            }

            var depth = 0;
            var i = 0;

            while (i < MaxLookahead)
            {
                switch (Peek(i).Kind)
                {
                    case SyntaxKind.OpenParenthesisToken:
                        depth++;
                        i++;
                        break;

                    case SyntaxKind.CloseParenthesisToken:
                        depth--;
                        i++;
                        if (depth == 0)
                        {
                            return Peek(i).Kind == SyntaxKind.ArrowToken;
                        }

                        break;

                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.LessToken:
                    case SyntaxKind.GreaterToken:
                    case SyntaxKind.ShiftRightToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.CloseBracketToken:
                    case SyntaxKind.ArrowToken:
                        i++;
                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        private int ScanBalancedAngleSuffix(int offset)
        {
            if (Peek(offset).Kind != SyntaxKind.LessToken)
            {
                return -1;
            }

            var depth = 0;
            var i = offset;

            while (true)
            {
                switch (Peek(i).Kind)
                {
                    case SyntaxKind.LessToken:
                        depth++;
                        i++;
                        break;

                    case SyntaxKind.GreaterToken:
                        depth--;
                        i++;
                        if (depth == 0)
                        {
                            return i;
                        }

                        if (depth < 0)
                        {
                            return -1;
                        }

                        break;

                    case SyntaxKind.ShiftRightToken:
                        depth -= 2;
                        i++;
                        if (depth == 0)
                        {
                            return i;
                        }

                        if (depth < 0)
                        {
                            return -1;
                        }

                        break;

                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.CloseBracketToken:
                        i++;
                        break;

                    default:
                        return -1;
                }
            }
        }

        private bool IsGenericTypeNameAhead()
        {
            var afterAngles = ScanBalancedAngleSuffix(1);
            return afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.IdentifierToken;
        }

        private SyntaxToken ParseClosingAngle()
        {
            if (_syntheticTokens.Count > 0 && _syntheticTokens.Peek().Kind == SyntaxKind.GreaterToken)
            {
                return NextToken();
            }

            if (Current.Kind == SyntaxKind.GreaterToken)
            {
                return MatchToken(SyntaxKind.GreaterToken);
            }

            if (Current.Kind == SyntaxKind.ShiftRightToken)
            {
                var shiftRight = NextToken();
                var second = new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, shiftRight.Position + 1, ">", null, ImmutableArray<SyntaxTrivia>.Empty, shiftRight.TrailingTrivia);
                _syntheticTokens.Enqueue(second);
                return new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, shiftRight.Position, ">", null, shiftRight.LeadingTrivia, ImmutableArray<SyntaxTrivia>.Empty);
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GreaterToken);
            return new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        private TypeArgumentListSyntax ParseTypeArgumentList()
        {
            var lessThanToken = NextToken();
            var arguments = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

            while (true)
            {
                if (Current.Kind != SyntaxKind.IdentifierToken)
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
                    break;
                }

                arguments.Add(ParseSingleTypeArgument());

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    continue;
                }

                break;
            }

            var greaterThanToken = ParseClosingAngle();
            return new TypeArgumentListSyntax(_syntaxTree, lessThanToken, arguments.ToImmutable(), greaterThanToken);
        }

        private TypeParameterListSyntax? ParseOptionalTypeParameterList()
        {
            if (Current.Kind != SyntaxKind.LessToken || !IsTypeParameterListAhead())
            {
                return null;
            }

            var lessThanToken = NextToken();
            var parameters = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (Current.Kind == SyntaxKind.IdentifierToken)
            {
                parameters.Add(NextToken());

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                }
                else
                {
                    break;
                }
            }

            var greaterThanToken = ParseClosingAngle();
            return new TypeParameterListSyntax(_syntaxTree, lessThanToken, parameters.ToImmutable(), greaterThanToken);
        }

        private bool IsTypeParameterListAhead()
        {
            var offset = 1;
            var sawIdentifier = false;

            while (true)
            {
                var kind = Peek(offset).Kind;
                switch (kind)
                {
                    case SyntaxKind.IdentifierToken:
                        sawIdentifier = true;
                        offset++;
                        break;

                    case SyntaxKind.CommaToken:
                        offset++;
                        break;

                    case SyntaxKind.GreaterToken:
                        return sawIdentifier;

                    case SyntaxKind.ShiftRightToken:
                        return false;

                    default:
                        return false;
                }
            }
        }

        private ImmutableArray<WhereClauseSyntax> ParseWhereClauses()
        {
            var clauses = ImmutableArray.CreateBuilder<WhereClauseSyntax>();

            while (Current.Kind == SyntaxKind.WhereKeyword)
            {
                var whereKeyword = NextToken();
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var colonToken = MatchToken(SyntaxKind.ColonToken);
                var constraints = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

                while (Current.Kind == SyntaxKind.IdentifierToken ||
                       Current.Kind == SyntaxKind.NewKeyword ||
                       Current.Kind == SyntaxKind.ClassKeyword ||
                       Current.Kind == SyntaxKind.StructKeyword)
                {
                    constraints.Add(ParseConstraintType());

                    if (Current.Kind == SyntaxKind.CommaToken)
                    {
                        NextToken();
                        continue;
                    }

                    break;
                }

                clauses.Add(new WhereClauseSyntax(_syntaxTree, whereKeyword, identifier, colonToken, constraints.ToImmutable()));
            }

            return clauses.ToImmutable();
        }

        private TypeClauseSyntax ParseConstraintType()
        {
            if (Current.Kind == SyntaxKind.NewKeyword &&
                Peek(1).Kind == SyntaxKind.OpenParenthesisToken &&
                Peek(2).Kind == SyntaxKind.CloseParenthesisToken)
            {
                var newKeyword = NextToken();
                NextToken();
                NextToken();
                var synthesized = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, newKeyword.Position, "new()", "new()", ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new TypeClauseSyntax(_syntaxTree, null, synthesized);
            }

            if (Current.Kind == SyntaxKind.ClassKeyword)
            {
                var keyword = NextToken();
                var synthesizedIdentifier = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, keyword.Position, keyword.Text, keyword.Text, keyword.LeadingTrivia, keyword.TrailingTrivia);
                return new TypeClauseSyntax(_syntaxTree, null, synthesizedIdentifier);
            }

            if (Current.Kind == SyntaxKind.StructKeyword)
            {
                var keyword = NextToken();
                var synthesizedIdentifier = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, keyword.Position, keyword.Text, keyword.Text, keyword.LeadingTrivia, keyword.TrailingTrivia);
                return new TypeClauseSyntax(_syntaxTree, null, synthesizedIdentifier);
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(null, identifier);

            while (Current.Kind == SyntaxKind.OpenBracketToken &&
                   Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, null, type, openBracketToken, closeBracketToken);
            }

            return type;
        }

        private TypeClauseSyntax ParseBaseTypeClause()
        {
            SyntaxToken prefixToken;
            if (Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                prefixToken = MatchToken(SyntaxKind.ExtendsKeyword);
            }
            else
            {
                prefixToken = MatchToken(SyntaxKind.ColonToken);
            }

            return CreateBaseTypeClause(prefixToken);
        }

        private TypeClauseSyntax CreateBaseTypeClause(SyntaxToken? prefixToken)
        {
            if (prefixToken == null)
            {
                prefixToken = new SyntaxToken(_syntaxTree, SyntaxKind.ColonToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            TypeClauseSyntax type = ParseGenericTypeSuffix(prefixToken, identifier);

            while (Current.Kind == SyntaxKind.OpenBracketToken &&
                   Peek(1).Kind == SyntaxKind.CloseBracketToken)
            {
                var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
                var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                type = new ArrayTypeClauseSyntax(_syntaxTree, prefixToken, type, openBracketToken, closeBracketToken);
            }

            return type;
        }
    }
}
