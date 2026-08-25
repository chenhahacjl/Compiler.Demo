using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法分析器核心（6e-M15 双前端拆分）
    /// <br/>
    /// Token =&gt; 语法树
    /// <br/>
    /// 共享：token 管道 / 诊断 / trivia / 表达式引擎 / 公共语句。
    /// 方言差异经 virtual 钩子由子类覆写：<see cref="CocoaParser"/>（宽松，`.co`）与 <see cref="CSharpParser"/>（严格，`.cs`）。
    /// 规约：基类不得出现方言分支；新语法落点 = 覆写各自钩子，逐字相同的进基类一次。
    /// </summary>
    internal abstract class ParserCore
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        protected readonly SyntaxTree _syntaxTree;
        protected readonly SourceText _text;
        private readonly ImmutableArray<SyntaxToken> _tokens;
        private int _position;

        /// <summary>`>>` 拆分出的合成 token 队列（6e-M20 嵌套泛型 `List<List<int>>`；仅在泛型实参表解析窗口内非空）。</summary>
        private readonly Queue<SyntaxToken> _syntheticTokens = new Queue<SyntaxToken>();

        /// <summary>当前解析方言（子类覆写；用于插值洞子解析与方言钩子默认行为）。</summary>
        protected abstract LanguageDialect Dialect { get; }

        protected ParserCore(SyntaxTree syntaxTree)
        {
            var tokens = new List<SyntaxToken>();
            var badTokens = new List<SyntaxToken>();

            var lexer = new Lexer(syntaxTree);
            SyntaxToken token;

            do
            {
                token = lexer.Lex();

                if (token.Kind == SyntaxKind.BadToken)
                {
                    badTokens.Add(token);
                }
                else
                {
                    if (badTokens.Count > 0)
                    {
                        var leadingTrivia = token.LeadingTrivia.ToBuilder();
                        var index = 0;

                        foreach (var badToken in badTokens)
                        {
                            foreach (var lt in badToken.LeadingTrivia)
                            {
                                leadingTrivia.Insert(index++, lt);
                            }

                            var trivia = new SyntaxTrivia(syntaxTree, SyntaxKind.SkippedTextTrivia, badToken.Position, badToken.Text);

                            leadingTrivia.Insert(index++, trivia);

                            foreach (var tt in badToken.TrailingTrivia)
                            {
                                leadingTrivia.Insert(index++, tt);
                            }
                        }

                        badTokens.Clear();

                        token = new SyntaxToken(token.SyntaxTree, token.Kind, token.Position, token.Text, token.Value, leadingTrivia.ToImmutable(), token.TrailingTrivia);
                    }

                    tokens.Add(token);
                }

            } while (token.Kind != SyntaxKind.EndOfFileToken);

            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
            _tokens = tokens.ToImmutableArray();
            _diagnostics.AddRange(lexer.Diagnostics);
        }

        /// <summary>用预词法 token 构造 Parser（插值洞的子解析；token 属同一 SyntaxTree，Span 绝对定位）。</summary>
        protected ParserCore(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
        {
            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
            _tokens = tokens;
        }

        /// <summary>按方言创建解析器（入口工厂，SyntaxTree.Parse 使用）。</summary>
        public static ParserCore Create(SyntaxTree syntaxTree, LanguageDialect dialect)
        {
            return dialect switch
            {
                LanguageDialect.CSharp => new CSharpParser(syntaxTree),
                _ => new CocoaParser(syntaxTree),
            };
        }

        /// <summary>用预词法 token 按当前方言创建子解析器（插值洞；洞内语法与宿主方言一致）。</summary>
        protected ParserCore CreateSubParser(ImmutableArray<SyntaxToken> tokens)
        {
            return Dialect == LanguageDialect.CSharp
                ? new CSharpParser(_syntaxTree, tokens)
                : new CocoaParser(_syntaxTree, tokens);
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        protected SyntaxToken Peek(int offset)
        {
            var index = _position + offset;
            if (index >= _tokens.Length)
            {
                return _tokens[_tokens.Length - 1];
            }

            return _tokens[index];
        }

        protected SyntaxToken Current => _syntheticTokens.Count > 0 ? _syntheticTokens.Peek() : Peek(0);

        protected SyntaxToken NextToken()
        {
            if (_syntheticTokens.Count > 0)
            {
                return _syntheticTokens.Dequeue();
            }

            var current = Current;
            _position++;

            return current;
        }

        protected SyntaxToken MatchToken(SyntaxKind kind)
        {
            if (Current.Kind == kind)
            {
                return NextToken();
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, kind);
            return new SyntaxToken(_syntaxTree, kind, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        /// <summary>报告诊断（供子类方言收紧使用）。</summary>
        protected void ReportError(TextLocation location, string message) => _diagnostics.ReportError(location, message);

        public CompilationUnitSyntax ParseCompilationUnit()
        {
            var members = ParseMembers();
            var endOfFileToken = MatchToken(SyntaxKind.EndOfFileToken);

            return new CompilationUnitSyntax(_syntaxTree, members, endOfFileToken);
        }

        private ImmutableArray<MemberSyntax> ParseMembers()
        {
            var members = ImmutableArray.CreateBuilder<MemberSyntax>();

            while (Current.Kind != SyntaxKind.EndOfFileToken)
            {
                // 语句边界的分号可选：跳过孤立 ';'（`using Foo.Bar;`、顶层 `print(1);` 等 C# 式结尾）
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                var startToken = Current;

                var member = ParseMember();
                members.Add(member);

                // If ParseMember() did not consume any tokens,
                // we need to skip the current token and continue
                // in order to avoid an infinite loop.
                //
                // We don't need to report an error, because we'll
                // already tried to parse an expression statement
                // and reported one.
                if (Current == startToken)
                {
                    NextToken();
                }
            }

            return members.ToImmutable();
        }

        private MemberSyntax ParseMember()
        {
            if (Current.Kind == SyntaxKind.ImportKeyword)
            {
                // 顶层位置式 import 已废弃（6e-M17 Step 4）：须作类成员 import 块
                if (!AllowTopLevelImport())
                {
                    ReportError(Current.Location, "顶层 `import` 声明已废弃：请改用类内 import 块 `class Kernel32 { import kernel32.dll { static extern ... } }`。");
                }

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

            // 统一修饰符：public/private/stdcall/cdecl（顺序无关）
            var modifiers = ParseModifiers();

            if (Current.Kind == SyntaxKind.CdeclKeyword ||
                Current.Kind == SyntaxKind.StdcallKeyword ||
                Current.Kind == SyntaxKind.FunctionKeyword)
            {
                if (!AllowCocoaFunctionKeywords())
                {
                    ReportError(Current.Location, "C# 方言函数声明须为 `[修饰符] 返回类型 名称(...)`，不能使用 'function'/'cdecl'/'stdcall' 关键字。");
                }

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

            if (Current.Kind == SyntaxKind.InterfaceKeyword)
            {
                return ParseInterfaceDeclaration(modifiers);
            }

            // delegate 声明（6e-M22）：`delegate Ret Name(params)` — 命名空间级
            if (Current.Kind == SyntaxKind.DelegateKeyword)
            {
                return ParseDelegateDeclaration(modifiers);
            }

            // P1（6e-M11）：C# 式顶层函数 `type name(params)`（类型前置，可带 `[]` 返回类型）
            if (IsCSharpStyleTopLevelFunction())
            {
                if (!AllowCSharpStyleTopLevelFunction())
                {
                    ReportError(Current.Location, "Cocoa 顶层函数须用 function 关键字（如 `function Add(a: int, b: int): int`），不支持 C# 式 `返回类型 名称(...)`。");
                }

                return ParseCSharpStyleTopLevelFunction(modifiers);
            }

            // Cocoa 式无关键字顶层函数 `name(params) [ : type ] { ... }`（如 `Main(): void`）
            // 双方言均拒绝：须带 function 关键字（Cocoa）/返回类型（C#）；报错后仍解析以保留干净语法树恢复。
            // 括号扫描消歧：`)` 后紧跟 `{`/`:` 判定为函数声明，否则是全局表达式语句（如 `print("hi")`）
            if (IsNoKeywordTopLevelFunction())
            {
                ReportError(Current.Location, "顶层函数须用 function 关键字（Cocoa）或带返回类型（C#），不支持无关键字写法（如 `Main(): void`）。");
                return ParseNoKeywordTopLevelFunction(modifiers);
            }

            if (modifiers.Any())
            {
                // 修饰符后非法声明：报错并继续按全局语句解析
                _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.FunctionKeyword);
            }

            return ParseGlobalStatement();
        }

        /// <summary>C# 方言是否允许 `function`/`cdecl`/`stdcall` 关键字顶层函数（Cocoa 为 true，C# 为 false）。</summary>
        protected virtual bool AllowCocoaFunctionKeywords() => true;

        /// <summary>方言是否允许 C# 式顶层函数 `type name(params)`（Cocoa 为 false，C# 为 true）。</summary>
        protected virtual bool AllowCSharpStyleTopLevelFunction() => true;

        /// <summary>方言是否允许类成员中的 C# 式声明 `type name ...`（字段/属性/方法/构造函数；Cocoa 为 false，C# 为 true）。</summary>
        protected virtual bool AllowCSharpStyleMember() => true;

        /// <summary>方言是否允许 C# 式局部变量 `type name [= expr]`（无 var/let/const；Cocoa 为 false，C# 为 true）。</summary>
        protected virtual bool AllowCSharpStyleVariableDeclaration() => true;

        /// <summary>方言是否允许冒号 `:` 基类型/基接口（Cocoa 为 false，须用 extends；C# 为 true）。</summary>
        protected virtual bool AllowColonInheritance() => true;

        /// <summary>函数类型 `(A,B) -&gt; R`（6e-M22 C2）：仅 .co；.cs 走 Func/Action/Predicate 家族拼写。</summary>
        protected virtual bool AllowArrowFunctionTypes() => true;

        /// <summary>免括号单参 lambda `x =&gt; expr`（6e-M22 C2）：仅 .cs。</summary>
        protected virtual bool AllowParenlessLambda() => false;

        /// <summary>lambda 隐式类型参数 `(x, y) =&gt; …`（6e-M22 C2）：仅 .cs；.co 要求显式标注。</summary>
        protected virtual bool AllowImplicitLambdaParameters() => false;

        /// <summary>C# 式顶层函数判定：`type name(` / `type[] name(` / `List&lt;int&gt; name&lt;T&gt;(`（返回类型/函数名均可带泛型后缀）。</summary>
        private bool IsCSharpStyleTopLevelFunction()
        {
            var offset = 0;
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken)
            {
                return false;
            }

            offset++;

            // 泛型返回类型后缀：`List<int> Make(...)`（6e-M20）
            if (Peek(offset).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(offset);
                if (afterAngles < 0)
                {
                    return false;
                }

                offset = afterAngles;
            }

            // 类型后缀 `[]`：`int[] name(` / `string[][] name(`
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

            // 泛型方法类型参数后缀：`T Max<T>(…)`（6e-M20）
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

        /// <summary>Cocoa 式无关键字顶层函数判定：`name(...)` 的 `)` 后紧跟 `{` 或 `:`。</summary>
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

        /// <summary>C# 式顶层函数：`type name(params) { ... }` / `type name(params);`。</summary>
        private MemberSyntax ParseCSharpStyleTopLevelFunction(ImmutableArray<SyntaxToken> modifiers)
        {
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            return ParseCSharpStyleMethod(modifiers, type, identifier);
        }

        /// <summary>Cocoa 式无关键字顶层函数：`name(params) [ : type ] { ... }` / `name(params) [ : type ] => expr`（归一 FunctionDeclarationSyntax）。</summary>
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

        /// <summary>顶层位置式 `import <dll>` 是否允许（6e-M17 Step 4 起废弃，双方言一律拒绝 → false）。</summary>
        protected virtual bool AllowTopLevelImport() => false;

        /// <summary>解析 import 块：`import <dll> { static extern ... }`（6e-M17 Step 4，仅作类成员）；可选块级键 `charset = unicode`（Step 5）。</summary>
        private MemberSyntax ParseImportBlock()
        {
            var importKeyword = MatchToken(SyntaxKind.ImportKeyword);
            var nameTokens = ParseQualifiedName();

            // 块级 charset 键（可选，括号宽松）：`import user32.dll charset = unicode` / `import (user32.dll charset = unicode)`
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
                // 块内成员以函数声明为主（static extern）；跳过孤立分号
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

        protected virtual MemberSyntax ParseUsingDirective()
        {
            return ParseUsingDirectiveCore();
        }

        /// <summary>解析 `using [static] [Alias =] <name>` 结构（.co 分号可选；.cs 由 CSharpParser 强制分号）。</summary>
        protected MemberSyntax ParseUsingDirectiveCore()
        {
            var usingKeyword = MatchToken(SyntaxKind.UsingKeyword);
            SyntaxToken? staticKeyword = null;
            SyntaxToken? aliasToken = null;

            // `using static <name>`：导入类的静态成员（C# 同构）
            if (Current.Kind == SyntaxKind.StaticKeyword)
            {
                staticKeyword = MatchToken(SyntaxKind.StaticKeyword);
            }

            // `using <Alias> = <name>`：别名导入
            if (staticKeyword == null &&
                Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.EqualsToken)
            {
                aliasToken = MatchToken(SyntaxKind.IdentifierToken);
                MatchToken(SyntaxKind.EqualsToken);
            }

            var nameTokens = ParseQualifiedName();

            return new UsingDirectiveSyntax(_syntaxTree, usingKeyword, staticKeyword, aliasToken, nameTokens);
        }

        private MemberSyntax ParseNamespaceDeclaration()
        {
            var namespaceKeyword = MatchToken(SyntaxKind.NamespaceKeyword);
            var nameTokens = ParseQualifiedName();

            // 文件作用域命名空间：`namespace Foo;` —— 剩余整个文件归入 Foo（C# 10 语义，两方言共享）
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

        protected ImmutableArray<SyntaxToken> ParseQualifiedName()
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
            // 若修饰符中夹带 stdcall/cdecl（历史写法 `stdcall function`），它们在 ParseModifiers 已收集
            var functionKeyword = MatchToken(SyntaxKind.FunctionKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var type = ParseOptionalTypeClause();

            // extern 元数据子句（6e-M17 Step 5）：`extern(entry=…, charset=…)` / `extern entry=…, charset=…`
            var externMetadata = ParseOptionalExternMetadata();

            // 泛型约束子句：`function Max<T>(a: T, b: T): T where T: IComparable<T>`
            var whereClauses = ParseWhereClauses();

            BlockStatementSyntax? body = null;

            // 表达式体函数：`function Foo(): int => expr`（合成 `{ return expr; }`）
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
            // extern（stdcall/cdecl）与 abstract 方法无方法体
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

        /// <summary>解析可选的 extern 元数据子句：`extern(entry=…, charset=…)` 或 `extern entry=…, charset=…`（括号可选，命名键值，逗号分隔）。</summary>
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
            var classKeyword = MatchToken(SyntaxKind.ClassKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeParameters = ParseOptionalTypeParameterList();
            var baseTypes = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

            // class Foo: Bar, IA, IB / class Foo extends Bar, IA —— 基类型列表（首个非接口 = 基类，其余须为接口）
            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                if (Current.Kind == SyntaxKind.ExtendsKeyword && !AllowExtendsKeyword())
                {
                    ReportError(Current.Location, "C# 方言继承/基接口须用冒号 `:`，不支持 'extends' 关键字。");
                }

                if (Current.Kind == SyntaxKind.ColonToken && !AllowColonInheritance())
                {
                    ReportError(Current.Location, "Cocoa 继承/基接口须用 extends 关键字，不支持冒号 `:`。");
                }

                var prefixToken = NextToken(); // : / extends
                baseTypes.Add(CreateBaseTypeClause(prefixToken));

                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken(); // ,
                    baseTypes.Add(CreateBaseTypeClause(null));
                }
            }

            // 泛型约束子句：`class Foo<T>: Bar where T: IComparable<T>`（C# 顺序：类型参数 → 基类 → where）
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
                // C# 式声明以 ';' 结尾：跳过孤立分号
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
            // import 块（6e-M17 Step 4）：`import kernel32.dll { static extern ... }` —— 块内成员只允许 extern 函数声明
            if (Current.Kind == SyntaxKind.ImportKeyword)
            {
                return ParseImportBlock();
            }

            // 统一修饰符：public/private/stdcall/cdecl（顺序无关）
            var modifiers = ParseModifiers();

            if (Current.Kind == SyntaxKind.ConstructorKeyword)
            {
                if (!AllowCocoaClassMemberKeywords())
                {
                    ReportError(Current.Location, "C# 方言构造函数应写成 `ClassName(...)`（名字 = 类名），不能使用 'constructor' 关键字。");
                }

                return ParseConstructorDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.CdeclKeyword ||
                Current.Kind == SyntaxKind.StdcallKeyword ||
                Current.Kind == SyntaxKind.FunctionKeyword)
            {
                if (!AllowCocoaClassMemberKeywords())
                {
                    ReportError(Current.Location, "C# 方言方法须为 `返回类型 名称(...)`，不能使用 'function'/'cdecl'/'stdcall' 关键字。");
                }

                return ParseFunctionDeclaration(modifiers);
            }

            // 事件声明（6e-M22 C5+）：`event Name: HandlerType`（.co）/ `event HandlerType Name;`（.cs）
            if (Current.Kind == SyntaxKind.EventKeyword)
            {
                return ParseEventDeclaration(modifiers);
            }

            // delegate 声明（6e-M22）：`delegate Ret Name(params)` / `.cs` `delegate Ret Name(params);`
            if (Current.Kind == SyntaxKind.DelegateKeyword)
            {
                return ParseDelegateDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.PropertyKeyword)
            {
                if (!AllowCocoaClassMemberKeywords())
                {
                    ReportError(Current.Location, "C# 方言属性须为 `类型 名称 { get; set; }`，不能使用 'property' 关键字。");
                }

                return ParsePropertyDeclaration(modifiers);
            }

            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                // Cocoa 式字段：`name : type`
                if (Peek(1).Kind == SyntaxKind.ColonToken)
                {
                    if (!AllowCocoaStyleField())
                    {
                        ReportError(Current.Location, "C# 方言字段须为 `类型 名称`，不能 `名称: 类型`。");
                    }

                    return ParseClassFieldDeclaration(modifiers);
                }

                // C# 式成员：`type name ...`
                if (!AllowCSharpStyleMember())
                {
                    ReportError(Current.Location, "Cocoa 类成员须用 function/property/constructor 关键字且类型后置，不支持 C# 式 `类型 名称(...)`。");
                }

                return ParseCSharpStyleMember(modifiers, className);
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
            var badColon = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, ":", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badType = new SyntaxToken(_syntaxTree, SyntaxKind.BadToken, Current.Position, Current.Text, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var badMember = new ClassFieldDeclarationSyntax(_syntaxTree, modifiers, Current, new TypeClauseSyntax(_syntaxTree, badColon, badType));
            NextToken();
            return badMember;
        }

        /// <summary>C# 方言是否允许 `constructor`/`function`/`property` 类成员关键字（Cocoa 为 true，C# 为 false）。</summary>
        protected virtual bool AllowCocoaClassMemberKeywords() => true;

        /// <summary>C# 方言是否允许 Cocoa 式字段 `name: Type`（Cocoa 为 true，C# 为 false）。</summary>
        protected virtual bool AllowCocoaStyleField() => true;

        /// <summary>C# 方言是否允许 `extends` 继承关键字（Cocoa 为 true，C# 为 false，须用冒号 `:`）。</summary>
        protected virtual bool AllowExtendsKeyword() => true;

        /// <summary>C# 式成员：`type name ...`（字段/属性/方法/构造函数）。</summary>
        private MemberSyntax ParseCSharpStyleMember(ImmutableArray<SyntaxToken> modifiers, string className)
        {
            // C# 式构造函数：`ClassName(params)`（单标识符 == 类名，后接 (）
            if (Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.OpenParenthesisToken &&
                Current.Text == className)
            {
                return ParseCSharpStyleConstructor(modifiers);
            }

            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            // 泛型方法：`T Max<T>(…)` —— `<T>` 由 ParseCSharpStyleMethod 的类型参数解析接管
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

        /// <summary>C# 式构造函数：`ClassName(params) [: base(...) | : this(...)] { ... }`。</summary>
        private MemberSyntax ParseCSharpStyleConstructor(ImmutableArray<SyntaxToken> modifiers)
        {
            MatchToken(SyntaxKind.IdentifierToken); // 类名
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                if (Current.Kind == SyntaxKind.ExtendsKeyword && !AllowExtendsKeyword())
                {
                    ReportError(Current.Location, "C# 方言构造链须用冒号 `:`，不支持 'extends' 关键字。");
                }

                NextToken(); // : / extends
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

        /// <summary>C# 式方法：`returnType name&lt;T&gt;(params) where T: ... { ... }` / `returnType name(params) => expr`（返回类型前置）。</summary>
        private MemberSyntax ParseCSharpStyleMethod(ImmutableArray<SyntaxToken> modifiers, TypeClauseSyntax type, SyntaxToken identifier)
        {
            // 泛型方法类型参数：`Max<T>(…)`（6e-M20）
            var typeParameters = ParseOptionalTypeParameterList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            // 泛型约束子句：`where T: IComparable<T>`（签名后、函数体前）
            var whereClauses = ParseWhereClauses();

            BlockStatementSyntax? body = null;
            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                body = ParseBlockStatement();
            }
            else if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken(); // 抽象/外部方法签名：`;` 结尾
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

        /// <summary>C# 式属性：`type name { get; set; }` / `{ get { ... } set { ... } }` / `type name => expr`，可带初始化器。</summary>
        private MemberSyntax ParseCSharpStyleProperty(ImmutableArray<SyntaxToken> modifiers, TypeClauseSyntax type, SyntaxToken identifier)
        {
            // 表达式体属性：`type name => expr`（合成 get 访问器）
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

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword: null, identifier, type, openBraceToken, getter, setter, closeBraceToken, equalsToken, initializer);
        }

        /// <summary>前缀类型：`int` / `int[]` / `List&lt;int&gt;[]`（无冒号，C# 式类型前置）。</summary>
        protected TypeClauseSyntax ParsePrefixTypeClause()
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

            // `: base(...)` / `: this(...)` 或 `extends base(...)` / `extends this(...)` 构造链
            SyntaxToken? initializerKeyword = null;
            var initializerArguments = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                if (Current.Kind == SyntaxKind.ExtendsKeyword && !AllowExtendsKeyword())
                {
                    ReportError(Current.Location, "C# 方言构造链须用冒号 `:`，不支持 'extends' 关键字。");
                }

                NextToken(); // : / extends
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

            // interface IBird: IAnimal, IFlyable / interface IBird extends IAnimal, IFlyable —— 基接口列表
            if (Current.Kind == SyntaxKind.ColonToken ||
                Current.Kind == SyntaxKind.ExtendsKeyword)
            {
                if (Current.Kind == SyntaxKind.ExtendsKeyword && !AllowExtendsKeyword())
                {
                    ReportError(Current.Location, "C# 方言继承/基接口须用冒号 `:`，不支持 'extends' 关键字。");
                }

                if (Current.Kind == SyntaxKind.ColonToken && !AllowColonInheritance())
                {
                    ReportError(Current.Location, "Cocoa 继承/基接口须用 extends 关键字，不支持冒号 `:`。");
                }

                var prefixToken = NextToken();
                baseTypes.Add(CreateBaseTypeClause(prefixToken));

                while (Current.Kind == SyntaxKind.CommaToken)
                {
                    NextToken(); // ,
                    baseTypes.Add(CreateBaseTypeClause(null));
                }
            }

            // 泛型约束子句：`interface IEnumerable<T> where T: class`
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
                // C# 式接口成员以 ';' 结尾：跳过孤立分号
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
                    if (!AllowCocoaInterfaceKeywords())
                    {
                        ReportError(Current.Location, "C# 方言接口成员须为 `返回类型 名称(...)`，不能使用 'function'/'cdecl'/'stdcall' 关键字。");
                    }

                    // 接口成员：函数签名（无方法体）
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
                    if (!AllowCocoaInterfaceKeywords())
                    {
                        ReportError(Current.Location, "C# 方言接口属性须为 `类型 名称 { get; }`，不能使用 'property' 关键字。");
                    }

                    members.Add(ParsePropertyDeclaration(modifiers));
                }
                else if (Current.Kind == SyntaxKind.IdentifierToken &&
                         (Peek(1).Kind == SyntaxKind.IdentifierToken ||
                          // C# 式泛型类型成员：`IEnumerator<T> GetEnumerator()`（6e-M20）
                          (Peek(1).Kind == SyntaxKind.LessToken && IsGenericTypeNameAhead())))
                {
                    // C# 式接口成员：`type name (...)` 方法签名 / `type name { get; }` 属性
                    if (!AllowCSharpStyleMember())
                    {
                        ReportError(Current.Location, "Cocoa 接口成员须用 function/property 关键字且类型后置，不支持 C# 式 `类型 名称`。");
                    }

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

        /// <summary>C# 方言是否允许接口中的 `function`/`property` 关键字成员（Cocoa 为 true，C# 为 false）。</summary>
        protected virtual bool AllowCocoaInterfaceKeywords() => true;

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

        /// <summary>
        /// 事件声明（6e-M22 C5+）：`.co` `event Click: HandlerType` / `.cs` `event HandlerType Name;`
        /// 双方言统一产出 EventDeclarationSyntax；处理器类型可为函数类型/Func 家族/delegate 别名。
        /// </summary>
        private MemberSyntax ParseEventDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var eventKeyword = MatchToken(SyntaxKind.EventKeyword);

            // 形态判别：标识符后跟冒号 → .co（类型后置）；否则 → .cs（类型前置）
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

        /// <summary>
        /// delegate 声明（6e-M22）：两方言同形——返回类型前置，参数列表复用 ParseParameterList。
        /// `.co` `delegate void H(Object,string)` / `.cs` `public delegate void H(object,string);`
        /// </summary>
        private MemberSyntax ParseDelegateDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var delegateKeyword = MatchToken(SyntaxKind.DelegateKeyword);
            var returnType = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openParen = MatchToken(SyntaxKind.OpenParenthesisToken);
            var parameters = ParseParameterList();
            var closeParen = MatchToken(SyntaxKind.CloseParenthesisToken);

            if (Current.Kind == SyntaxKind.SemicolonToken)
                NextToken();

            return new DelegateDeclarationSyntax(_syntaxTree, modifiers, delegateKeyword, returnType, identifier, parameters);
        }

        private MemberSyntax ParsePropertyDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var propertyKeyword = MatchToken(SyntaxKind.PropertyKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var type = ParseTypeClause();

            // 表达式体属性：`property X: int => expr`（合成 get 访问器）
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

            // 自动属性初始化器：`property X: int { get set } = 42`
            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword, identifier, type, openBraceToken, getter, setter, closeBraceToken, equalsToken, initializer);
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

        /// <summary>表达式体合成：`expr` → `{ return expr; }` 块（synthetic token 定位到 `=>` 处，表达式节点保留真实 Span）。</summary>
        private BlockStatementSyntax SynthesizeExpressionBodyBlock(ExpressionSyntax expression, SyntaxToken arrow)
        {
            var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, arrow.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var returnKeyword = new SyntaxToken(_syntaxTree, SyntaxKind.ReturnKeyword, arrow.Position, "return", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, arrow.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);

            var returnStatement = new ReturnStatementSyntax(_syntaxTree, returnKeyword, expression);
            return new BlockStatementSyntax(_syntaxTree, openBrace, ImmutableArray.Create<StatementSyntax>(returnStatement), closeBrace);
        }

        /// <summary>表达式体属性合成：`expr` → `get { return expr; }` 访问器（只读）。</summary>
        private PropertyDeclarationSyntax SynthesizeExpressionBodyProperty(ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken arrow, ExpressionSyntax expression)
        {
            var openBrace = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, arrow.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var closeBrace = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, arrow.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var getKeyword = new SyntaxToken(_syntaxTree, SyntaxKind.GetKeyword, arrow.Position, "get", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var getter = new PropertyAccessorSyntax(_syntaxTree, ImmutableArray<SyntaxToken>.Empty, getKeyword, SynthesizeExpressionBodyBlock(expression, arrow), semicolonToken: null);

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword, identifier, type, openBrace, getter, setter: null, closeBrace);
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

        protected virtual ParameterSyntax ParseParameter()
        {
            // 双语法参数：Cocoa `name: Type` | C# `Type name`
            if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.ColonToken)
            {
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var type = ParseTypeClause();

                return new ParameterSyntax(_syntaxTree, identifier, type);
            }

            var csType = ParsePrefixTypeClause();
            var csIdentifier = MatchToken(SyntaxKind.IdentifierToken);

            return new ParameterSyntax(_syntaxTree, csIdentifier, csType);
        }

        private MemberSyntax ParseGlobalStatement()
        {
            var statement = ParseStatement();

            return new GlobalStatementSyntax(_syntaxTree, statement);
        }

        protected StatementSyntax ParseStatement()
        {
            StatementSyntax statement;
            switch (Current.Kind)
            {
                case SyntaxKind.OpenBraceToken:
                    statement = ParseBlockStatement();
                    break;
                case SyntaxKind.VarKeyword:
                case SyntaxKind.ConstKeyword:
                    statement = ParseVariableDeclaration();
                    break;
                case SyntaxKind.LetKeyword:
                    if (AllowLetKeyword())
                    {
                        statement = ParseVariableDeclaration();
                    }
                    else
                    {
                        ReportError(Current.Location, "'let' 是 Cocoa 语法；C# 方言请用 'var'。");
                        NextToken();
                        statement = ParseExpressionStatement();
                    }

                    break;
                case SyntaxKind.IfKeyword:
                    statement = ParseIfStatement();
                    break;
                case SyntaxKind.WhileKeyword:
                    statement = ParseWhileStatement();
                    break;
                case SyntaxKind.DoKeyword:
                    statement = ParseDoWhileStatement();
                    break;
                case SyntaxKind.ForKeyword:
                    statement = ParseForStatement();
                    break;
                case SyntaxKind.ForeachKeyword:
                    statement = ParseForeachStatement();
                    break;
                case SyntaxKind.SwitchKeyword:
                    statement = ParseSwitchStatement();
                    break;
                case SyntaxKind.BreakKeyword:
                    statement = ParseBreakStatement();
                    break;
                case SyntaxKind.ContinueKeyword:
                    statement = ParseContinueStatement();
                    break;
                case SyntaxKind.ReturnKeyword:
                    statement = ParseReturnStatement();
                    break;
                default:
                    // C# 式局部变量：`type name [= expr]`
                    if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                        Peek(1).Kind == SyntaxKind.IdentifierToken)
                    {
                        if (!AllowCSharpStyleVariableDeclaration())
                        {
                            ReportError(Current.Location, "Cocoa 局部变量须用 var/let/const 声明且类型后置，不支持 C# 式 `类型 名称`。");
                        }

                        statement = ParseCSharpStyleVariableDeclaration();
                    }
                    else
                    {
                        statement = ParseExpressionStatement();
                    }

                    break;
            }

            ConsumeStatementTerminator(statement);
            return statement;
        }

        /// <summary>C# 方言是否允许 `let` 关键字（Cocoa 为 true，C# 为 false）。</summary>
        protected virtual bool AllowLetKeyword() => true;

        /// <summary>语句终止符钩子：Cocoa 分号可选（默认无操作，孤立 `;` 由块循环跳过）；C# 方言对需终止语句强制 `;`。</summary>
        protected virtual void ConsumeStatementTerminator(StatementSyntax statement)
        {
        }

        /// <summary>C# 式局部变量：`int x` / `int x = 10;`（无 var/let 关键字）。</summary>
        protected StatementSyntax ParseCSharpStyleVariableDeclaration()
        {
            var type = ParsePrefixTypeClause();
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            SyntaxToken? equalsToken = null;
            ExpressionSyntax? initializer = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
                initializer = ParseExpression();
            }

            return new VariableDeclarationSyntax(_syntaxTree, keyword: null, identifier, type, equalsToken, initializer);
        }

        private BlockStatementSyntax ParseBlockStatement()
        {
            var statements = ImmutableArray.CreateBuilder<StatementSyntax>();

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            while (Current.Kind != SyntaxKind.EndOfFileToken &&
                Current.Kind != SyntaxKind.CloseBraceToken)
            {
                // 语句边界的分号可选：跳过孤立 ';'（C# 式方法体 `x = 1;` 等）
                if (Current.Kind == SyntaxKind.SemicolonToken)
                {
                    NextToken();
                    continue;
                }

                var startToken = Current;
                var statement = ParseStatement();
                statements.Add(statement);

                // If ParseStatement() did not consume any tokens,
                // we need to skip the current token and continue
                // in order to avoid an infinite loop.
                //
                // We don't need to report an error, because we'll
                // already tried to parse an expression statement
                // and reported one.
                if (Current == startToken)
                {
                    NextToken();
                }
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new BlockStatementSyntax(_syntaxTree, openBraceToken, statements.ToImmutable(), closeBraceToken);
        }

        protected virtual StatementSyntax ParseVariableDeclaration()
        {
            var expected = Current.Kind == SyntaxKind.LetKeyword ? SyntaxKind.LetKeyword
                         : Current.Kind == SyntaxKind.ConstKeyword ? SyntaxKind.ConstKeyword
                         : SyntaxKind.VarKeyword;
            var keyword = MatchToken(expected);            // C# 式：`const int x = 10;`（类型前置；const 才有此形式，let/var 无 C# 对应写法）
            if (keyword.Kind == SyntaxKind.ConstKeyword &&
                Current.Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.IdentifierToken)
            {
                var csType = ParsePrefixTypeClause();
                var csIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                SyntaxToken? csEquals = null;
                ExpressionSyntax? csInitializer = null;
                if (Current.Kind == SyntaxKind.EqualsToken)
                {
                    csEquals = MatchToken(SyntaxKind.EqualsToken);
                    csInitializer = ParseExpression();
                }

                return new VariableDeclarationSyntax(_syntaxTree, keyword, csIdentifier, csType, csEquals, csInitializer);
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeClause = ParseOptionalTypeClause();
            var equals = Current.Kind == SyntaxKind.EqualsToken ? MatchToken(SyntaxKind.EqualsToken) : null;
            var initializer = equals == null ? null : ParseExpression();

            return new VariableDeclarationSyntax(_syntaxTree, keyword, identifier, typeClause, equals, initializer);
        }

        protected TypeClauseSyntax? ParseOptionalTypeClause()
        {
            if (Current.Kind != SyntaxKind.ColonToken)
            {
                return null;
            }

            return ParseTypeClause();
        }

        protected TypeClauseSyntax ParseTypeClause()
        {
            var colonToken = MatchToken(SyntaxKind.ColonToken);

            // 函数类型（6e-M22 C2）：`f: (A, B) -> R`——冒号属类型子句前缀，其后才是类型本体
            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsFunctionTypeStart())
            {
                return ParseFunctionTypeClause();
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            TypeClauseSyntax type = ParseGenericTypeSuffix(colonToken, identifier);
            type = WrapArrayTypeClause(colonToken, type);

            return type;
        }

        /// <summary>数组后缀包裹：`int[]` / `int[][]`（ElementType 递归嵌套）。</summary>
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

        /// <summary>
        /// 泛型后缀（6e-M20）：`List&lt;int&gt;` / `List&lt;List&lt;int&gt;&gt;`。
        /// 仅当 `&lt;` 后紧跟合法类型实参首 token 时按泛型解析（类型位置无歧义）；非泛型回退普通类型名。
        /// </summary>
        private TypeClauseSyntax ParseGenericTypeSuffix(SyntaxToken? colonToken, SyntaxToken identifier)
        {
            // 标识符已被消费：当前 token 为 `<` 才按泛型解析（类型位置无歧义）
            if (Current.Kind != SyntaxKind.LessToken)
            {
                return new TypeClauseSyntax(_syntaxTree, colonToken, identifier);
            }

            var lessThanToken = NextToken();
            var arguments = ImmutableArray.CreateBuilder<TypeClauseSyntax>();

            while (true)
            {
                // 实参元素：类型子句（标识符 + 递归泛型/数组后缀）
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

        /// <summary>单个类型实参：标识符 + 泛型/数组后缀（类型参数表与类型实参表共用）。</summary>
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

        /// <summary>
        /// 函数类型（6e-M22 C2）：`(A, B) -> R`。参数与返回类型均支持嵌套函数类型/泛型/数组；
        /// 返回类型无冒号前缀（区别于常规 TypeClause）。
        /// </summary>
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
                        NextToken();
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

        /// <summary>
        /// 函数类型前瞻（6e-M22 C2）：从当前 `(` 起扫描平衡括号，内容仅允许类型形态 token
        /// （标识符/逗号/泛型角/数组方括号），闭合后须紧跟 `-&gt;`。
        /// </summary>
        private bool IsFunctionTypeStart()
        {
            if (!AllowArrowFunctionTypes() || Peek(0).Kind != SyntaxKind.OpenParenthesisToken)
            {
                return false;
            }

            var depth = 0;
            var i = 0;

            while (i < 128)
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
                    case SyntaxKind.ArrowToken: // 嵌套函数类型内层箭头：((int) -> int) -> int
                        i++;
                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 前瞻扫描平衡的 `&lt;…&gt;` 区域（6e-M20）：从 offset 处（须为 LessToken）扫描，
        /// 返回匹配 `&gt;` 之后一个 token 的下标；区域含非法 token（运算符等）返回 -1。
        /// `&gt;&gt;` 视为连续两个闭合角（嵌套泛型收尾，词法层合并所致）。
        /// </summary>
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

        /// <summary>C# 式声明中「泛型类型 + 成员名」判定：`IEnumerator&lt;T&gt; GetEnumerator` / `List&lt;int&gt; MakeList`。</summary>
        private bool IsGenericTypeNameAhead()
        {
            var afterAngles = ScanBalancedAngleSuffix(1);
            return afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.IdentifierToken;
        }

        /// <summary>消费一个闭合 `&gt;`；遇 `&gt;&gt;` 拆分为合成 GreaterToken 入队（嵌套泛型收尾）。</summary>
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
                // `>>` → 两个 `>`：当前槽位替换为第一个合成 `>`，第二个入队
                var shiftRight = NextToken();
                var second = new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, shiftRight.Position + 1, ">", null, ImmutableArray<SyntaxTrivia>.Empty, shiftRight.TrailingTrivia);
                _syntheticTokens.Enqueue(second);
                return new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, shiftRight.Position, ">", null, shiftRight.LeadingTrivia, ImmutableArray<SyntaxTrivia>.Empty);
            }

            _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.GreaterToken);
            return new SyntaxToken(_syntaxTree, SyntaxKind.GreaterToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        /// <summary>泛型类型实参表：`&lt;int, List&lt;string&gt;&gt;`（调用/new 的显式实参，6e-M20 首版仅显式）；当前须为 `&lt;`。</summary>
        private TypeArgumentListSyntax ParseTypeArgumentList()
        {
            var lessThanToken = NextToken(); // <
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

        /// <summary>泛型类型参数列表：`&lt;T, U&gt;`（6e-M20）；当前非 `&lt;` 返回 null。</summary>
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

        /// <summary>区分类型参数表与方法体/比较表达式中的 `<`：`<T>` 或 `<T, U ...>` 形态才成立。</summary>
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
                        // 嵌套泛型实参的 `>>` 不可能出现在参数表（无嵌套），视为非法
                        return false;

                    default:
                        return false;
                }
            }
        }

        /// <summary>泛型约束子句序列（0 个或多个）：`where T: Base, IFoo&lt;T&gt; where U: new()`。</summary>
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
                       Current.Kind == SyntaxKind.ClassKeyword)
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

        /// <summary>约束类型：普通/泛型/数组类型，或 `new()` / `class` / `struct` 特殊约束。</summary>
        private TypeClauseSyntax ParseConstraintType()
        {
            if (Current.Kind == SyntaxKind.NewKeyword &&
                Peek(1).Kind == SyntaxKind.OpenParenthesisToken &&
                Peek(2).Kind == SyntaxKind.CloseParenthesisToken)
            {
                // `new()` 无参构造约束：合成为名为 new() 的伪类型标识符（绑定层按文本识别）
                var newKeyword = NextToken();
                NextToken(); // (
                NextToken(); // )
                var synthesized = new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, newKeyword.Position, "new()", "new()", ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new TypeClauseSyntax(_syntaxTree, null, synthesized);
            }

            // `class` 特殊约束：关键字合成为同名标识符（绑定层按文本识别；struct 约束待 struct 落地后扩展）
            if (Current.Kind == SyntaxKind.ClassKeyword)
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

        // 基类/基接口子句：接受 `: T` 或 `extends T` 前缀
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

        // 基类型名子句；prefixToken 为 null 时（逗号分隔的后续基接口）使用空前缀
        private TypeClauseSyntax CreateBaseTypeClause(SyntaxToken? prefixToken)
        {
            if (prefixToken == null)
            {
                prefixToken = new SyntaxToken(_syntaxTree, SyntaxKind.ColonToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            // 泛型基类/基接口：`extends List<T>`（6e-M20）
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

        protected virtual StatementSyntax ParseIfStatement()
        {
            var keyword = MatchToken(SyntaxKind.IfKeyword);
            var condition = ParseExpression();
            var statement = ParseStatement();
            var elseClause = ParseOptionalElseClause();

            return new IfStatementSyntax(_syntaxTree, keyword, condition, statement, elseClause);
        }

        protected ElseClauseSyntax? ParseOptionalElseClause()
        {
            if (Current.Kind != SyntaxKind.ElseKeyword)
            {
                return null;
            }

            var keyword = NextToken();
            var statement = ParseStatement();

            return new ElseClauseSyntax(_syntaxTree, keyword, statement);
        }

        protected virtual StatementSyntax ParseWhileStatement()
        {
            var keyword = MatchToken(SyntaxKind.WhileKeyword);
            var condition = ParseExpression();
            var body = ParseStatement();

            return new WhileStatementSyntax(_syntaxTree, keyword, condition, body);
        }

        protected virtual StatementSyntax ParseDoWhileStatement()
        {
            var doKeyword = MatchToken(SyntaxKind.DoKeyword);
            var body = ParseStatement();
            var whileKeyword = MatchToken(SyntaxKind.WhileKeyword);
            var condition = ParseExpression();

            return new DoWhileStatementSyntax(_syntaxTree, doKeyword, body, whileKeyword, condition);
        }

        protected virtual StatementSyntax ParseForStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForKeyword);

            // for (init; cond; update) —— C 风格（括号内以顶层 ; 分隔）
            if (Current.Kind == SyntaxKind.OpenParenthesisToken && IsCSStyleForHeader())
            {
                return ParseCSStyleForStatement(keyword);
            }

            return ParseRangeForStatement(keyword);
        }

        /// <summary>for 头初始化钩子：Cocoa 支持 `var/let/const` 声明与表达式；C# 方言追加类型前置声明并拒绝 `let`。</summary>
        protected virtual StatementSyntax? ParseForInitializer()
        {
            if (Current.Kind == SyntaxKind.LetKeyword ||
                Current.Kind == SyntaxKind.VarKeyword ||
                Current.Kind == SyntaxKind.ConstKeyword)
            {
                return ParseVariableDeclaration();
            }

            if (Current.Kind != SyntaxKind.SemicolonToken)
            {
                return new ExpressionStatementSyntax(_syntaxTree, ParseExpression());
            }

            return null;
        }

        // 扫描括号内的 token 消歧：含顶层 ; → C 风格；含 to → range 次数/变量循环。
        protected bool IsCSStyleForHeader()
        {
            var index = _position;
            var depth = 0;

            while (index < _tokens.Length)
            {
                var token = _tokens[index];

                if (token.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    depth++;
                }
                else if (token.Kind == SyntaxKind.CloseParenthesisToken)
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
                else if (depth >= 1 && token.Kind == SyntaxKind.SemicolonToken)
                {
                    return true;
                }
                else if (depth >= 1 && token.Kind == SyntaxKind.ToKeyword)
                {
                    return false;
                }

                index++;
            }

            return false;
        }

        protected StatementSyntax ParseRangeForStatement(SyntaxToken keyword)
        {
            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

            // 循环变量声明关键字：仅 var 合法；let/const 报错后按 var 恢复继续解析
            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword ||
                Current.Kind == SyntaxKind.LetKeyword ||
                Current.Kind == SyntaxKind.ConstKeyword)
            {
                var keywordToken = NextToken();
                if (keywordToken.Kind != SyntaxKind.VarKeyword)
                {
                    _diagnostics.ReportError(keywordToken.Location, $"for 循环变量只能用 var 声明（不能用 {keywordToken.Text}）。");
                }

                varKeyword = keywordToken;
            }

            // 循环变量标识符 + '='；省略则为纯次数循环 for (1 to 10)
            SyntaxToken? identifier = null;
            SyntaxToken? equalsToken = null;
            if (varKeyword != null ||
                Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.EqualsToken)
            {
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
            }

            var lowerBound = ParseExpression();
            var toKeyword = MatchToken(SyntaxKind.ToKeyword);
            var upperBound = ParseExpression();

            // 可选步长：`for i = 0 to 10 step 2`
            SyntaxToken? stepKeyword = null;
            ExpressionSyntax? step = null;
            if (Current.Kind == SyntaxKind.StepKeyword)
            {
                stepKeyword = NextToken();
                step = ParseExpression();
            }

            SyntaxToken? closeParenToken = null;
            if (openParenToken != null)
            {
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var body = ParseStatement();

            return new ForStatementSyntax(_syntaxTree, keyword, openParenToken, varKeyword, identifier, equalsToken, lowerBound, toKeyword, upperBound, stepKeyword, step, closeParenToken, body);
        }

        protected StatementSyntax ParseCSStyleForStatement(SyntaxToken keyword)
        {
            var openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);

            var init = ParseForInitializer();

            var semicolonToken1 = MatchToken(SyntaxKind.SemicolonToken);

            ExpressionSyntax? condition = null;
            if (Current.Kind != SyntaxKind.SemicolonToken)
            {
                condition = ParseExpression();
            }

            var semicolonToken2 = MatchToken(SyntaxKind.SemicolonToken);

            ExpressionSyntax? update = null;
            if (Current.Kind != SyntaxKind.CloseParenthesisToken)
            {
                update = ParseExpression();
            }

            var closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var body = ParseStatement();

            return new CSStyleForStatementSyntax(_syntaxTree, keyword, openParenToken, init, semicolonToken1, condition, semicolonToken2, update, closeParenToken, body);
        }

        protected virtual StatementSyntax ParseForeachStatement()
        {
            var keyword = MatchToken(SyntaxKind.ForeachKeyword);

            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

            // 循环变量声明关键字：仅 var 合法；let/const 报错后按 var 恢复继续解析
            SyntaxToken? varKeyword = null;
            if (Current.Kind == SyntaxKind.VarKeyword ||
                Current.Kind == SyntaxKind.LetKeyword ||
                Current.Kind == SyntaxKind.ConstKeyword)
            {
                var keywordToken = NextToken();
                if (keywordToken.Kind != SyntaxKind.VarKeyword)
                {
                    _diagnostics.ReportError(keywordToken.Location, $"foreach 循环变量只能用 var 声明（不能用 {keywordToken.Text}）。");
                }

                varKeyword = keywordToken;
            }

            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var inKeyword = MatchToken(SyntaxKind.InKeyword);
            var collection = ParseExpression();

            SyntaxToken? closeParenToken = null;
            if (openParenToken != null)
            {
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var body = ParseStatement();

            return new ForeachStatementSyntax(_syntaxTree, keyword, openParenToken, varKeyword, identifier, inKeyword, collection, closeParenToken, body);
        }

        protected virtual StatementSyntax ParseSwitchStatement()
        {
            var keyword = MatchToken(SyntaxKind.SwitchKeyword);

            SyntaxToken? openParenToken = null;
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenToken = NextToken();
            }

            var expression = ParseExpression();

            SyntaxToken? closeParenToken = null;
            if (openParenToken != null)
            {
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }

            var openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);

            var sections = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
            while (Current.Kind == SyntaxKind.CaseKeyword || Current.Kind == SyntaxKind.DefaultKeyword)
            {
                sections.Add(ParseSwitchSection());
            }

            var closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);

            return new SwitchStatementSyntax(_syntaxTree, keyword, openParenToken, expression, closeParenToken, openBraceToken, sections.ToImmutable(), closeBraceToken);
        }

        protected SwitchSectionSyntax ParseSwitchSection()
        {
            if (Current.Kind == SyntaxKind.DefaultKeyword)
            {
                var defaultKeyword = MatchToken(SyntaxKind.DefaultKeyword);
                var colon = MatchToken(SyntaxKind.ColonToken);
                var sectionBody = ParseSwitchSectionBody();

                return new DefaultClauseSyntax(_syntaxTree, defaultKeyword, colon, sectionBody);
            }

            var caseKeyword = MatchToken(SyntaxKind.CaseKeyword);

            var valuesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            var parseNextValue = true;
            while (parseNextValue)
            {
                var value = ParseExpression();
                valuesAndSeparators.Add(value);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    valuesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextValue = false;
                }
            }

            var values = new SeparatedSyntaxList<ExpressionSyntax>(valuesAndSeparators.ToImmutable());

            SyntaxToken? whenKeyword = null;
            ExpressionSyntax? whenCondition = null;
            if (Current.Kind == SyntaxKind.WhenKeyword)
            {
                whenKeyword = MatchToken(SyntaxKind.WhenKeyword);
                whenCondition = ParseExpression();
            }

            var colonToken = MatchToken(SyntaxKind.ColonToken);
            var body = ParseSwitchSectionBody();

            return new CaseClauseSyntax(_syntaxTree, caseKeyword, values, whenKeyword, whenCondition, colonToken, body);
        }

        /// <summary>节体：叠标（下一个 case/default 或闭合 `}`）时为合成空块。</summary>
        private StatementSyntax ParseSwitchSectionBody()
        {
            if (Current.Kind == SyntaxKind.CaseKeyword ||
                Current.Kind == SyntaxKind.DefaultKeyword ||
                Current.Kind == SyntaxKind.CloseBraceToken)
            {
                var emptyOpen = new SyntaxToken(_syntaxTree, SyntaxKind.OpenBraceToken, Current.Position, "{", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                var emptyClose = new SyntaxToken(_syntaxTree, SyntaxKind.CloseBraceToken, Current.Position, "}", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                return new BlockStatementSyntax(_syntaxTree, emptyOpen, ImmutableArray<StatementSyntax>.Empty, emptyClose);
            }

            return ParseStatement();
        }

        private StatementSyntax ParseBreakStatement()
        {
            var keyword = MatchToken(SyntaxKind.BreakKeyword);

            return new BreakStatementSyntax(_syntaxTree, keyword);
        }

        private StatementSyntax ParseContinueStatement()
        {
            var keyword = MatchToken(SyntaxKind.ContinueKeyword);

            return new ContinueStatementSyntax(_syntaxTree, keyword);
        }

        private StatementSyntax ParseReturnStatement()
        {
            var keyword = MatchToken(SyntaxKind.ReturnKeyword);
            var keywordLine = _text.GetLineIndex(keyword.Span.Start);
            var currentLine = _text.GetLineIndex(Current.Span.Start);
            var isEof = Current.Kind == SyntaxKind.EndOfFileToken;
            var sameLine = !isEof && keywordLine == currentLine;
            var expression = sameLine ? ParseExpression() : null;

            return new ReturnStatementSyntax(_syntaxTree, keyword, expression);
        }

        private StatementSyntax ParseExpressionStatement()
        {
            var expression = ParseExpression();

            return new ExpressionStatementSyntax(_syntaxTree, expression);
        }

        protected ExpressionSyntax ParseExpression()
        {
            return ParseAssignmentExpression();
        }

        private ExpressionSyntax ParseAssignmentExpression()
        {
            if (Peek(0).Kind == SyntaxKind.IdentifierToken)
            {
                switch (Peek(1).Kind)
                {
                    case SyntaxKind.PlusEqualsToken:
                    case SyntaxKind.MinusEqualsToken:
                    case SyntaxKind.StarEqualsToken:
                    case SyntaxKind.SlashEqualsToken:
                    case SyntaxKind.PercentEqualsToken:
                    case SyntaxKind.ShiftLeftEqualsToken:
                    case SyntaxKind.ShiftRightEqualsToken:
                    case SyntaxKind.AmpersandEqualsToken:
                    case SyntaxKind.PipeEqualsToken:
                    case SyntaxKind.HatEqualsToken:
                    case SyntaxKind.EqualsToken:
                    {
                        var identifierToken = NextToken();
                        var operatorToken = NextToken();
                        var right = ParseAssignmentExpression();
                        var target = new NameExpressionSyntax(_syntaxTree, identifierToken);

                        return new AssignmentExpressionSyntax(_syntaxTree, target, operatorToken, right);
                    }
                }
            }

            var expression = ParseBinaryExpression();

            switch (Current.Kind)
            {
                case SyntaxKind.PlusEqualsToken:
                case SyntaxKind.MinusEqualsToken:
                case SyntaxKind.StarEqualsToken:
                case SyntaxKind.SlashEqualsToken:
                case SyntaxKind.PercentEqualsToken:
                case SyntaxKind.ShiftLeftEqualsToken:
                case SyntaxKind.ShiftRightEqualsToken:
                case SyntaxKind.AmpersandEqualsToken:
                case SyntaxKind.PipeEqualsToken:
                case SyntaxKind.HatEqualsToken:
                case SyntaxKind.EqualsToken:
                {
                    var operatorToken = NextToken();
                    var right = ParseAssignmentExpression();

                    return new AssignmentExpressionSyntax(_syntaxTree, expression, operatorToken, right);
                }
            }

            if (Current.Kind == SyntaxKind.QuestionToken)
            {
                return ParseConditionalExpression(expression);
            }

            return expression;
        }

        private ExpressionSyntax ParseConditionalExpression(ExpressionSyntax condition)
        {
            var questionToken = MatchToken(SyntaxKind.QuestionToken);
            var whenTrue = ParseExpression();
            var colonToken = MatchToken(SyntaxKind.ColonToken);
            var whenFalse = ParseExpression();

            return new ConditionalExpressionSyntax(_syntaxTree, condition, questionToken, whenTrue, colonToken, whenFalse);
        }

        private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
        {
            ExpressionSyntax left;
            var unaryOperatorPrecedence = Current.Kind.GetUnaryOperatorPrecedence();
            if (unaryOperatorPrecedence != 0 && unaryOperatorPrecedence >= parentPrecedence)
            {
                var operatorToken = NextToken();
                var operand = ParseBinaryExpression(unaryOperatorPrecedence);
                left = new UnaryExpressionSyntax(_syntaxTree, operatorToken, operand);
            }
            else
            {
                left = ParsePrimaryExpression();
                left = ParsePostfixExpressions(left);
            }

            while (true)
            {
                var precedence = Current.Kind.GetBinaryOperatorPrecedence();
                if (precedence == 0 || precedence <= parentPrecedence)
                {
                    break;
                }

                // 6e-M19 M5-b：is / as 类型测试与转换（与关系运算同优先级；目标为单标识符类型名，与 cast 先例一致）
                if (Current.Kind == SyntaxKind.IsKeyword)
                {
                    var isKeyword = NextToken();
                    var isTypeName = MatchToken(SyntaxKind.IdentifierToken);
                    left = new IsExpressionSyntax(_syntaxTree, left, isKeyword, isTypeName);
                    continue;
                }

                if (Current.Kind == SyntaxKind.AsKeyword)
                {
                    var asKeyword = NextToken();
                    var asTypeName = MatchToken(SyntaxKind.IdentifierToken);
                    left = new AsExpressionSyntax(_syntaxTree, left, asKeyword, asTypeName);
                    continue;
                }

                var operatorToken = NextToken();
                var right = ParseBinaryExpression(precedence);
                left = new BinaryExpressionSyntax(_syntaxTree, left, operatorToken, right);
            }

            return left;
        }

        private ExpressionSyntax ParsePrimaryExpression()
        {
            switch (Current.Kind)
            {
                case SyntaxKind.OpenParenthesisToken:
                    if (IsLambdaParenStart())
                    {
                        return ParseLambdaExpression();
                    }

                    if (IsCastStart())
                    {
                        return ParseCastExpression();
                    }

                    return ParseParenthesizedExpression();

                case SyntaxKind.NewKeyword:
                    return ParseArrayCreationExpression();

                case SyntaxKind.FalseKeyword:
                case SyntaxKind.TrueKeyword:
                    return ParseBooleanLiteral();

                case SyntaxKind.NullKeyword:
                    return ParseNullLiteral();

                case SyntaxKind.NumberToken:
                case SyntaxKind.DoubleToken:
                    return ParseNumberLiteral();

                case SyntaxKind.StringToken:
                case SyntaxKind.VerbatimStringToken:
                case SyntaxKind.RawStringToken:
                    return ParseStringLiteral();

                case SyntaxKind.InterpolatedStringToken:
                    return ParseInterpolatedStringExpression();

                case SyntaxKind.CharToken:
                    return ParseCharLiteral();

                case SyntaxKind.ThisKeyword:
                    return new ThisExpressionSyntax(_syntaxTree, NextToken());

                case SyntaxKind.BaseKeyword:
                    return new BaseExpressionSyntax(_syntaxTree, NextToken());

                case SyntaxKind.IdentifierToken:
                default:
                    // 免括号单参 lambda `x => …`（6e-M22 C2，仅 .cs）
                    if (AllowParenlessLambda() &&
                        Current.Kind == SyntaxKind.IdentifierToken &&
                        Peek(1).Kind == SyntaxKind.FatArrowToken)
                    {
                        return ParseLambdaExpression();
                    }

                    return ParseNameOrCallExpression();
            }
        }

        /// <summary>lambda 前瞻（6e-M22 C2）：平衡括号参数表 + 显式类型/隐式标识符/空参，闭合后紧跟 `=&gt;`。</summary>
        private bool IsLambdaParenStart()
        {
            if (Peek(0).Kind != SyntaxKind.OpenParenthesisToken)
            {
                return false;
            }

            var depth = 0;
            var i = 0;

            while (i < 128)
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
                            return Peek(i).Kind == SyntaxKind.FatArrowToken;
                        }

                        break;

                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.ColonToken:
                    case SyntaxKind.LessToken:
                    case SyntaxKind.GreaterToken:
                    case SyntaxKind.ShiftRightToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.CloseBracketToken:
                        i++;
                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Lambda 解析（6e-M22 C2）：`(x: int, y) =&gt; expr|block`、`() =&gt; expr`、免括号 `x =&gt; expr`（.cs）。
        /// 参数复用 ParseParameter（双语法形态）；隐式参数仅 .cs 且不可与显式混用。
        /// </summary>
        private ExpressionSyntax ParseLambdaExpression()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();
            SyntaxToken? openParenthesisToken = null;
            SyntaxToken? closeParenthesisToken = null;
            var hasExplicitParameterTypes = true;

            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                openParenthesisToken = NextToken();
                var sawExplicit = false;
                var sawImplicit = false;

                if (Current.Kind != SyntaxKind.CloseParenthesisToken)
                {
                    while (true)
                    {
                        if (Current.Kind == SyntaxKind.IdentifierToken &&
                            (Peek(1).Kind == SyntaxKind.CommaToken ||
                             Peek(1).Kind == SyntaxKind.CloseParenthesisToken))
                        {
                            // 隐式类型参数：裸标识符（6e-M22 C2，仅 .cs）
                            if (!AllowImplicitLambdaParameters())
                            {
                                ReportError(Current.Location, "lambda 参数须显式标注类型，如 '(x: int) => …'。");
                            }

                            sawImplicit = true;
                            var identifier = MatchToken(SyntaxKind.IdentifierToken);
                            var missingType = new TypeClauseSyntax(
                                _syntaxTree,
                                null,
                                new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                            nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
                        }
                        else
                        {
                            // 显式参数：Cocoa `name: Type` / C# `Type name`（ParseParameter 双形态）
                            sawExplicit = true;
                            nodesAndSeparators.Add(ParseParameter());
                        }

                        if (Current.Kind == SyntaxKind.CommaToken)
                        {
                            nodesAndSeparators.Add(NextToken());
                            continue;
                        }

                        break;
                    }
                }

                if (sawExplicit && sawImplicit)
                {
                    ReportError(openParenthesisToken.Location, "lambda 参数须全部显式标注或全部隐式，不可混用。");
                }

                hasExplicitParameterTypes = !sawImplicit;
                closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            }
            else
            {
                // 免括号单参：恒为隐式类型（仅 .cs）
                hasExplicitParameterTypes = false;
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var missingType = new TypeClauseSyntax(
                    _syntaxTree,
                    null,
                    new SyntaxToken(_syntaxTree, SyntaxKind.IdentifierToken, Current.Position, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
                nodesAndSeparators.Add(new ParameterSyntax(_syntaxTree, identifier, missingType));
            }

            var arrowToken = MatchToken(SyntaxKind.FatArrowToken);

            SyntaxNode body = Current.Kind == SyntaxKind.OpenBraceToken
                ? ParseBlockStatement()
                : ParseExpression();

            return new LambdaExpressionSyntax(
                _syntaxTree,
                openParenthesisToken,
                new SeparatedSyntaxList<ParameterSyntax>(nodesAndSeparators.ToImmutable()),
                closeParenthesisToken,
                hasExplicitParameterTypes,
                arrowToken,
                body);
        }

        private bool IsCastStart()
        {
            if (Peek(1).Kind != SyntaxKind.IdentifierToken || Peek(2).Kind != SyntaxKind.CloseParenthesisToken)
            {
                return false;
            }

            switch (Peek(3).Kind)
            {
                case SyntaxKind.IdentifierToken:
                case SyntaxKind.NumberToken:
                case SyntaxKind.DoubleToken:
                case SyntaxKind.StringToken:
                case SyntaxKind.VerbatimStringToken:
                case SyntaxKind.RawStringToken:
                case SyntaxKind.InterpolatedStringToken:
                case SyntaxKind.CharToken:
                case SyntaxKind.OpenParenthesisToken:
                case SyntaxKind.NewKeyword:
                case SyntaxKind.TrueKeyword:
                case SyntaxKind.FalseKeyword:
                case SyntaxKind.NullKeyword:
                case SyntaxKind.BangToken:
                case SyntaxKind.MinusToken:
                case SyntaxKind.PlusToken:
                case SyntaxKind.TildeToken:
                    return true;
                default:
                    return false;
            }
        }

        private ExpressionSyntax ParseCastExpression()
        {
            var openParenthesisToken = NextToken();
            var typeName = NextToken();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
            var expression = ParseBinaryExpression(6); // 一元优先级：cast 体只消费一元表达式
            return new CastExpressionSyntax(_syntaxTree, openParenthesisToken, typeName, closeParenthesisToken, expression);
        }

        private ExpressionSyntax ParseParenthesizedExpression()
        {
            var left = MatchToken(SyntaxKind.OpenParenthesisToken);
            var expression = ParseExpression();
            var right = MatchToken(SyntaxKind.CloseParenthesisToken);

            return new ParenthesizedExpressionSyntax(_syntaxTree, left, expression, right);
        }

        private ExpressionSyntax ParseBooleanLiteral()
        {
            var isTrue = Current.Kind == SyntaxKind.TrueKeyword;
            var keywordToken = isTrue ? MatchToken(SyntaxKind.TrueKeyword) : MatchToken(SyntaxKind.FalseKeyword);

            return new LiteralExpressionSyntax(_syntaxTree, keywordToken, isTrue);
        }

        /// <summary>6e-M19 M5-a：null 字面量（值 null，绑定层赋 TypeSymbol.Null）。</summary>
        private ExpressionSyntax ParseNullLiteral()
        {
            var keywordToken = MatchToken(SyntaxKind.NullKeyword);
            return new LiteralExpressionSyntax(_syntaxTree, keywordToken, (object)null!);
        }

        private ExpressionSyntax ParseNumberLiteral()
        {
            var numberToken = Current.Kind == SyntaxKind.DoubleToken
                ? MatchToken(SyntaxKind.DoubleToken)
                : MatchToken(SyntaxKind.NumberToken);

            return new LiteralExpressionSyntax(_syntaxTree, numberToken);
        }

        private ExpressionSyntax ParseStringLiteral()
        {
            var stringToken = Current.Kind is SyntaxKind.StringToken or SyntaxKind.VerbatimStringToken or SyntaxKind.RawStringToken
                ? NextToken()
                : MatchToken(SyntaxKind.StringToken);

            return new LiteralExpressionSyntax(_syntaxTree, stringToken);
        }

        /// <summary>插值字符串：字面量段合成 StringToken；洞逐个子词法 + 子解析（绝对 Span，诊断并入主 bag）。</summary>
        private ExpressionSyntax ParseInterpolatedStringExpression()
        {
            var interpolatedToken = NextToken();
            var parts = (InterpolatedStringPart[])interpolatedToken.Value!;
            var contents = ImmutableArray.CreateBuilder<InterpolatedStringContentSyntax>();

            foreach (var part in parts)
            {
                if (part.Kind == InterpolatedStringPartKind.Literal)
                {
                    var textToken = new SyntaxToken(_syntaxTree, SyntaxKind.StringToken, part.Start, part.Text, part.Text, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
                    contents.Add(new InterpolatedStringTextSyntax(_syntaxTree, textToken));
                }
                else
                {
                    contents.Add(ParseHoleExpression(part.Start, part.End));
                }
            }

            return new InterpolatedStringExpressionSyntax(_syntaxTree, interpolatedToken, contents.ToImmutable());
        }

        /// <summary>从洞的绝对 Span 子词法并解析（表达式 + 可选对齐 <c>,N</c> + 格式 <c>:fmt</c>；同一 SyntaxTree → 诊断定位正确）。</summary>
        private InterpolationSyntax ParseHoleExpression(int start, int end)
        {
            var lexer = new Lexer(_syntaxTree, start);
            var tokens = new List<SyntaxToken>();
            SyntaxToken token;
            do
            {
                token = lexer.Lex();
                tokens.Add(token);
            } while (token.Kind != SyntaxKind.EndOfFileToken && token.Position < end);

            if (tokens.Count == 0 || tokens[^1].Kind != SyntaxKind.EndOfFileToken)
            {
                tokens.Add(new SyntaxToken(_syntaxTree, SyntaxKind.EndOfFileToken, end, "\0", null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty));
            }

            var holeParser = CreateSubParser(tokens.ToImmutableArray());
            var expression = holeParser.ParseExpression();

            SyntaxToken? commaToken = null;
            ExpressionSyntax? alignment = null;
            SyntaxToken? colonToken = null;
            SyntaxToken? formatToken = null;

            if (holeParser.Current.Kind == SyntaxKind.CommaToken)
            {
                commaToken = holeParser.NextToken();
                alignment = holeParser.ParseAlignment();
            }

            if (holeParser.Current.Kind == SyntaxKind.ColonToken)
            {
                colonToken = holeParser.NextToken();
                formatToken = ParseFormatSpecifier(holeParser, end);
            }

            _diagnostics.AddRange(holeParser.Diagnostics);
            return new InterpolationSyntax(_syntaxTree, expression, commaToken, alignment, colonToken, formatToken);
        }

        /// <summary>对齐宽度：<c>N</c> / <c>-N</c>（有符号整数字面量）。</summary>
        private ExpressionSyntax ParseAlignment()
        {
            var negate = Current.Kind == SyntaxKind.MinusToken;
            if (negate)
            {
                NextToken();
            }

            if (Current.Kind != SyntaxKind.NumberToken)
            {
                return new LiteralExpressionSyntax(_syntaxTree, MatchToken(SyntaxKind.NumberToken));
            }

            var numberToken = NextToken();
            var value = (int)numberToken.Value!;
            if (negate)
            {
                value = -value;
            }

            return new LiteralExpressionSyntax(_syntaxTree, numberToken, value);
        }

        /// <summary>格式说明符：<c>:</c> 之后到洞尾（不含闭合 <c>}</c>）的原始文本（C# 式无引号，如 <c>F2</c>/<c>g</c>/<c>0.00</c>）。</summary>
        private SyntaxToken ParseFormatSpecifier(ParserCore holeParser, int end)
        {
            var formatStart = holeParser.Current.Position;
            var length = end - formatStart;
            if (length > 0 && _text[end - 1] == '}')
            {
                length--;
            }

            var formatText = length > 0 ? _text.ToString(formatStart, length) : "";
            formatText = formatText.Trim();
            return new SyntaxToken(_syntaxTree, SyntaxKind.StringToken, formatStart, formatText, formatText, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
        }

        private ExpressionSyntax ParseCharLiteral()
        {
            var charToken = MatchToken(SyntaxKind.CharToken);

            return new LiteralExpressionSyntax(_syntaxTree, charToken);
        }

        private ExpressionSyntax ParseNameOrCallExpression()
        {
            // 泛型调用：`Swap<int>(a, b)`（6e-M20 首版仅显式实参）——前瞻 `ident <…> (` 才按泛型解析，
            // `a < b` 比较表达式不受影响（扫描遇非法 token 或闭合角后非 `(` 即回退）
            if (Peek(1).Kind == SyntaxKind.LessToken)
            {
                var afterAngles = ScanBalancedAngleSuffix(1);
                if (afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.OpenParenthesisToken)
                {
                    return ParseGenericCallExpression();
                }
            }

            if (Peek(0).Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken)
            {
                return ParseCallExpression();
            }

            return ParseNameExpression();
        }

        /// <summary>泛型函数调用：`Swap<int>(a, b)`（显式类型实参，6e-M20）。</summary>
        private ExpressionSyntax ParseGenericCallExpression()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var typeArguments = ParseTypeArgumentList();
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var arguments = ParseArguments();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            return new CallExpressionSyntax(_syntaxTree, identifier, typeArguments, openParenthesisToken, arguments, closeParenthesisToken);
        }

        private ExpressionSyntax ParseCallExpression()
        {
            var identifier = MatchToken(SyntaxKind.IdentifierToken);
            var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
            var arguments = ParseArguments();
            var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

            return new CallExpressionSyntax(_syntaxTree, identifier, typeArguments: null, openParenthesisToken, arguments, closeParenthesisToken);
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArguments()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextArgument = true;
            while (parseNextArgument &&
                Current.Kind != SyntaxKind.CloseParenthesisToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var expression = ParseExpression();
                nodesAndSeparators.Add(expression);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextArgument = false;
                }
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

        private ExpressionSyntax ParseNameExpression()
        {
            var identifierToken = MatchToken(SyntaxKind.IdentifierToken);

            return new NameExpressionSyntax(_syntaxTree, identifierToken);
        }

        private ExpressionSyntax ParsePostfixExpressions(ExpressionSyntax expression)
        {
            while (true)
            {
                if (Current.Kind == SyntaxKind.OpenBracketToken)
                {
                    var openBracketToken = NextToken();
                    var index = ParseExpression();
                    var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
                    expression = new ElementAccessExpressionSyntax(_syntaxTree, expression, openBracketToken, index, closeBracketToken);
                }
                else if (Current.Kind == SyntaxKind.DotToken)
                {
                    var dotToken = NextToken();
                    var identifierToken = MatchToken(SyntaxKind.IdentifierToken);

                    // 泛型成员调用：`list.Map<int>(f)`（6e-M20；前瞻 `<…> (` 消歧）
                    TypeArgumentListSyntax? memberTypeArguments = null;
                    if (Current.Kind == SyntaxKind.LessToken)
                    {
                        var afterAngles = ScanBalancedAngleSuffix(0);
                        if (afterAngles > 0 && Peek(afterAngles).Kind == SyntaxKind.OpenParenthesisToken)
                        {
                            memberTypeArguments = ParseTypeArgumentList();
                        }
                    }

                    if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                    {
                        var openParenthesisToken = NextToken();
                        var arguments = ParseArguments();
                        var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);
                        expression = new MemberCallExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken, memberTypeArguments, openParenthesisToken, arguments, closeParenthesisToken);
                    }
                    else if (memberTypeArguments != null)
                    {
                        _diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.OpenParenthesisToken);
                        expression = new MemberAccessExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken);
                    }
                    else
                    {
                        expression = new MemberAccessExpressionSyntax(_syntaxTree, expression, dotToken, identifierToken);
                    }
                }
                else if (Current.Kind == SyntaxKind.PlusPlusToken ||
                         Current.Kind == SyntaxKind.MinusMinusToken)
                {
                    var operatorToken = NextToken();
                    expression = new PostfixIncrementExpressionSyntax(_syntaxTree, expression, operatorToken);
                }
                else
                {
                    break;
                }
            }

            return expression;
        }

        private ExpressionSyntax ParseArrayCreationExpression()
        {
            var newKeyword = MatchToken(SyntaxKind.NewKeyword);
            var identifier = MatchToken(SyntaxKind.IdentifierToken);

            // 泛型类型实参：`new List<int>(args)`（6e-M20；`new` 后 `<` 无歧义）
            TypeArgumentListSyntax? typeArguments = null;
            if (Current.Kind == SyntaxKind.LessToken)
            {
                typeArguments = ParseTypeArgumentList();
            }

            // new Foo(args) —— 对象创建
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                var openParenthesisToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                var arguments = ParseArgumentList();
                var closeParenthesisToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                return new ObjectCreationExpressionSyntax(_syntaxTree, newKeyword, identifier, typeArguments, openParenthesisToken, arguments, closeParenthesisToken);
            }

            if (typeArguments != null)
            {
                // `new List<int>[n]`（泛型元素数组创建）暂不支持：报错后按普通数组恢复解析
                _diagnostics.ReportError(typeArguments.Location, "泛型数组创建 `new T<n>[...]` 暂不支持（6e-M20 后续）。");
            }

            var openBracketToken = MatchToken(SyntaxKind.OpenBracketToken);
            ExpressionSyntax? size = null;

            if (Current.Kind != SyntaxKind.CloseBracketToken)
            {
                size = ParseExpression();
            }

            var closeBracketToken = MatchToken(SyntaxKind.CloseBracketToken);
            SyntaxToken? openBraceToken = null;
            var elements = new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            SyntaxToken? closeBraceToken = null;

            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                openBraceToken = MatchToken(SyntaxKind.OpenBraceToken);
                elements = ParseArrayInitializerElements();
                closeBraceToken = MatchToken(SyntaxKind.CloseBraceToken);
            }

            return new ArrayCreationExpressionSyntax(_syntaxTree, newKeyword, identifier, openBracketToken, size, closeBracketToken, openBraceToken, elements, closeBraceToken);
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArgumentList()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextArgument = true;
            while (parseNextArgument &&
                Current.Kind != SyntaxKind.CloseParenthesisToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var argument = ParseExpression();
                nodesAndSeparators.Add(argument);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextArgument = false;
                }
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }

        private SeparatedSyntaxList<ExpressionSyntax> ParseArrayInitializerElements()
        {
            var nodesAndSeparators = ImmutableArray.CreateBuilder<SyntaxNode>();

            var parseNextElement = true;
            while (parseNextElement &&
                Current.Kind != SyntaxKind.CloseBraceToken &&
                Current.Kind != SyntaxKind.EndOfFileToken)
            {
                var element = ParseExpression();
                nodesAndSeparators.Add(element);

                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    var comma = MatchToken(SyntaxKind.CommaToken);
                    nodesAndSeparators.Add(comma);
                }
                else
                {
                    parseNextElement = false;
                }
            }

            return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators.ToImmutable());
        }
    }
}
