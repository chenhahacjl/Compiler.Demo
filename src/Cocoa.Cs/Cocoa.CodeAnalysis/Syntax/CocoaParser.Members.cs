using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
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

            var attributes = ParseOptionalAttributes();

            var modifiers = ParseModifiers();

            // Tier-2：attribute 支持类/结构体/函数/枚举声明位（字段/属性在类体内单独处理）
            if (attributes.Length > 0)
            {
                var isSupported = Current.Kind == SyntaxKind.ClassKeyword ||
                    Current.Kind == SyntaxKind.StructKeyword ||
                    Current.Kind == SyntaxKind.InterfaceKeyword ||
                    Current.Kind == SyntaxKind.CdeclKeyword ||
                    Current.Kind == SyntaxKind.StdcallKeyword ||
                    Current.Kind == SyntaxKind.FunctionKeyword ||
                    Current.Kind == SyntaxKind.OperatorKeyword ||
                    Current.Kind == SyntaxKind.ImplicitKeyword ||
                    Current.Kind == SyntaxKind.ExplicitKeyword ||
                    Current.Kind == SyntaxKind.EnumKeyword;
                if (!isSupported)
                {
                    ReportError(Current.Location, "attribute 目前仅支持类/结构体/接口/函数/运算符/枚举声明（如 `[Test] function X(): void`）。");
                }
            }

            if (Current.Kind == SyntaxKind.CdeclKeyword ||
                Current.Kind == SyntaxKind.StdcallKeyword ||
                Current.Kind == SyntaxKind.FunctionKeyword ||
                Current.Kind == SyntaxKind.OperatorKeyword ||
                Current.Kind == SyntaxKind.ImplicitKeyword ||
                Current.Kind == SyntaxKind.ExplicitKeyword)
            {
                return ParseFunctionDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.EnumKeyword)
            {
                return ParseEnumDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.IdentifierToken && Current.Text == "record")
            {
                return ParseRecordDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.ClassKeyword)
            {
                return ParseClassDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.StructKeyword)
            {
                return ParseClassDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.InterfaceKeyword)
            {
                return ParseInterfaceDeclaration(attributes, modifiers);
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

        /// <summary>record（语言后置件 7/8）：`record 名称(位置参数)` 展开为等价类声明——
        /// 公共字段 + 构造器（this.字段 = 参数）+ Equals（字段逐比较 &amp;&amp;）+ ToString（"名 { 字段 = 值 }" 拼接），
        /// 复用既有类绑定/发射，零 binder 改动。</summary>
        private MemberSyntax ParseRecordDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var recordToken = MatchToken(SyntaxKind.IdentifierToken);
            var nameToken = MatchToken(SyntaxKind.IdentifierToken);

            // record 只支持位置参数形式 `record Name(params)`。
            // 缺括号时若继续走，MatchToken 不消费 token，参数列表会把 `{ … }` 当参数吃掉，
            // 随后成员循环在已消费的 token 上原地打转 → parser 死循环（挂死远重于报错）。故显式拒绝并收尾。
            if (Current.Kind != SyntaxKind.OpenParenthesisToken)
            {
                ReportError(Current.Location,
                    "record 声明须为位置参数形式，如 `record Point(x: i32, y: i32)`。");

                var badOpen = SyntheticToken(SyntaxKind.OpenParenthesisToken, Current.Position, "(");
                var badClose = SyntheticToken(SyntaxKind.CloseParenthesisToken, Current.Position, ")");
                var members = ImmutableArray.CreateBuilder<MemberSyntax>();
                ParseRecordBodyMembers(members, out var bodyOpen, out var bodyClose, out _);
                return new ClassDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers,
                    SyntheticToken(SyntaxKind.ClassKeyword, recordToken.Span.Start, "class"), nameToken, null,
                    ImmutableArray<TypeClauseSyntax>.Empty, ImmutableArray<WhereClauseSyntax>.Empty,
                    bodyOpen, members.ToImmutable(), bodyClose);
            }

            var openParen = NextToken();
            var parameters = ParseParameterList();
            var closeParen = MatchToken(SyntaxKind.CloseParenthesisToken);

            var positionalMembers = ImmutableArray.CreateBuilder<MemberSyntax>();
            ExpandRecordPositionalMembers(nameToken, parameters, positionalMembers);
            ParseRecordBodyMembers(positionalMembers, out var openBrace, out var closeBrace, out _);

            var classKeyword = SyntheticToken(SyntaxKind.ClassKeyword, recordToken.Span.Start, "class");
            return new ClassDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, classKeyword, nameToken, null, ImmutableArray<TypeClauseSyntax>.Empty, ImmutableArray<WhereClauseSyntax>.Empty, openBrace, positionalMembers.ToImmutable(), closeBrace);
        }

        /// <summary>
        /// record 体（`{ … }`，可省略）：逐成员解析。带**不推进兜底**——
        /// <c>ParseMember</c> 若在某些 token 上不消费 token，循环会原地打转导致 parser 死循环。
        /// </summary>
        private void ParseRecordBodyMembers(
            ImmutableArray<MemberSyntax>.Builder members,
            out SyntaxToken openBrace,
            out SyntaxToken closeBrace,
            out bool hadBody)
        {
            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                openBrace = NextToken();
                while (Current.Kind != SyntaxKind.CloseBraceToken && Current.Kind != SyntaxKind.EndOfFileToken)
                {
                    var before = Current;
                    members.Add(ParseMember());
                    if (Current == before)
                    {
                        // 不推进则强制前进一个 token，避免死循环
                        NextToken();
                    }
                }

                closeBrace = MatchToken(SyntaxKind.CloseBraceToken);
                hadBody = true;
                return;
            }

            var synthetic = SyntheticToken(SyntaxKind.OpenBraceToken, Current.Position, "{");
            openBrace = synthetic;
            closeBrace = SyntheticToken(SyntaxKind.CloseBraceToken, Current.Position, "}");
            hadBody = false;
        }

        private SyntaxToken SyntheticToken(SyntaxKind kind, int position, string text, object? value = null)
            => new SyntaxToken(_syntaxTree, kind, position, text, value, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);

        /// <summary>
        /// 为 record 合成 <c>operator ==</c> / <c>operator !=</c> 声明（逐字段比较）。
        /// 形态为标准的 <c>public static function operator ==(a: P, b: P): bool</c>——
        /// 复用既有运算符绑定/发射管线（OperatorRegistry + <c>op_Equality</c>/<c>op_Inequality</c>），
        /// 故 <c>a == b</c> 会优先命中它而非类引用相等。
        /// </summary>
        private FunctionDeclarationSyntax BuildRecordComparisonOperator(
            int pos,
            ImmutableArray<SyntaxToken> publicMods,
            SyntaxToken nameToken,
            SeparatedSyntaxList<ParameterSyntax> parameters,
            bool isEquals)
        {
            var selfType = new TypeClauseSyntax(_syntaxTree, null, nameToken);
            var aParam = new ParameterSyntax(_syntaxTree, null,
                SyntheticToken(SyntaxKind.IdentifierToken, pos, "a"), selfType);
            var bParam = new ParameterSyntax(_syntaxTree, null,
                SyntheticToken(SyntaxKind.IdentifierToken, pos, "b"), selfType);

            // a.f == b.f && …
            ExpressionSyntax? chain = null;
            foreach (var p in parameters)
            {
                var left = new MemberAccessExpressionSyntax(_syntaxTree,
                    new NameExpressionSyntax(_syntaxTree, aParam.Identifier), SyntheticToken(SyntaxKind.DotToken, pos, "."), p.Identifier);
                var right = new MemberAccessExpressionSyntax(_syntaxTree,
                    new NameExpressionSyntax(_syntaxTree, bParam.Identifier), SyntheticToken(SyntaxKind.DotToken, pos, "."), p.Identifier);
                var eq = new BinaryExpressionSyntax(_syntaxTree, left, SyntheticToken(SyntaxKind.EqualsEqualsToken, pos, "=="), right);
                chain = chain == null ? eq : new BinaryExpressionSyntax(_syntaxTree, chain, SyntheticToken(SyntaxKind.AmpersandAmpersandToken, pos, "&&"), eq);
            }

            // != 取反：!(a.f == b.f && …)
            ExpressionSyntax? result = chain;
            if (!isEquals && result != null)
            {
                result = new UnaryExpressionSyntax(_syntaxTree, SyntheticToken(SyntaxKind.BangToken, pos, "!"), result);
            }

            var body = new BlockStatementSyntax(_syntaxTree,
                SyntheticToken(SyntaxKind.OpenBraceToken, pos, "{"),
                ImmutableArray.Create<StatementSyntax>(new ReturnStatementSyntax(
                    _syntaxTree, SyntheticToken(SyntaxKind.ReturnKeyword, pos, "return"), result)),
                SyntheticToken(SyntaxKind.CloseBraceToken, pos, "}"));

            var staticMods = publicMods.Add(SyntheticToken(SyntaxKind.StaticKeyword, pos, "static"));
            // SeparatedSyntaxList 是「项/分隔符交错」布局：[a, ',', b]
            var paramList = new SeparatedSyntaxList<ParameterSyntax>(ImmutableArray.Create<SyntaxNode>(
                aParam,
                SyntheticToken(SyntaxKind.CommaToken, pos, ","),
                bParam));

            return new FunctionDeclarationSyntax(
                _syntaxTree, ImmutableArray<AttributeSyntax>.Empty, staticMods,
                SyntheticToken(SyntaxKind.OperatorKeyword, pos, "operator"),
                SyntheticToken(isEquals ? SyntaxKind.EqualsEqualsToken : SyntaxKind.BangEqualsToken, pos,
                    isEquals ? "==" : "!="),
                null,
                SyntheticToken(SyntaxKind.OpenParenthesisToken, pos, "("), paramList,
                SyntheticToken(SyntaxKind.CloseParenthesisToken, pos, ")"),
                new TypeClauseSyntax(_syntaxTree, null, SyntheticToken(SyntaxKind.IdentifierToken, pos, "bool")),
                body);
        }

        private void ExpandRecordPositionalMembers(SyntaxToken nameToken, SeparatedSyntaxList<ParameterSyntax> parameters, ImmutableArray<MemberSyntax>.Builder members)
        {
            var pos = nameToken.Span.Start;
            var publicMods = ImmutableArray.Create(SyntheticToken(SyntaxKind.PublicKeyword, pos, "public"));

            foreach (var p in parameters)
            {
                members.Add(new ClassFieldDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, publicMods, p.Identifier, p.Type));
            }

            var ctorStatements = ImmutableArray.CreateBuilder<StatementSyntax>();
            foreach (var p in parameters)
            {
                var thisAccess = new MemberAccessExpressionSyntax(_syntaxTree,
                    new ThisExpressionSyntax(_syntaxTree, SyntheticToken(SyntaxKind.ThisKeyword, pos, "this")),
                    SyntheticToken(SyntaxKind.DotToken, pos, "."), p.Identifier);
                var assign = new AssignmentExpressionSyntax(_syntaxTree, thisAccess, SyntheticToken(SyntaxKind.EqualsToken, pos, "="), new NameExpressionSyntax(_syntaxTree, p.Identifier));
                ctorStatements.Add(new ExpressionStatementSyntax(_syntaxTree, assign));
            }

            var ctorBody = new BlockStatementSyntax(_syntaxTree, SyntheticToken(SyntaxKind.OpenBraceToken, pos, "{"), ctorStatements.ToImmutable(), SyntheticToken(SyntaxKind.CloseBraceToken, pos, "}"));
            members.Add(new ConstructorDeclarationSyntax(_syntaxTree, publicMods,
                SyntheticToken(SyntaxKind.ConstructorKeyword, pos, "constructor"),
                SyntheticToken(SyntaxKind.OpenParenthesisToken, pos, "("), parameters,
                SyntheticToken(SyntaxKind.CloseParenthesisToken, pos, ")"), initializerKeyword: null,
                new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty), ctorBody));

            if (parameters.Count > 0)
            {
                var otherParam = new ParameterSyntax(_syntaxTree, modifier: null, SyntheticToken(SyntaxKind.IdentifierToken, pos, "other"), new TypeClauseSyntax(_syntaxTree, null, nameToken));
                var otherList = new SeparatedSyntaxList<ParameterSyntax>(ImmutableArray<SyntaxNode>.Empty.Add(otherParam));
                ExpressionSyntax? chain = null;
                foreach (var p in parameters)
                {
                    var left = new NameExpressionSyntax(_syntaxTree, p.Identifier);
                    var right = new MemberAccessExpressionSyntax(_syntaxTree, new NameExpressionSyntax(_syntaxTree, otherParam.Identifier), SyntheticToken(SyntaxKind.DotToken, pos, "."), p.Identifier);
                    var eq = new BinaryExpressionSyntax(_syntaxTree, left, SyntheticToken(SyntaxKind.EqualsEqualsToken, pos, "=="), right);
                    chain = chain == null ? eq : new BinaryExpressionSyntax(_syntaxTree, chain, SyntheticToken(SyntaxKind.AmpersandAmpersandToken, pos, "&&"), eq);
                }

                var equalsBody = new BlockStatementSyntax(_syntaxTree, SyntheticToken(SyntaxKind.OpenBraceToken, pos, "{"),
                    ImmutableArray.Create<StatementSyntax>(new ReturnStatementSyntax(_syntaxTree, SyntheticToken(SyntaxKind.ReturnKeyword, pos, "return"), chain)),
                    SyntheticToken(SyntaxKind.CloseBraceToken, pos, "}"));
                members.Add(new FunctionDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, publicMods,
                    SyntheticToken(SyntaxKind.FunctionKeyword, pos, "function"),
                    SyntheticToken(SyntaxKind.IdentifierToken, pos, "Equals"), null,
                    SyntheticToken(SyntaxKind.OpenParenthesisToken, pos, "("), otherList,
                    SyntheticToken(SyntaxKind.CloseParenthesisToken, pos, ")"),
                    new TypeClauseSyntax(_syntaxTree, null, SyntheticToken(SyntaxKind.IdentifierToken, pos, "bool")), equalsBody));

                // record 的值相等：生成 == / != 运算符（逐字段比较）。
                // 此前只生成 Equals，于是 `a == b` 落到类引用相等分支 → 值相等的两个 record 判为不等（C# 语义不符）。
                // 走运算符机制而非改 == 的绑定规则：用户可显式声明 operator ==，行为与 C# 一致。
                members.Add(BuildRecordComparisonOperator(pos, publicMods, nameToken, parameters, isEquals: true));
                members.Add(BuildRecordComparisonOperator(pos, publicMods, nameToken, parameters, isEquals: false));
            }

            var toStringExpr = BuildRecordToString(nameToken, parameters, pos);
            var toStringBody = new BlockStatementSyntax(_syntaxTree, SyntheticToken(SyntaxKind.OpenBraceToken, pos, "{"),
                ImmutableArray.Create<StatementSyntax>(new ReturnStatementSyntax(_syntaxTree, SyntheticToken(SyntaxKind.ReturnKeyword, pos, "return"), toStringExpr)),
                SyntheticToken(SyntaxKind.CloseBraceToken, pos, "}"));
            members.Add(new FunctionDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, publicMods,
                SyntheticToken(SyntaxKind.FunctionKeyword, pos, "function"),
                SyntheticToken(SyntaxKind.IdentifierToken, pos, "ToString"), null,
                SyntheticToken(SyntaxKind.OpenParenthesisToken, pos, "("), new SeparatedSyntaxList<ParameterSyntax>(ImmutableArray<SyntaxNode>.Empty),
                SyntheticToken(SyntaxKind.CloseParenthesisToken, pos, ")"),
                new TypeClauseSyntax(_syntaxTree, null, SyntheticToken(SyntaxKind.IdentifierToken, pos, "string")), toStringBody));
        }

        private ExpressionSyntax BuildRecordToString(SyntaxToken nameToken, SeparatedSyntaxList<ParameterSyntax> parameters, int pos)
        {
            ExpressionSyntax? expr = null;
            void Concat(string value)
            {
                var literal = new LiteralExpressionSyntax(_syntaxTree, SyntheticToken(SyntaxKind.StringToken, pos, "\"" + value + "\"", value));
                expr = expr == null ? literal : new BinaryExpressionSyntax(_syntaxTree, expr, SyntheticToken(SyntaxKind.PlusToken, pos, "+"), literal);
            }

            Concat(nameToken.Text + " { ");
            var first = true;
            foreach (var p in parameters)
            {
                // Cocoa 无 `string + 非 string`：仅字符串字段拼值，非 string 字段仅留名。
                var isStringField = p.Type?.Identifier.Text == "string";
                if (!first)
                {
                    Concat(", ");
                }

                Concat(p.Identifier.Text + (isStringField ? " = " : ""));
                if (isStringField)
                {
                    var valueRef = new NameExpressionSyntax(_syntaxTree, p.Identifier);
                    expr = expr == null ? valueRef : new BinaryExpressionSyntax(_syntaxTree, expr, SyntheticToken(SyntaxKind.PlusToken, pos, "+"), valueRef);
                }

                first = false;
            }

            Concat(" }");
            return expr ?? new LiteralExpressionSyntax(_syntaxTree, SyntheticToken(SyntaxKind.StringToken, pos, "\"\"", ""));
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

            return new FunctionDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, functionKeyword: null, identifier, typeParameters: null, openParenthesisToken, parameters, closeParenthesisToken, type, body);
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

        /// <summary>解析声明前 attribute 列表（`[Name]` / `[Name("arg")]`，可多个；Tier-1 编译器识别 Facade）。</summary>
        private ImmutableArray<AttributeSyntax> ParseOptionalAttributes()
        {
            if (Current.Kind != SyntaxKind.OpenBracketToken)
            {
                return ImmutableArray<AttributeSyntax>.Empty;
            }

            var attributes = ImmutableArray.CreateBuilder<AttributeSyntax>();
            while (Current.Kind == SyntaxKind.OpenBracketToken)
            {
                attributes.Add(ParseAttribute());
            }

            return attributes.ToImmutable();
        }

        private AttributeSyntax ParseAttribute()
        {
            var openBracket = MatchToken(SyntaxKind.OpenBracketToken);

            SyntaxToken name;
            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                name = MatchToken(SyntaxKind.IdentifierToken);
            }
            else
            {
                ReportError(Current.Location, "attribute 名应为标识符（如 `[Facade(\"System.IntPtr\")]`）。");
                name = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, "?", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            }

            SyntaxToken? openParenthesis = null;
            var arguments = ImmutableArray.CreateBuilder<SyntaxToken>();
            SyntaxToken? closeParenthesis = null;

            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
                while (Current.Kind != SyntaxKind.CloseParenthesisToken && Current.Kind != SyntaxKind.EndOfFileToken)
                {
                    if (Current.Kind == SyntaxKind.StringToken ||
                        Current.Kind == SyntaxKind.NumberToken ||
                        Current.Kind == SyntaxKind.TrueKeyword ||
                        Current.Kind == SyntaxKind.FalseKeyword ||
                        Current.Kind == SyntaxKind.CharToken)
                    {
                        arguments.Add(NextToken());
                    }
                    else
                    {
                        ReportError(Current.Location, "attribute 实参目前仅支持字面量（string/i32/f64/bool/char，如 `[Facade(\"System.IntPtr\")]`）。");
                        NextToken();
                    }

                    if (Current.Kind == SyntaxKind.CommaToken)
                    {
                        arguments.Add(NextToken());
                    }
                    else
                    {
                        break;
                    }
                }

                closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
            return new AttributeSyntax(_syntaxTree, openBracket, name, openParenthesis, arguments.ToImmutable(), closeParenthesis, closeBracket);
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
                default:
                    return false;
            }
        }

        private MemberSyntax ParseEnumDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers)
        {
            var enumKeyword = MatchToken(SyntaxKind.EnumKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
            var members = ParseEnumMemberList();
            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new EnumDeclarationSyntax(_syntaxTree, attributes, modifiers, enumKeyword, identifier, openBraceToken, members, closeBraceToken);
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

        private MemberSyntax ParseFunctionDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers, bool allowBody = true)
        {
            SyntaxToken? functionKeyword = null;
            SyntaxToken identifier;

            // 运算符重载 / 转换运算符声明：`function operator +` / `function implicit operator T`。
            // 复用 FunctionDeclarationSyntax：关键字槽存 operator|implicit|explicit，identifier 槽存运算符 token
            // （转换运算符的 identifier 存被转换目标类型的首标识符，binder 以返回类型为准合成方法名）。
            if (Current.Kind == SyntaxKind.FunctionKeyword)
            {
                functionKeyword = NextToken();
            }

            if (Current.Kind == SyntaxKind.OperatorKeyword ||
                Current.Kind == SyntaxKind.ImplicitKeyword ||
                Current.Kind == SyntaxKind.ExplicitKeyword)
            {
                var operatorPrefix = NextToken();
                if (operatorPrefix.Kind == SyntaxKind.ImplicitKeyword || operatorPrefix.Kind == SyntaxKind.ExplicitKeyword)
                {
                    MatchToken(SyntaxKind.OperatorKeyword);
                }

                functionKeyword = operatorPrefix;
                identifier = ParseOperatorToken();
            }
            else
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
            }

            var typeParameters = ParseOptionalTypeParameterList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var type = ParseOptionalTypeClause();

            var externMetadata = ParseOptionalExternMetadata();

            var whereClauses = ParseWhereClauses();

            BlockStatementSyntax? body = null;

            if (!allowBody)
            {
                // 接口成员：只有签名，无函数体；可选尾随分号
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                }
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

            return new FunctionDeclarationSyntax(_syntaxTree, attributes, modifiers, functionKeyword, identifier, typeParameters, openParenthesisToken, parameters, closeParenthesisToken, type, body, externMetadata, whereClauses);
        }

        /// <summary>可重载的运算符 token 集合（二元 + 一元 + 相等/关系 + 取反/按位非）。
        /// 转换运算符（implicit/explicit）不走本表——其"运算符"是目标类型，identifier 退化为类型名 token。</summary>
        private static bool IsOverloadableOperatorToken(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.PlusToken:
                case SyntaxKind.MinusToken:
                case SyntaxKind.StarToken:
                case SyntaxKind.SlashToken:
                case SyntaxKind.PercentToken:
                case SyntaxKind.AmpersandToken:
                case SyntaxKind.PipeToken:
                case SyntaxKind.HatToken:
                case SyntaxKind.ShiftLeftToken:
                case SyntaxKind.ShiftRightToken:
                case SyntaxKind.EqualsEqualsToken:
                case SyntaxKind.BangEqualsToken:
                case SyntaxKind.LessToken:
                case SyntaxKind.LessOrEqualsToken:
                case SyntaxKind.GreaterToken:
                case SyntaxKind.GreaterOrEqualsToken:
                case SyntaxKind.BangToken:
                case SyntaxKind.TildeToken:
                    return true;
                default:
                    return false;
            }
        }

        private SyntaxToken ParseOperatorToken()
        {
            if (IsOverloadableOperatorToken(Current.Kind))
            {
                return NextToken();
            }

            // 转换运算符的目标类型：先吃标识符，允许点号限定（implicit operator Foo.Bar）
            var builder = new List<SyntaxToken> { MatchToken(SyntaxKind.IdentifierToken) };
            while (Current.Kind == SyntaxKind.DotToken)
            {
                builder.Add(NextToken());
                builder.Add(MatchToken(SyntaxKind.IdentifierToken));
            }

            return builder[0];
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

        private MemberSyntax ParseClassDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers)
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

            return new ClassDeclarationSyntax(_syntaxTree, attributes, modifiers, classKeyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses, openBraceToken, members, closeBraceToken);
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

            var attributes = ParseOptionalAttributes();

            var modifiers = ParseModifiers();

            if (Current.Kind == SyntaxKind.ConstructorKeyword)
            {
                return ParseConstructorDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.CdeclKeyword ||
                Current.Kind == SyntaxKind.StdcallKeyword ||
                Current.Kind == SyntaxKind.FunctionKeyword)
            {
                return ParseFunctionDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.EventKeyword)
            {
                return ParseEventDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.DelegateKeyword)
            {
                return ParseDelegateDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.ClassKeyword || Current.Kind == SyntaxKind.StructKeyword)
            {
                // 嵌套类型：类体/结构体体内直接写 `class Inner { … }`。
                // ParseClassDeclaration 本身就吃 `class|struct` 关键字打头，可直接复用。
                return ParseClassDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.PropertyKeyword)
            {
                return ParsePropertyDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.FieldKeyword)
            {
                MatchToken(SyntaxKind.FieldKeyword);
                return ParseClassFieldDeclaration(attributes, modifiers);
            }

            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                if (Peek(1).Kind == SyntaxKind.ColonToken)
                {
                    // 6e-M31：类字段须显式 `field` 关键字（与 property 对齐）；裸 `name: type` 报诊断并降级解析
                    ReportError(Current.Location, "类字段声明须加 field 关键字，如 `field " + Current.Text + ": ...`。");
                    return ParseClassFieldDeclaration(attributes, modifiers);
                }

                ReportError(Current.Location, "Cocoa 类成员须用 function/property/field/constructor 关键字且类型后置，不支持 C# 式 `类型 名称(...)`。");
                return ParseCSharpStyleMember(modifiers, className);
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
            var badColon = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, ":", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badType = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, Current.Text, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badMember = new ClassFieldDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, Current, new TypeClauseSyntax(_syntaxTree, badColon, badType));
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
                    return new ClassFieldDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, identifier, type);
                }

                case SyntaxKind.EqualsToken:
                {
                    var equalsToken = MatchToken(SyntaxKind.EqualsToken);
                    var initializer = ParseExpression();
                    return new ClassFieldDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, identifier, type, equalsToken, initializer);
                }

                case SyntaxKind.OpenBraceToken:
                case SyntaxKind.FatArrowToken:
                    return ParseCSharpStyleProperty(modifiers, type, identifier);

                case SyntaxKind.OpenParenthesisToken:
                    return ParseCSharpStyleMethod(modifiers, type, identifier);

                default:
                    _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.SemicolonToken);
                    return new ClassFieldDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, identifier, type);
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

            return new FunctionDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, functionKeyword: null, identifier, typeParameters, openParenthesisToken, parameters, closeParenthesisToken, type, body, whereClauses: whereClauses);
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

            return new PropertyDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, propertyKeyword: null, identifier, type, openBraceToken, getter, setter, closeBraceToken, ImmutableArray<ParameterSyntax>.Empty, equalsToken, initializer);
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

        private MemberSyntax ParseInterfaceDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers)
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

            return new InterfaceDeclarationSyntax(_syntaxTree, attributes, modifiers, interfaceKeyword, identifier, typeParameters, baseTypes.ToImmutable(), whereClauses, openBraceToken, members, closeBraceToken);
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
                    Current.Kind == SyntaxKind.FunctionKeyword ||
                    Current.Kind == SyntaxKind.OperatorKeyword ||
                    Current.Kind == SyntaxKind.ImplicitKeyword ||
                    Current.Kind == SyntaxKind.ExplicitKeyword)
                {
                    // 接口内声明运算符虽非法（C# 同），仍走完整声明解析以便 binder 给出精确诊断而非语法错误
                    members.Add(ParseFunctionDeclaration(ImmutableArray<AttributeSyntax>.Empty, modifiers, allowBody: false));
                }
                else if (Current.Kind == SyntaxKind.PropertyKeyword)
                {
                    members.Add(ParsePropertyDeclaration(ImmutableArray<AttributeSyntax>.Empty, modifiers));
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

                    members.Add(new FunctionDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, functionKeyword: null, memberIdentifier, typeParameters: null, openParenthesisToken, parameters, closeParenthesisToken, type, body: null, whereClauses: csMemberWhereClauses));
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

        private MemberSyntax ParseClassFieldDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers)
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

            return new ClassFieldDeclarationSyntax(_syntaxTree, attributes, modifiers, identifier, type, equalsToken, initializer);
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
                           (Peek(1).Kind == SyntaxKind.OpenParenthesisToken || Peek(1).Kind == SyntaxKind.LessToken);

            SyntaxToken identifier;
            SeparatedSyntaxList<ParameterSyntax> parameters;
            TypeClauseSyntax? returnType = null;
            TypeParameterListSyntax? typeParameters = null;
            SyntaxToken openParenToken;
            SyntaxToken closeParenToken;
            SyntaxToken? semicolonToken = null;

            if (isCoForm)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);

                // 泛型类型参数（6e-M22 delegate 真实类型化）：`delegate 名<T>(...)`——两形态均在名字与 `(` 之间。
                typeParameters = ParseOptionalTypeParameterList();

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

                // 泛型类型参数（6e-M22 delegate 真实类型化）：`delegate 返回类型 名<T>(...)`
                typeParameters = ParseOptionalTypeParameterList();

                openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                parameters = ParseParameterList();
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                if (Current.Kind == SyntaxKind.SemicolonToken)
                    semicolonToken = MatchToken(SyntaxKind.SemicolonToken);
            }

            return new DelegateDeclarationSyntax(_syntaxTree, modifiers, delegateKeyword, returnType, identifier, typeParameters, openParenToken, parameters, closeParenToken, semicolonToken);
        }

        private MemberSyntax ParsePropertyDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers)
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

            return new PropertyDeclarationSyntax(_syntaxTree, attributes, modifiers, propertyKeyword, identifier, type, openBraceToken, getter, setter, closeBraceToken, ImmutableArray<ParameterSyntax>.Empty, equalsToken, initializer);
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

            return new PropertyDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, propertyKeyword, identifier, type, openBrace, getter, setter: null, closeBrace);
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

            return new PropertyDeclarationSyntax(_syntaxTree, ImmutableArray<AttributeSyntax>.Empty, modifiers, propertyKeyword, identifier, type, openBraceToken, getter, setter, closeBraceToken, builder.ToImmutable(), equalsToken, initializer);
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
            if (Current.Kind == SyntaxKind.OutKeyword || Current.Kind == SyntaxKind.RefKeyword || Current.Kind == SyntaxKind.ParamsKeyword || Current.Kind == SyntaxKind.ThisKeyword)
            {
                modifier = MatchToken(Current.Kind);
            }

            if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.ColonToken)
            {
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var type = ParseTypeClause();

                SyntaxToken? equalsToken = null;
                ExpressionSyntax? defaultValue = null;
                if (Current.Kind == SyntaxKind.EqualsToken)
                {
                    equalsToken = MatchToken(SyntaxKind.EqualsToken);
                    defaultValue = ParseExpression();
                }

                return new ParameterSyntax(_syntaxTree, modifier, identifier, type, equalsToken, defaultValue);
            }

            ReportError(Current.Location, "Cocoa 参数须为 `名称: 类型`（类型后置），不支持 C# 式 `类型 名称`。");
            var csType = ParsePrefixTypeClause();
            var csIdentifier = MatchToken(SyntaxKind.IdentifierToken);

            return new ParameterSyntax(_syntaxTree, modifier, csIdentifier, csType);
        }
    }
}
