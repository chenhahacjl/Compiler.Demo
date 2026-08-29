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
    /// 方言差异经 virtual 钩子由子类覆写：CocoaParser（`.co`）与 CSharpParser（`.cs`，Cocoa.Core.CSharp）。
    /// 规约：基类不得出现方言分支；新语法落点 = 覆写各自钩子，逐字相同的进基类一次。
    /// </summary>
    internal abstract partial class ParserCore
    {
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
                case SyntaxKind.ThrowKeyword:
                    statement = ParseThrowStatement();
                    break;
                case SyntaxKind.TryKeyword:
                    statement = ParseTryStatement();
                    break;
                default:
                    // 方言原生"无关键字"语句（CSharpParser：类型前置局部变量 `int x`；CocoaParser：无此形态，遇 C# 式局部变量报错恢复，否则回落表达式语句）
                    statement = ParseDialectNativeStatement();
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

        /// <summary>方言原生局部变量声明（CocoaParser：`var/let/const` 类型后置；CSharpParser：`var`/`const 类型 名称`）。</summary>
        protected abstract StatementSyntax ParseVariableDeclaration();

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

            // `struct` 特殊约束：关键字合成为同名标识符（绑定层按文本识别；与 class 互斥由 Binder 约束校验报告）
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

        /// <summary>方言原生 for 语句（CocoaParser：次数循环 `for i = 0 to n [step k]`；CSharpParser：C 风格 `for(;;)`）。</summary>
        protected abstract StatementSyntax ParseForStatement();

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

        /// <summary>方言原生 foreach 语句（两方言均要求 `var` 循环变量；C# 强制括号，CO 允许省略）。</summary>
        protected abstract StatementSyntax ParseForeachStatement();

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

        protected StatementSyntax ParseExpressionStatement()
        {
            var expression = ParseExpression();

            return new ExpressionStatementSyntax(_syntaxTree, expression);
        }

        private StatementSyntax ParseThrowStatement()
        {
            var keyword = MatchToken(SyntaxKind.ThrowKeyword);
            var expression = ParseExpression();

            return new ThrowStatementSyntax(_syntaxTree, keyword, expression);
        }

        private StatementSyntax ParseTryStatement()
        {
            var keyword = MatchToken(SyntaxKind.TryKeyword);
            var tryBlock = ParseBlockStatement();

            var catches = ImmutableArray.CreateBuilder<CatchClauseSyntax>();
            while (Current.Kind == SyntaxKind.CatchKeyword)
            {
                var catchKeyword = MatchToken(SyntaxKind.CatchKeyword);
                MatchToken(SyntaxKind.OpenParenthesisToken);
                var identifier = MatchToken(SyntaxKind.IdentifierToken);
                var type = ParseTypeClause();
                MatchToken(SyntaxKind.CloseParenthesisToken);
                var body = ParseBlockStatement();
                catches.Add(new CatchClauseSyntax(_syntaxTree, catchKeyword, identifier, type, body));
            }

            FinallyClauseSyntax? finallyClause = null;
            if (Current.Kind == SyntaxKind.FinallyKeyword)
            {
                var finallyKeyword = MatchToken(SyntaxKind.FinallyKeyword);
                var body = ParseBlockStatement();
                finallyClause = new FinallyClauseSyntax(_syntaxTree, finallyKeyword, body);
            }

            return new TryStatementSyntax(_syntaxTree, keyword, tryBlock, catches.ToImmutable(), finallyClause);
        }

        protected ExpressionSyntax ParseExpression()
        {
            return ParseAssignmentExpression();
        }

    }
}
