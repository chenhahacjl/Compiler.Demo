using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Linq;
using System.Collections;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 诊断信息合集
    /// </summary>
    internal sealed class DiagnosticBag : IEnumerable<Diagnostic>
    {
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();

        public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void AddRange(IEnumerable<Diagnostic> diagnostics)
        {
            _diagnostics.AddRange(diagnostics);
        }

        internal void ReportError(TextLocation location, string message)
        {
            var diagnostic = Diagnostic.Error(location, message);
            _diagnostics.Add(diagnostic);
        }

        private void ReportWarning(TextLocation location, string message)
        {
            var diagnostic = Diagnostic.Warning(location, message);
            _diagnostics.Add(diagnostic);
        }

        public void ReportInvalidNumber(TextLocation location, string text, TypeSymbol type)
        {
            var message = $"The number '{text}' isn't valid '{type}'.";
            ReportError(location, message);
        }

        public void ReportBadCharacter(TextLocation location, char character)
        {
            var message = $"Bad character input: '{character}'.";
            ReportError(location, message);
        }

        public void ReportUnterminatedString(TextLocation location)
        {
            var message = $"Unterminated string literal.";
            ReportError(location, message);
        }

        internal void ReportUnrecognizedEscape(TextLocation location, string text)
        {
            var message = $"Unrecognized escape sequence '\\{text}'.";
            ReportError(location, message);
        }

        internal void ReportUnterminatedMultiLineComment(TextLocation location)
        {
            var message = $"Unterminated multi-line comment.";
            ReportError(location, message);
        }

        public void ReportUnexpectedToken(TextLocation location, SyntaxKind actualKind, SyntaxKind expectedKind)
        {
            var message = $"Unexpected token <{actualKind}>, expected <{expectedKind}>.";
            ReportError(location, message);
        }

        public void ReportUndefinedUnaryOperator(TextLocation location, string operatorText, TypeSymbol operandType)
        {
            var message = $"Unary operator '{operatorText}' is not defined for type '{operandType}'.";
            ReportError(location, message);
        }

        public void ReportUndefinedBinaryOperator(TextLocation location, string operatorText, TypeSymbol leftType, TypeSymbol rightType)
        {
            var message = $"Binary operator '{operatorText}' is not defined for types '{leftType}' and '{rightType}'.";
            ReportError(location, message);
        }

        public void ReportParameterAlreadyDeclared(TextLocation location, string parameterName)
        {
            var message = $"A parameter with the name '{parameterName}' already exists.";
            ReportError(location, message);
        }

        public void ReportUndefinedVariable(TextLocation location, string name)
        {
            var message = $"Variable '{name}' doesn't exist.";
            ReportError(location, message);
        }

        public void ReportNotAVariable(TextLocation location, string name)
        {
            var message = $"'{name}' is not a variable.";
            ReportError(location, message);
        }

        public void ReportUndefinedType(TextLocation location, string name)
        {
            var message = $"Type '{name}' doesn't exist.";
            ReportError(location, message);
        }

        public void ReportCannotConvert(TextLocation location, TypeSymbol fromType, TypeSymbol toType)
        {
            var message = $"Cannot convert type '{fromType}' to '{toType}'.";
            ReportError(location, message);
        }

        public void ReportCannotConvertImplicitly(TextLocation location, TypeSymbol fromType, TypeSymbol toType)
        {
            var message = $"Cannot convert type '{fromType}' to '{toType}'. An explicit conversion exists (are you missing a cast?)";
            ReportError(location, message);
        }

        public void ReportByteConstantOutOfRange(TextLocation location, int value)
        {
            var message = $"Constant value '{value}' is out of range for 'byte' (0-255). Use an explicit cast.";
            ReportError(location, message);
        }

        public void ReportSymbolAlreadyDeclared(TextLocation location, string name)
        {
            var message = $"'{name}' is already declared.";
            ReportError(location, message);
        }

        public void ReportCannotAssign(TextLocation location, string name)
        {
            var message = $"Variable '{name}' is read-only and cannot be assigned to.";
            ReportError(location, message);
        }

        public void ReportIndexRequiresArray(TextLocation location, TypeSymbol type)
        {
            var message = $"Cannot index a value of type '{type}'. Indexing requires an array type.";
            ReportError(location, message);
        }

        public void ReportUnknownMember(TextLocation location, string name, TypeSymbol type)
        {
            var message = $"Type '{type}' doesn't have a member named '{name}' (only array/string 'Length' is supported).";
            ReportError(location, message);
        }

        public void ReportStringIndexNotAssignable(TextLocation location)
        {
            var message = "A string index is read-only and cannot be assigned to.";
            ReportError(location, message);
        }

        public void ReportEnumMemberNotDefined(TextLocation location, string enumName, string memberName)
        {
            var message = $"Enum '{enumName}' doesn't have a member named '{memberName}'.";
            ReportError(location, message);
        }

        public void ReportEnumMemberValueMustBeInt(TextLocation location, string memberName)
        {
            var message = $"The value of enum member '{memberName}' must be an int constant.";
            ReportError(location, message);
        }

        public void ReportUndefinedFunction(TextLocation location, string name)
        {
            var message = $"Function '{name}' doesn't exist.";
            ReportError(location, message);
        }

        public void ReportNotAFunction(TextLocation location, string name)
        {
            var message = $"'{name}' is not a function.";
            ReportError(location, message);
        }

        public void ReportWrongArgumentCount(TextLocation location, string name, int expectedCount, int actualCount)
        {
            var message = $"Function '{name}' requires {expectedCount} arguments but was given {actualCount}.";
            ReportError(location, message);
        }

        public void ReportNoMatchingOverload(TextLocation location, string name)
        {
            var message = $"Function '{name}' has no overload that matches the argument types.";
            ReportError(location, message);
        }

        public void ReportAmbiguousInvocation(TextLocation location, string name)
        {
            var message = $"The call to '{name}' is ambiguous between multiple overloads.";
            ReportError(location, message);
        }

        public void ReportExpressionMustHaveValue(TextLocation location)
        {
            var message = "Expression must have a value.";
            ReportError(location, message);
        }

        internal void ReportInvalidBreakOrContinue(TextLocation location, string text)
        {
            var message = $"The keyword '{text}' can only be used inside of loops.";
            ReportError(location, message);
        }

        public void ReportAllPathsMustReturn(TextLocation location)
        {
            var message = $"Not all code paths return a value.";
            ReportError(location, message);
        }

        public void ReportExternFunctionWithoutImport(TextLocation location)
        {
            var message = "An extern function declaration must be preceded by an 'import' clause.";
            ReportError(location, message);
        }

        public void ReportExternFunctionCannotHaveBody(TextLocation location)
        {
            var message = "An extern function declaration cannot have a body.";
            ReportError(location, message);
        }

        public void ReportInvalidReturnExpression(TextLocation location, string functionName)
        {
            var message = $"Since the function '{functionName}' does not return a value the 'return' keyword cannot be followed by an expression.";
            ReportError(location, message);
        }

        public void ReportInvalidReturnWithValueInGlobalStatements(TextLocation location)
        {
            var message = "The 'return' keyword cannot be followed by an expression in global statements.";
            ReportError(location, message);
        }

        public void ReportMissingReturnExpression(TextLocation location, TypeSymbol returnType)
        {
            var message = $"An expression of type '{returnType}' is expected.";
            ReportError(location, message);
        }

        public void ReportInvalidExpressionStatement(TextLocation location)
        {
            var message = $"Only assignment and call expressions can be used as a statement.";
            ReportError(location, message);
        }

        public void ReportOnlyOneFileCanHaveGlobalStatements(TextLocation location)
        {
            var message = $"At most one file can have global statements.";
            ReportError(location, message);
        }

        public void ReportMainMustHaveCorrectSignature(TextLocation location)
        {
            var message = $"main must take no parameters or a single string[] parameter, and must return either void or int (or nothing, which defaults to 0).";
            ReportError(location, message);
        }

        public void ReportEntryClassNotFound(TextLocation location, string className)
        {
            var message = $"入口函数指定的类 '{className}' 不存在。";
            ReportError(location, message);
        }

        public void ReportEntryClassAmbiguous(TextLocation location, string className)
        {
            var message = $"入口函数指定的类 '{className}' 存在多个匹配（不同命名空间），请使用命名空间全名（如 Namespace.ClassName）限定。";
            ReportError(location, message);
        }

        public void ReportEntryMethodNotFound(TextLocation location, string className, string methodName)
        {
            var message = $"类 '{className}' 中不存在静态入口方法 '{methodName}'（入口方法必须为 static）。";
            ReportError(location, message);
        }

        public void ReportAmbiguousEntryPoint(TextLocation location, string entryName)
        {
            var message = $"入口函数 '{entryName}' 存在多个匹配（顶层函数与类静态方法并存），请用 `entry = ClassName.{entryName}`（或命名空间全名）限定。";
            ReportError(location, message);
        }

        public void ReportCannotMixMainAndGlobalStatements(TextLocation location)
        {
            var message = $"Cannot declare main function when global statements are used.";
            ReportError(location, message);
        }

        public void ReportInvalidReference(string path)
        {
            var message = $"The reference is not a valid .NET assembly: '{path}'.";
            ReportError(default, message);
        }

        public void ReportRequiredTypeNotFound(string? cocoaName, string metadataName)
        {
            var message = cocoaName == null
                ? $"The required type '{metadataName}' cannot be resolved among the given references."
                : $"The required type '{cocoaName}' ('{metadataName}') cannot be resolved among the given references.";
            ReportError(default, message);
        }


        public void ReportRequiredMethodNotFound(string typeName, string methodName, string[] parameterTypeNames)
        {
            var parameterTypeNameList = string.Join(", ", parameterTypeNames);
            var message = $"The required method '{typeName}.{methodName}({parameterTypeNameList})' cannot be resolved among the given references.";
            ReportError(default, message);
        }

        public void ReportUnreachableCode(TextLocation location)
        {
            var message = $"Unreachable code detected.";
            ReportWarning(location, message);
        }

        /// <summary>using 命名空间在程序/引用/.cod 库中都未解析时发警告（6e-M15；提示 Cocoa 不绑定 .NET BCL）。</summary>
        internal void ReportUnresolvedUsing(TextLocation location, string name)
        {
            var message = $"Using namespace '{name}' could not be resolved in the program, references, or .cod libraries. (Cocoa 不绑定 .NET BCL：System.* 等需自带 System.co / 显式引用)";
            ReportWarning(location, message);
        }

        public void ReportUnreachableCode(SyntaxNode node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.BlockStatement:
                {
                    var firstStatement = ((BlockStatementSyntax)node).Statements.FirstOrDefault();

                    // Report just for non empty blocks.
                    if (firstStatement != null)
                    {
                        ReportUnreachableCode(firstStatement);
                    }

                    return;
                }
                case SyntaxKind.VariableDeclaration:
                {
                    var variableDeclaration = (VariableDeclarationSyntax)node;
                    ReportUnreachableCode(variableDeclaration.Keyword?.Location ?? variableDeclaration.Location);
                    return;
                }
                case SyntaxKind.IfStatement:
                {
                    ReportUnreachableCode(((IfStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.WhileStatement:
                {
                    ReportUnreachableCode(((WhileStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.DoWhileStatement:
                {
                    ReportUnreachableCode(((DoWhileStatementSyntax)node).DoKeyword.Location);
                    return;
                }
                case SyntaxKind.ForStatement:
                {
                    ReportUnreachableCode(((ForStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.ForeachStatement:
                {
                    ReportUnreachableCode(((ForeachStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.SwitchStatement:
                {
                    ReportUnreachableCode(((SwitchStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.BreakStatement:
                {
                    ReportUnreachableCode(((BreakStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.ContinueStatement:
                {
                    ReportUnreachableCode(((ContinueStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.ReturnStatement:
                {
                    ReportUnreachableCode(((ReturnStatementSyntax)node).Keyword.Location);
                    return;
                }
                case SyntaxKind.ExpressionStatement:
                {
                    var expression = ((ExpressionStatementSyntax)node).Expression;
                    ReportUnreachableCode(expression);
                    return;
                }
                case SyntaxKind.CallExpression:
                {
                    ReportUnreachableCode(((CallExpressionSyntax)node).Identifier.Location);
                    return;
                }
                default:
                {
                    throw new Exception($"Unexpected syntax {node.Kind}");
                }
            }
        }

        public void ReportCannotAccessPrivateMember(TextLocation location, string memberName)
        {
            ReportCannotAccessMember(location, memberName, Visibility.Private);
        }

        /// <summary>可见性不足访问诊断（private/protected/internal 统一入口）。</summary>
        public void ReportCannotAccessMember(TextLocation location, string memberName, Visibility visibility)
        {
            var visibilityName = visibility switch
            {
                Visibility.Protected => "protected",
                Visibility.Internal => "internal",
                _ => "private",
            };
            var message = $"成员 '{memberName}' 是 {visibilityName} 的，不能在当前上下文中访问。";
            ReportError(location, message);
        }

        /// <summary>CS0273 等价：访问器可见性修饰符必须严格比属性更受限（相等亦报错，严格对齐 C#）。</summary>
        public void ReportAccessorVisibilityNotMoreRestrictive(TextLocation location, string propertyName)
        {
            var message = $"访问器 '{propertyName}' 的可见性修饰符必须比属性更受限。";
            ReportError(location, message);
        }

        /// <summary>属性 get/set 两个访问器不能同时带可见性修饰符（C# 规则）。</summary>
        public void ReportAccessorModifierOnBothAccessors(TextLocation location, string propertyName)
        {
            var message = $"属性 '{propertyName}' 的 get/set 两个访问器不能同时带可见性修饰符。";
            ReportError(location, message);
        }

        public void ReportCircularInheritance(TextLocation location, string baseName)
        {
            var message = $"循环继承：'{baseName}' 形成继承环。";
            ReportError(location, message);
        }

        public void ReportCannotInheritSealed(TextLocation location, string baseName)
        {
            var message = $"不能继承 sealed 类 '{baseName}'。";
            ReportError(location, message);
        }
    }
}
