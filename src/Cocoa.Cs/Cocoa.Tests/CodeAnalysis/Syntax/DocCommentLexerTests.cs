using System.Linq;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    public class DocCommentLexerTests
    {
        [Fact]
        public void TripleSlash_ProducesDocCommentTrivia()
        {
            var tokens = SyntaxTree.ParseTokens("/// <summary>x</summary>\nfoo", includeEndOfFile: false);
            var token = tokens.First(t => t.Kind == SyntaxKind.IdentifierToken);

            var trivia = Assert.Single(token.LeadingTrivia, t => t.Kind == SyntaxKind.SingleLineDocCommentTrivia);
            Assert.Equal("/// <summary>x</summary>", trivia.Text);
        }

        [Fact]
        public void ConsecutiveDocLines_AllAttachToFollowingToken()
        {
            var tokens = SyntaxTree.ParseTokens("/// a\n/// b\nfoo", includeEndOfFile: false);
            var token = tokens.First(t => t.Kind == SyntaxKind.IdentifierToken);

            var docTrivia = token.LeadingTrivia.Where(t => t.Kind == SyntaxKind.SingleLineDocCommentTrivia).ToList();
            Assert.Equal(2, docTrivia.Count);
        }

        [Fact]
        public void FourSlashes_ProducesOrdinaryCommentTrivia()
        {
            var tokens = SyntaxTree.ParseTokens("//// not doc\nfoo", includeEndOfFile: false);
            var token = tokens.First(t => t.Kind == SyntaxKind.IdentifierToken);

            Assert.Contains(token.LeadingTrivia, t => t.Kind == SyntaxKind.SingleLineCommentTrivia);
            Assert.DoesNotContain(token.LeadingTrivia, t => t.Kind == SyntaxKind.SingleLineDocCommentTrivia);
        }

        [Fact]
        public void DocComment_IsTriviaAndComment()
        {
            Assert.True(SyntaxFacts.IsTrivia(SyntaxKind.SingleLineDocCommentTrivia));
            Assert.True(SyntaxFacts.IsComment(SyntaxKind.SingleLineDocCommentTrivia));
            Assert.False(SyntaxFacts.IsToken(SyntaxKind.SingleLineDocCommentTrivia));
        }
    }
}
