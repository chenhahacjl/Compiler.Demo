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

            if (Current.Kind == SyntaxKind.StructKeyword)
            {
                // 6e-M26：struct（值类型）——复用类解析，classKeyword 承载 struct 关键字
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

        /// <summary>方言原生"无关键字"语句：C# 类型前置局部变量 `type name`；CO 无此形态（遇 C# 式局部变量报错恢复，否则回落表达式语句）。</summary>
        protected virtual StatementSyntax ParseDialectNativeStatement()
        {
            return ParseExpressionStatement();
        }

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
            SyntaxToken? equalsToken = null;

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
                equalsToken = MatchToken(SyntaxKind.EqualsToken);
            }

            var nameTokens = ParseQualifiedName();

            return new UsingDirectiveSyntax(_syntaxTree, usingKeyword, staticKeyword, aliasToken, equalsToken, nameTokens);
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
            var classKeyword = Current.Kind == SyntaxKind.StructKeyword
                ? MatchToken(SyntaxKind.StructKeyword)
                : MatchToken(SyntaxKind.ClassKeyword);
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
                if (!AllowClassImportBlock())
                {
                    ReportError(Current.Location, "C# 方言不支持 import 块；请用 `using` 指令 + extern P/Invoke（如 `[DllImport(\"kernel32.dll\")]`）。");
                }

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

        /// <summary>C# 方言是否允许类内 import 块（Cocoa 为 true，C# 为 false，须用 using + P/Invoke）。</summary>
        protected virtual bool AllowClassImportBlock() => true;

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

            return new PropertyDeclarationSyntax(_syntaxTree, modifiers, propertyKeyword: null, identifier, type, openBraceToken, getter, setter, closeBraceToken, ImmutableArray<ParameterSyntax>.Empty, equalsToken, initializer);
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
        /// delegate 声明（6e-M22）：双方言同形——名称在前，参数列表，返回类型冒号后置。
        /// `.co` `delegate H(x: i32): i32` / `.cs` `public delegate int H(int x);`
        /// </summary>
        private MemberSyntax ParseDelegateDeclaration(ImmutableArray<SyntaxToken> modifiers)
        {
            var delegateKeyword = MatchToken(SyntaxKind.DelegateKeyword);

            // 形态判别：标识符后跟 `(` → .co（无返回类型前置）；否则 → .cs（类型前置）
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
                // .co：`delegate Name(params) [: RetType]`
                identifier = MatchToken(SyntaxKind.IdentifierToken);
                openParenToken = MatchToken(SyntaxKind.OpenParenthesisToken);
                parameters = ParseParameterList();
                closeParenToken = MatchToken(SyntaxKind.CloseParenthesisToken);

                if (Current.Kind == SyntaxKind.ColonToken)
                    returnType = ParseTypeClause();
            }
            else
            {
                // .cs：`delegate [RetType] Name(params)`
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

            // 索引器：`property this[index: i32]: T { get {} set {} }`
            if (identifier.Text == "this" && Current.Kind == SyntaxKind.OpenBracketToken)
            {
                return ParseIndexerDeclaration(modifiers, propertyKeyword, identifier);
            }

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

        private MemberSyntax ParseIndexerDeclaration(ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier)
        {
            // 已匹配 `this`，当前为 `[`
            NextToken(); // 消耗 [
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

        /// <summary>方言原生参数（CocoaParser：`名称: 类型` 类型后置；CSharpParser：`类型 名称` 类型前置；均带 out/ref）。</summary>
        protected abstract ParameterSyntax ParseParameter();

    }
}
