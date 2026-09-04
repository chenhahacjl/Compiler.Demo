using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Authoring
{
    public sealed class Classifier
    {
        public static ImmutableArray<ClassifiedSpan> Classify(SyntaxTree syntaxTree, TextSpan span)
        {
            var result = ImmutableArray.CreateBuilder<ClassifiedSpan>();

            ClassifyNode(syntaxTree.Root, span, result);

            return result.ToImmutableArray();
        }

        private static void ClassifyNode(SyntaxNode node, TextSpan span, ImmutableArray<ClassifiedSpan>.Builder result)
        {
            if (!node.FullSpan.OverlapsWith(span))
            {
                return;
            }

            if (node is SyntaxToken token)
            {
                ClassifyToken(token, span, result);
            }

            foreach (var child in node.GetChildren())
            {
                ClassifyNode(child, span, result);
            }
        }

        private static void ClassifyToken(SyntaxToken token, TextSpan span, ImmutableArray<ClassifiedSpan>.Builder result)
        {
            foreach (var leadingTrivia in token.LeadingTrivia)
            {
                ClassifyTrivia(leadingTrivia, span, result);
            }

            AddClassification(token.Kind, token.Span, span, result);

            foreach (var trailingTrivia in token.TrailingTrivia)
            {
                ClassifyTrivia(trailingTrivia, span, result);
            }
        }

        private static void ClassifyTrivia(SyntaxTrivia trivia, TextSpan span, ImmutableArray<ClassifiedSpan>.Builder result)
        {
            AddClassification(trivia.Kind, trivia.Span, span, result);
        }

        private static void AddClassification(SyntaxKind elementKind, TextSpan elementSpan, TextSpan span, ImmutableArray<ClassifiedSpan>.Builder result)
        {
            if (!elementSpan.OverlapsWith(span))
            {
                return;
            }

            var adjustedStart = Math.Max(elementSpan.Start, span.Start);
            var adjustedEnd = Math.Min(elementSpan.End, span.End);
            var adjustedSpan = TextSpan.FromBounds(adjustedStart, adjustedEnd);
            var classification = GetClassification(elementKind);

            var classifiedSpan = new ClassifiedSpan(adjustedSpan, classification);

            result.Add(classifiedSpan);
        }

        private static Classification GetClassification(SyntaxKind kind)
        {
            var isKeyword = kind.IsKeyword();
            var isIdentifier = kind == SyntaxKind.IdentifierToken;
            var isNumber = kind == SyntaxKind.NumberToken;
            var isString = IsString(kind);
            var isComment = kind.IsComment();
            var isPunctuation = IsPunctuation(kind);
            var isOperator = !isKeyword && !isIdentifier && !isNumber && !isString && !isComment && !isPunctuation && IsOperator(kind);

            if (isKeyword)
                return Classification.Keyword;
            else if (isIdentifier)
                return Classification.Identifier;
            else if (isNumber)
                return Classification.Number;
            else if (isString)
                return Classification.String;
            else if (isComment)
                return Classification.Comment;
            else if (isPunctuation)
                return Classification.Punctuation;
            else if (isOperator)
                return Classification.Operator;
            else
                return Classification.Text;
        }

        private static bool IsPunctuation(SyntaxKind kind)
        {
            return kind.IsOpenBracket() ||
                   kind.IsCloseBracket() ||
                   kind == SyntaxKind.DotToken ||
                   kind == SyntaxKind.CommaToken ||
                   kind == SyntaxKind.SemicolonToken ||
                   kind == SyntaxKind.ColonToken;
        }

        private static bool IsString(SyntaxKind kind)
        {
            return kind == SyntaxKind.StringToken ||
                   kind == SyntaxKind.VerbatimStringToken ||
                   kind == SyntaxKind.RawStringToken ||
                   kind == SyntaxKind.InterpolatedStringToken;
        }

        private static bool IsOperator(SyntaxKind kind)
        {
            return kind != SyntaxKind.EndOfFileToken &&
                   kind != SyntaxKind.BadToken;
        }
    }
}
