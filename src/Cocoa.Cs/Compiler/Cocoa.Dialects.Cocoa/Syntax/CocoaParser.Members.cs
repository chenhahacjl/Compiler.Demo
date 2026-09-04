using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    internal sealed partial class CocoaParser
    {
        // ==================== Members ====================

        // ==================== Members ====================

        private MemberSyntax ParseMember()
        {
            if (Current.Kind == SyntaxKind.ImportKeyword)
            {
                ReportError(Current.Location, "顶层 `import` 声明已废弃：请改用类内 import 块 `class Kernel32 { import kernel32.dll { static extern ... } }`。");

                return ParseImportClause();
            }

            if (Current.Kind == SyntaxKind.UsingKeyword)
            {
                return ParseUsingDirective();
            }

            if (Current.Kind == SyntaxKind.NamespaceKeyword)
            {
                return ParseNamespaceDeclaration();
            }

            var modifiers = ParseModifiers();

            if (Current.Kind == SyntaxKind.CdeclKeyword ||
                Current.Kind == SyntaxKind.StdcallKeyword ||
                Current.Kind == SyntaxKind.FunctionKeyword)
            {
                return ParseFunctionDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.EnumKeyword)
            {
                return ParseEnumDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.ClassKeyword)
            {
                return ParseClassDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.StructKeyword)
            {
                return ParseClassDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.InterfaceKeyword)
            {
                return ParseInterfaceDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.DelegateKeyword)
            {
                return ParseDelegateDeclaration(modifiers);
            }

            if (IsCSharpStyleTopLevelFunction())
            {
                ReportError(Current.Location, "Cocoa 顶层函数须用 function 关键字（如 `function Add(a: int, b: int): int`），不支持 C# 式 `返回类型 名称(...)`。");

                return ParseCSharpStyleTopLevelFunction(modifiers);
            }

            if (IsNoKeywordTopLevelFunction())
            {
                ReportError(Current.Location, "顶层函数须用 function 关键字（Cocoa）或带返回类型（C#），不支持无关键字写法（如 `Main(): void`）。");
                return ParseNoKeywordTopLevelFunction(modifiers);
            }

            if (modifiers.Any())
            {
                _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.FunctionKeyword);
            }

            return ParseGlobalStatement();
        }

        private bool IsCSharpStyleTopLevelFunction()
        {
            var offset = 0;
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken)
            {
                return false;
            }

            offset++;

            if (Peek(offset).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(offset);
                if (afterAngles < 0)
                {
                    return false;
                }

                offset = afterAngles;
            }

            while (Peek(offset).Kind == SyntaxKind.OpenBracketToken &&
                   Peek(offset + 1).Kind == SyntaxKind.CloseBracketToken)
            {
                offset += 2;
            }

            if (Peek(offset).Kind != SyntaxKind.IdentifierToken)
            {
                return false;
            }

            offset++;

            if (Peek(offset).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(offset);
                if (afterAngles < 0)
                {
                    return false;
                }

                offset = afterAngles;
            }

            return Peek(offset).Kind == SyntaxKind.OpenParenthesisToken;
        }

        private bool IsNoKeywordTopLevelFunction()
        {
            if (Current.Kind != SyntaxKind.IdentifierToken ||
                Peek(1).Kind != SyntaxKind.OpenParenthesisToken)
            {
                return false;
            }

            var depth = 0;
            for (var offset = 1; ; offset++)
            {
                var token = Peek(offset);
                if (token.Kind == SyntaxKind.EndOfFileToken)
                {
                    return false;
                }

                if (token.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    depth++;
                }
                else if (token.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    depth--;
                    if (depth == 0)
                    {
                        var next = Peek(offset + 1);
                        return next.Kind == SyntaxKind.OpenBraceToken || next.Kind == SyntaxKind.ColonToken || next.Kind == SyntaxKind.FatArrowToken;
                    }
                }
            }
        }

        private MemberSyntax ParseCSharpStyleTopLevelFunction(ImmutableArray<SyntaxToken> modifiers)
        {
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            return ParseCSharpStyleMethod(modifiers, type, identifier);
        }

        private MemberSyntax ParseNoKeywordTopLevelFunction(ImmutableArray<SyntaxToken> modifiers)
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var type = ParseOptionalTypeClause();

            BlockStatementSyntax? body;
            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                }

                body = SynthesizeExpressionBodyBlock(expression, arrow);
            }
            else
            {
                body = ParseBlockStatement();
            }

            return new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword: null, identifier, typeParameters: null, openParenthesisToken, parameters, closeParenthesisToken, type, body);
        }

        private ImmutableArray<SyntaxToken> ParseModifiers()
        {
            var modifiers = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (IsModifier(Current.Kind))
            {
                modifiers.Add(NextToken());
            }

            return modifiers.ToImmutable();
        }

        private static bool IsModifier(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.PrivateKeyword:
                case SyntaxKind.InternalKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.CdeclKeyword:
                case SyntaxKind.StdcallKeyword:
                case SyntaxKind.SyscallKeyword:
                case SyntaxKind.AbstractKeyword:
                case SyntaxKind.SealedKeyword:
                case SyntaxKind.StaticKeyword:
                case SyntaxKind.VirtualKeyword:
                case SyntaxKind.OverrideKeyword:
                case SyntaxKind.ReadonlyKeyword:
                case SyntaxKind.PartialKeyword:
                    return true;
                case SyntaxKind.FacadeKeyword:
                    return true;
                default:
                    return false;
            }
        }

        private MemberSyntax ParseEnumDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var enumKeyword = MatchToken(SyntaxKind.EnumKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseEnumMemberList();
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new EnumDeclarationSyntax(_syntaxTree, modifiers, enumKeyword, identifier, openBraceToken, members, closeBraceToken);
        }

        private SeparatedSyntaxList<EnumMemberSyntax> ParseEnumMemberList()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextMember = true;
            while (parseNextMember &&
                Current.Kind != SyntaxKind.CloseBraceToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var member = ParseEnumMember();
                nodesAndSeparators.Add(member);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextMember = false;
                }
            }

            return new SeparatedSyntaxList<EnumMemberSyntax>(nodesAndSeparators.ToImmutable());
        }

        private EnumMemberSyntax ParseEnumMember()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? value = null;

            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                value = ParseExpression();
            }

            return new EnumMemberSyntax(_syntaxTree, identifier, equalsToken, value);
        }

        private MemberSyntax ParseImportClause()
        {
            var importKeyword = MatchToken(SyntaxKind.ImportKeyword);
            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

            nameTokens.Add(MatchToken(SyntaxKind.IdentifierToken));

            while (Current.Kind == SyntaxKind.DotToken)
            {
                nameTokens.Add(MatchToken(SyntaxKind.DotToken));
                nameTokens.Add(MatchToken(SyntaxKind.IdentifierToken));
            }

            return new ImportClauseSyntax(_syntaxTree, importKeyword, nameTokens.ToImmutable());
        }

        private MemberSyntax ParseImportBlock()
        {
            var importKeyword = MatchToken(SyntaxKind.ImportKeyword);
            var nameTokens = ParseQualifiedName();

            SyntaxToken? blockCharsetKey = null;
            SyntaxToken? blockCharsetValue = null;
            SyntaxToken? blockOpenParen = null;
            SyntaxToken? blockCloseParen = null;

            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                blockOpenParen = NextToken();
            }

            if (Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.EqualsToken &&
                Current.Text == "charset")
            {
                blockCharsetKey = NextToken();
                MatchToken(SyntaxKind.EqualsToken);
                blockCharsetValue = MatchToken(SyntaxKind.IdentifierToken);
            }

            if (blockOpenParen != null)
            {
                blockCloseParen = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            var members = ImmutableArray.CreateBuilder<MemberSyntax>();
            while (Current.Kind != SyntaxKind.CloseBraceToken &&
                   Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                members.Add(ParseClassMember(""));
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new ImportBlockSyntax(_syntaxTree, importKeyword, nameTokens, blockOpenParen, blockCharsetKey, blockCharsetValue, blockCloseParen, openBraceToken, members.ToImmutable(), closeBraceToken);
        }

        private MemberSyntax ParseUsingDirective()
        {
            return ParseUsingDirectiveCore();
        }

        private MemberSyntax ParseUsingDirectiveCore()
        {
            var usingKeyword = MatchToken(SyntaxKind.UsingKeyword);
            SyntaxToken? staticKeyword = null;
            SyntaxToken? aliasToken = null;
            SyntaxToken? equalsToken = null;

            if (Current.Kind == SyntaxKind.StaticKeyword)
            {
                staticKeyword = MatchToken(SyntaxKind.StaticKeyword);
            }

            if (staticKeyword == null &&
                Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.EqualsToken)
            {
                aliasToken = MatchToken(SyntaxKind.IdentifierToken);
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
            }

            var nameTokens = ParseQualifiedName();

            return new UsingDirectiveSyntax(_syntaxTree, usingKeyword, staticKeyword, aliasToken, equalsToken, nameTokens);
        }

        private MemberSyntax ParseNamespaceDeclaration()
        {
            var namespaceKeyword = MatchToken(SyntaxKind.NamespaceKeyword);
            var nameTokens = ParseQualifiedName();

            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken();
                var members = ParseMembers();
                var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, namespaceKeyword.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, Current.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);

                return new NamespaceDeclarationSyntax(_syntaxTree, namespaceKeyword, nameTokens, openBrace, members, closeBrace);
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var namespaceMembers = ImmutableArray.CreateBuilder<MemberSyntax>();

            while (Current.Kind != SyntaxKind.CloseBraceToken &&
                   Current.Kind != SyntaxKind.EndOfFileToken)
            {
                namespaceMembers.Add(ParseMember());
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new NamespaceDeclarationSyntax(_syntaxTree, namespaceKeyword, nameTokens, openBraceToken, namespaceMembers.ToImmutable(), closeBraceToken);
        }

        private ImmutableArray<SyntaxToken> ParseQualifiedName()
        {
            var nameTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

            nameTokens.Add(MatchToken(SyntaxKind.IdentifierToken));

            while (Current.Kind == SyntaxKind.DotToken)
            {
                nameTokens.Add(MatchToken(SyntaxKind.DotToken));
                nameTokens.Add(MatchToken(SyntaxKind.IdentifierToken));
            }

            return nameTokens.ToImmutable();
        }

        private MemberSyntax ParseFunctionDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var functionKeyword = MatchToken(SyntaxKind.FunctionKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var type = ParseOptionalTypeClause();

            var externMetadata = ParseOptionalExternMetadata();

            var whereClauses = ParseWhereClauses();

            BlockStatementSyntax? body = null;

            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                }

                body = SynthesizeExpressionBodyBlock(expression, arrow);
            }
            else
            {
                var isExtern = modifiers.Any(m => m.Kind == SyntaxKind.CdeclKeyword || m.Kind == SyntaxKind.StdcallKeyword) || externMetadata != null;
                var isAbstract = modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword);
                var isSyscall = modifiers.Any(m => m.Kind == SyntaxKind.SyscallKeyword);
                if ((!isExtern && !isAbstract && !isSyscall) || Current.Kind == SyntaxKind.OpenBraceToken)
                {
                    body = ParseBlockStatement();
                }
            }

            return new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword, identifier, typeParameters, openParenthesisToken, parameters, closeParenthesisToken, type, body, externMetadata, whereClauses);
        }

        private ExternMetadataSyntax? ParseOptionalExternMetadata()
        {
            if (Current.Kind != SyntaxKind.ExternKeyword)
            {
                return null;
            }

            var externKeyword = NextToken();
            SyntaxToken? openParen = null;
            SyntaxToken? closeParen = null;

            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParen = NextToken();
            }

            var arguments = ImmutableArray.CreateBuilder<ExternMetadataArgumentSyntax>();
            while (Current.Kind != SyntaxKind.CloseParenthesisToken &&
                   Current.Kind != SyntaxKind.EndOfFileToken &&
                   (openParen != null || Current.Kind != SyntaxKind.OpenBraceToken))
            {
                var key = MatchToken(SyntaxKind.IdentifierToken);
                var equalsToken = MatchToken(SyntaxKind.EqualsToken);
                var value = MatchToken(SyntaxKind.IdentifierToken);
                arguments.Add(new ExternMetadataArgumentSyntax(_syntaxTree, key, equalsToken, value));

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                }
                else
                {
                    break;
                }
            }

            if (openParen != null)
            {
                closeParen = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            return new ExternMetadataSyntax(_syntaxTree, externKeyword, openParen, arguments.ToImmutable(), closeParen);
        }

        private MemberSyntax ParseClassDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var classKeyword = Current.Kind == SyntaxKind.StructKeyword
                ? MatchToken(SyntaxKind.StructKeyword)
                : MatchToken(SyntaxKind.ClassKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                if (Current.Kind == SyntaxKind.ColonToken)
                {
                    ReportError(Current.Location, "Cocoa 继承/基接口须用 extends 关键字，不支持冒号 `:`。");
                }

                var prefixToken = NextToken();
                baseTypes.Add(CreateBaseTypeClause(prefixToken));

                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    baseTypes.Add(CreateBaseTypeClause(null));
                }
            }

            var whereClauses = ParseWhereClauses();

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseClassMemberList(identifier.Text);
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new ClassDeclarationSyntax(_syntaxTree, modifiers, classKeyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses, openBraceToken, members, closeBraceToken);
        }

        private ImmutableArray<MemberSyntax> ParseClassMemberList(string className)
        {
            var members = ImmutableArray.CreateBuilder<MemberSyntax>();

            while (Current.Kind != SyntaxKind.CloseBraceToken &&
                   Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                members.Add(ParseClassMember(className));
            }

            return members.ToImmutable();
        }

        private MemberSyntax ParseClassMember(string className)
        {
            if (Current.Kind == SyntaxKind.ImportKeyword)
            {
                return ParseImportBlock();
            }

            var modifiers = ParseModifiers();

            if (Current.Kind == SyntaxKind.ConstructorKeyword)
            {
                return ParseConstructorDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.CdeclKeyword ||
                Current.Kind == SyntaxKind.StdcallKeyword ||
                Current.Kind == SyntaxKind.FunctionKeyword)
            {
                return ParseFunctionDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.EventKeyword)
            {
                return ParseEventDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.DelegateKeyword)
            {
                return ParseDelegateDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.PropertyKeyword)
            {
                return ParsePropertyDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                if (Peek(1).Kind == SyntaxKind.ColonToken)
                {
                    return ParseClassFieldDeclaration(modifiers);
                }

                ReportError(Current.Location, "Cocoa 类成员须用 function/property/constructor 关键字且类型后置，不支持 C# 式 `类型 名称(...)`。");
                return ParseCSharpStyleMember(modifiers, className);
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
            var badColon = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, ":", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badType = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, Current.Text, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badMember = new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, Current, new TypeClauseSyntax(_syntaxTree, badColon, badType));
            NextToken();
            return badMember;
        }

        private MemberSyntax ParseCSharpStyleMember(ImmutableArray<SyntaxToken> modifiers, string className)
        {
            if (Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.OpenParenthesisToken &&
                Current.Text == className)
            {
                return ParseCSharpStyleConstructor(modifiers);
            }

            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            if (Current.Kind == SyntaxKind.LessToken)
            {
                return ParseCSharpStyleMethod(modifiers, type, identifier);
            }

            switch (Current.Kind)
            {
                case SyntaxKind.SemicolonToken:
                {
                    MatchToken(SyntaxKind.SemicolonToken);
                    return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type);
                }

                case SyntaxKind.EqualsToken:
                {
                    var equalsToken = MatchToken(SyntaxKind.EqualsToken);
                    var initializer = ParseExpression();
                    return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type, equalsToken, initializer);
                }

                case SyntaxKind.OpenBraceToken:
                case SyntaxKind.FatArrowToken:
                    return ParseCSharpStyleProperty(modifiers, type, identifier);

                case SyntaxKind.OpenParenthesisToken:
                    return ParseCSharpStyleMethod(modifiers, type, identifier);

                default:
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.SemicolonToken);
                    return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type);
            }
        }

        private MemberSyntax ParseCSharpStyleConstructor(ImmutableArray<SyntaxToken> modifiers)
        {
            MatchToken(SyntaxKind.IdentifierToken);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                NextToken();
                if (Current.Kind == SyntaxKind.BaseKeyword || Current.Kind == SyntaxKind.ThisKeyword)
                {
                    initializerKeyword = NextToken();
                    var openParen = MatchToken(SyntaxKind.OpenParenthesisToken);
                    initializerArguments = ParseArgumentList();
                    MatchToken(SyntaxKind.CloseParenthesisToken);
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.BaseKeyword);
                }
            }

            var body = ParseBlockStatement();

            return new ConstructorDeclarationSyntax(_syntaxTree, modifiers, constructorKeyword: null, openParenthesisToken, parameters, closeParenthesisToken, initializerKeyword, initializerArguments, body);
        }

        private MemberSyntax ParseCSharpStyleMethod(ImmutableArray<SyntaxToken> modifiers, TypeClauseSyntax type, SyntaxToken identifier)
        {
            var typeParameters = ParseOptionalTypeParameterList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            var whereClauses = ParseWhereClauses();

            BlockStatementSyntax? body = null;
            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                body = ParseBlockStatement();
            }
            else if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken();
            }
            else if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                }

                body = SynthesizeExpressionBodyBlock(expression, arrow);
            }

            return new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword: null, identifier, typeParameters, openParenthesisToken, parameters, closeParenthesisToken, type, body, whereClauses: whereClauses);
        }

        private MemberSyntax ParseCSharpStyleProperty(ImmutableArray<SyntaxToken> modifiers, TypeClauseSyntax type, SyntaxToken identifier)
        {
            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                }

                return SynthesizeExpressionBodyProperty(modifiers, propertyKeyword: null, identifier, type, arrow, expression);
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            PropertyAccessorSyntax? getter = null;
            PropertyAccessorSyntax? setter = null;
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (IsModifier(Current.Kind) || Current.Kind == SyntaxKind.GetKeyword || Current.Kind == SyntaxKind.SetKeyword)
                {
                    var accessor = ParsePropertyAccessor();
                    if (accessor.IsGet)
                    {
                        getter = accessor;
                    }
                    else
                    {
                        setter = accessor;
                    }
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GetKeyword);
                    NextToken();
                }
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword: null, identifier, type, openBraceToken, getter, setter, closeBraceToken, ImmutableArray<ParameterSyntax>.Empty, equalsToken, initializer);
        }

        private TypeClauseSyntax ParsePrefixTypeClause()
        {
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

        private MemberSyntax ParseConstructorDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var constructorKeyword = MatchToken(SyntaxKind.ConstructorKeyword);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                NextToken();
                if (Current.Kind == SyntaxKind.BaseKeyword || Current.Kind == SyntaxKind.ThisKeyword)
                {
                    initializerKeyword = NextToken();
                    var openParen = MatchToken(SyntaxKind.OpenParenthesisToken);
                    initializerArguments = ParseArgumentList();
                    MatchToken(SyntaxKind.CloseParenthesisToken);
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.BaseKeyword);
                }
            }

            var body = ParseBlockStatement();

            return new ConstructorDeclarationSyntax(_syntaxTree, modifiers, constructorKeyword, openParenthesisToken, parameters, closeParenthesisToken, initializerKeyword, initializerArguments, body);
        }

        private MemberSyntax ParseInterfaceDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var interfaceKeyword = MatchToken(SyntaxKind.InterfaceKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                if (Current.Kind == SyntaxKind.ColonToken)
                {
                    ReportError(Current.Location, "Cocoa 继承/基接口须用 extends 关键字，不支持冒号 `:`。");
                }

                var prefixToken = NextToken();
                baseTypes.Add(CreateBaseTypeClause(prefixToken));

                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    baseTypes.Add(CreateBaseTypeClause(null));
                }
            }

            var whereClauses = ParseWhereClauses();

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseInterfaceMemberList();
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new InterfaceDeclarationSyntax(_syntaxTree, modifiers, interfaceKeyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses, openBraceToken, members, closeBraceToken);
        }

        private ImmutableArray<MemberSyntax> ParseInterfaceMemberList()
        {
            var members = ImmutableArray.CreateBuilder<MemberSyntax>();

            while (Current.Kind != SyntaxKind.CloseBraceToken &&
                   Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                var modifiers = ParseModifiers();

                if (Current.Kind == SyntaxKind.CdeclKeyword ||
                    Current.Kind == SyntaxKind.StdcallKeyword ||
                    Current.Kind == SyntaxKind.FunctionKeyword)
                {
                    var functionKeyword = MatchToken(SyntaxKind.FunctionKeyword);
                    var memberIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                    var memberTypeParameters = ParseOptionalTypeParameterList();
                    var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                    var parameters = ParseParameterList();
                    var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                    var type = ParseOptionalTypeClause();
                    var memberWhereClauses = ParseWhereClauses();
                    members.Add(new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword, memberIdentifier, memberTypeParameters, openParenthesisToken, parameters, closeParenthesisToken, type, body: null, whereClauses: memberWhereClauses));
                }
                else if (Current.Kind == SyntaxKind.PropertyKeyword)
                {
                    members.Add(ParsePropertyDeclaration(modifiers));
                }
                else if (Current.Kind == SyntaxKind.IdentifierToken &&
                         (Peek(1).Kind == SyntaxKind.IdentifierToken ||
                          (Peek(1).Kind == SyntaxKind.LessToken && IsGenericTypeNameAhead())))
                {
                    ReportError(Current.Location, "Cocoa 接口成员须用 function/property 关键字且类型后置，不支持 C# 式 `类型 名称`。");

                    var type = ParsePrefixTypeClause();
                    var memberIdentifier = MatchToken(SyntaxKind.IdentifierToken);

                    if (Current.Kind == SyntaxKind.OpenBraceToken)
                    {
                        members.Add(ParseCSharpStyleProperty(modifiers, type, memberIdentifier));
                    }
                    else
                    {
                    var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                    var parameters = ParseParameterList();
                    var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                    var csMemberWhereClauses = ParseWhereClauses();
                    if (Current.Kind == SyntaxKind.SemicolonToken)
                    {
                        NextToken();
                    }

                    members.Add(new FunctionDeclarationSyntax(_syntaxTree, modifiers, functionKeyword: null, memberIdentifier, typeParameters: null, openParenthesisToken, parameters, closeParenthesisToken, type, body: null, whereClauses: csMemberWhereClauses));
                    }
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.FunctionKeyword);
                    NextToken();
                }
            }

            return members.ToImmutable();
        }

        private MemberSyntax ParseClassFieldDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var type = ParseTypeClause();

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, identifier, type, equalsToken, initializer);
        }

        private MemberSyntax ParseEventDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var eventKeyword = MatchToken(SyntaxKind.EventKeyword);

            var isCocoaForm = Current.Kind == SyntaxKind.IdentifierToken &&
                              Peek(1).Kind == SyntaxKind.ColonToken;

            SyntaxToken identifier;
            TypeClauseSyntax handlerType;

            if (isCocoaForm)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                handlerType = ParseTypeClause();
            }
            else
            {
                handlerType = ParsePrefixTypeClause();
                identifier = MatchToken(SyntaxKind.IdentifierToken);
            }

            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken();
            }

            return new EventDeclarationSyntax(_syntaxTree, modifiers, eventKeyword, identifier, handlerType);
        }

        private MemberSyntax ParseDelegateDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var delegateKeyword = MatchToken(SyntaxKind.DelegateKeyword);

            var isCoForm = Current.Kind == SyntaxKind.IdentifierToken &&
                           Peek(1).Kind == SyntaxKind.OpenParenthesisToken;

            SyntaxToken identifier;
            SeparatedSyntaxList<ParameterSyntax> parameters;
            TypeClauseSyntax? returnType = null;
            SyntaxToken openParenToken;
            SyntaxToken closeParenToken;
            SyntaxToken? semicolonToken = null;

            if (isCoForm)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                parameters = ParseParameterList();
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                if (Current.Kind == SyntaxKind.ColonToken)
                    returnType = ParseTypeClause();
            }
            else
            {
                if (!(Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken))
                    returnType = ParsePrefixTypeClause();

                identifier = MatchToken(SyntaxKind.IdentifierToken);
                openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                parameters = ParseParameterList();
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                if (Current.Kind == SyntaxKind.SemicolonToken)
                    semicolonToken = MatchToken(SyntaxKind.SemicolonToken);
            }

            return new DelegateDeclarationSyntax(_syntaxTree, modifiers, delegateKeyword, returnType, identifier, openParenToken, parameters, closeParenToken, semicolonToken);
        }

        private MemberSyntax ParsePropertyDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var propertyKeyword = MatchToken(SyntaxKind.PropertyKeyword);
            var identifier = Current.Kind == SyntaxKind.ThisKeyword
                ? MatchToken(SyntaxKind.ThisKeyword)
                : MatchToken(SyntaxKind.IdentifierToken);

            if (identifier.Text == "this" && Current.Kind == SyntaxKind.OpenBracketToken)
            {
                return ParseIndexerDeclaration(modifiers, propertyKeyword, identifier);
            }

            var type = ParseTypeClause();

            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                var arrow = NextToken();
                var expression = ParseExpression();
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                }

                return SynthesizeExpressionBodyProperty(modifiers, propertyKeyword, identifier, type, arrow, expression);
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            PropertyAccessorSyntax? getter = null;
            PropertyAccessorSyntax? setter = null;
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (IsModifier(Current.Kind) || Current.Kind == SyntaxKind.GetKeyword || Current.Kind == SyntaxKind.SetKeyword)
                {
                    var accessor = ParsePropertyAccessor();
                    if (accessor.IsGet)
                    {
                        getter = accessor;
                    }
                    else
                    {
                        setter = accessor;
                    }
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GetKeyword);
                    NextToken();
                }
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword, identifier, type, openBraceToken, getter, setter, closeBraceToken, ImmutableArray<ParameterSyntax>.Empty, equalsToken, initializer);
        }

        private PropertyAccessorSyntax ParsePropertyAccessor()
        {
            var modifiers = ParseModifiers();

            SyntaxToken keyword;
            if (Current.Kind == SyntaxKind.GetKeyword || Current.Kind == SyntaxKind.SetKeyword)
            {
                keyword = NextToken();
            }
            else
            {
                _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GetKeyword);
                keyword = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, Current.Text, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            }

            BlockStatementSyntax? body = null;
            SyntaxToken? semicolonToken = null;

            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                body = ParseBlockStatement();
            }
            else if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                semicolonToken = MatchToken(SyntaxKind.SemicolonToken);
            }

            return new PropertyAccessorSyntax(_syntaxTree, modifiers, keyword, body, semicolonToken);
        }

        private BlockStatementSyntax SynthesizeExpressionBodyBlock(ExpressionSyntax expression, SyntaxToken arrow)
        {
            var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, arrow.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var returnKeyword = new SyntaxToken(_syntaxTree, SyntaxKind.ReturnKeyword, arrow.Position, "return", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, arrow.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);

            var returnStatement = new ReturnStatementSyntax(_syntaxTree, returnKeyword, expression);
            return new BlockStatementSyntax(_syntaxTree, openBrace, ImmutableArray.Create<StatementSyntax>(returnStatement), closeBrace);
        }

        private PropertyDeclarationSyntax SynthesizeExpressionBodyProperty(ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken arrow, ExpressionSyntax expression)
        {
            var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, arrow.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, arrow.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var getKeyword = new SyntaxToken(_syntaxTree, SyntaxKind.GetKeyword, arrow.Position, "get", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var getter = new PropertyAccessorSyntax(_syntaxTree, ImmutableArray<SyntaxToken>.Empty, getKeyword, SynthesizeExpressionBodyBlock(expression, arrow), semicolonToken: null);

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword, identifier, type, openBrace, getter, setter: null, closeBrace);
        }

        private MemberSyntax ParseIndexerDeclaration(ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier)
        {
            NextToken();
            var builder = ImmutableArray.CreateBuilder<ParameterSyntax>();
            if (Current.Kind != SyntaxKind.CloseBracketToken)
            {
                builder.Add(ParseParameter());
                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken();
                    builder.Add(ParseParameter());
                }
            }

            MatchToken(SyntaxKind.CloseBracketToken);
            var type = ParseTypeClause();

            if (Current.Kind == SyntaxKind.FatArrowToken)
            {
                _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.OpenBraceToken);
                NextToken();
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            PropertyAccessorSyntax? getter = null;
            PropertyAccessorSyntax? setter = null;
            while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (IsModifier(Current.Kind) || Current.Kind == SyntaxKind.GetKeyword || Current.Kind == SyntaxKind.SetKeyword)
                {
                    var accessor = ParsePropertyAccessor();
                    if (accessor.IsGet)
                    {
                        getter = accessor;
                    }
                    else
                    {
                        setter = accessor;
                    }
                }
                else
                {
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GetKeyword);
                    NextToken();
                }
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword, identifier, type, openBraceToken, getter, setter, closeBraceToken, builder.ToImmutable(), equalsToken, initializer);
        }

        private SeparatedSyntaxList<ParameterSyntax> ParseParameterList()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextParameter = true;
            while (parseNextParameter &&
                Current.Kind != SyntaxKind.CloseParenthesisToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var parameter = ParseParameter();
                nodesAndSeparators.Add(parameter);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextParameter = false;
                }
            }

            return new SeparatedSyntaxList<ParameterSyntax>(nodesAndSeparators.ToImmutable());
        }

        private ParameterSyntax ParseParameter()
        {
            SyntaxToken? modifier = null;
            if (Current.Kind == SyntaxKind.OutKeyword || Current.Kind == SyntaxKind.RefKeyword)
            {
                modifier = MatchToken(Current.Kind);
            }

            if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.ColonToken)
            {
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var type = ParseTypeClause();

                return new ParameterSyntax(_syntaxTree, modifier, identifier, type);
            }

            ReportError(Current.Location, "Cocoa 参数须为 `名称: 类型`（类型后置），不支持 C# 式 `类型 名称`。");
            var csType = ParsePrefixTypeClause();
            var csIdentifier = MatchToken(SyntaxKind.IdentifierToken);

            return new ParameterSyntax(_syntaxTree, modifier, csIdentifier, csType);
        }
    }
}
