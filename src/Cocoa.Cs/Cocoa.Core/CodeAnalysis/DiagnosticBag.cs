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

        public void ReportWarning(TextLocation location, string message)
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

        public void ReportUnsupported128BitType(TextLocation location, string name)
        {
            var message = $"Type '{name}' (128-bit) is not supported yet.";
            ReportError(location, message);
        }

        /// <summary>6e-M20 G0：泛型语法已落地、绑定未接管（G1/G2 实现）——先行明确诊断，防错绑。</summary>
        public void ReportGenericBindingNotYetSupported(TextLocation location, string name)
        {
            var message = $"Generic type '{name}' is not supported yet (6e-M20 in progress).";
            ReportError(location, message);
        }

        /// <summary>6e-M20 G0：显式类型实参调用/new 绑定未接管（G2 实现单态化）。</summary>
        public void ReportGenericTypeArgumentsNotYetSupported(TextLocation location, string name)
        {
            var message = $"Explicit type arguments on '{name}' are not supported yet (6e-M20 in progress).";
            ReportError(location, message);
        }

        /// <summary>6e-M20：泛型定义须带类型实参才能作具体类型使用（`var x: List` → `List<int>`）。</summary>
        public void ReportGenericDefinitionRequiresTypeArguments(TextLocation location, string name)
        {
            var message = $"Using the generic type '{name}' requires type arguments (e.g. '{name}<int>').";
            ReportError(location, message);
        }

        /// <summary>6e-M20：`facade` 标记用于非基元载体名。</summary>
        public void ReportInvalidFacadeMarker(TextLocation location, string fullName)
        {
            var message = $"'{fullName}' is not a known primitive facade carrier name; the 'facade' modifier is only valid on System primitive carrier classes.";
            ReportError(location, message);
        }

        /// <summary>6e-M20：与基元成员面载体同名但缺 `facade` 标记——按普通类处理（警告引导显式化）。</summary>
        public void ReportFacadeMarkerRecommended(TextLocation location, string fullName, string primitiveName)
        {
            var message = $"'{fullName}' matches the member-face carrier of primitive '{primitiveName}'. Add the 'facade' modifier to adopt it, or rename to avoid confusion.";
            ReportWarning(location, message);
        }

        public void ReportCannotConvert(TextLocation location, TypeSymbol fromType, TypeSymbol toType)
        {
            var message = $"Cannot convert type '{fromType}' to '{toType}'.";
            ReportError(location, message);
        }

        /// <summary>6e-M19 M5-a：var x = null 无类型信息可推断（对齐 C# CS8374）。</summary>
        public void ReportCannotInferVarFromNull(TextLocation location)
        {
            var message = "Cannot infer the type of 'null'. Use an explicit type declaration instead.";
            ReportError(location, message);
        }

        /// <summary>6e-M19 M5-b：is/as 目标须为非接口类（接口分派 native 未实现、数组无类型对象）。</summary>
        public void ReportIsAsUnsupportedTarget(TextLocation location, string targetName)
        {
            var message = $"'{targetName}' is not a valid target for 'is'/'as'. Only non-interface class types are supported.";
            ReportError(location, message);
        }

        /// <summary>6e-M19 M5-b：is/as 接收者须为类/string/null 字面量（any/数组/值类型无运行时类型信息）。</summary>
        public void ReportIsAsUnsupportedReceiver(TextLocation location, TypeSymbol receiverType)
        {
            var message = $"Operator 'is'/'as' requires a reference receiver (class or string), but got '{receiverType}'.";
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

        public void ReportConstantOutOfRange(TextLocation location, long value, string typeName)
        {
            var message = $"Constant value '{value}' is out of range for '{typeName}'. Use an explicit cast.";
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

        public void ReportExternFunctionCannotHaveBody(TextLocation location)
        {
            var message = "An extern function declaration cannot have a body.";
            ReportError(location, message);
        }

        /// <summary>顶层位置式 extern（6e-M17 Step 4 废弃）：extern 必须声明在类的 import 块内。</summary>
        public void ReportExternFunctionTopLevel(TextLocation location)
        {
            var message = "Top-level extern declarations are deprecated: declare extern functions inside a class import block (e.g. `class Kernel32 { import kernel32.dll { static extern ... } }`).";
            ReportError(location, message);
        }

        /// <summary>类内但 import 块外的 extern 声明（6e-M17 Step 4）：extern 必须声明在类的 import 块内。</summary>
        public void ReportExternFunctionMustBeInImportBlock(TextLocation location)
        {
            var message = "An extern function must be declared inside a class import block (e.g. `import kernel32.dll { static extern ... }`).";
            ReportError(location, message);
        }

        /// <summary>extern 函数必须 static（对齐 C# `static extern`）。</summary>
        public void ReportExternFunctionMustBeStatic(TextLocation location)
        {
            var message = "An extern function must be declared static.";
            ReportError(location, message);
        }

        /// <summary>import 块内只允许 extern 函数声明。</summary>
        public void ReportImportBlockOnlyExternFunctions(TextLocation location)
        {
            var message = "An import block may only contain extern function declarations (e.g. `static stdcall function GetTickCount(): int`).";
            ReportError(location, message);
        }

        public void ReportSyscallFunctionUnknown(TextLocation location, string name)
        {
            var message = $"Syscall function '{name}' does not match any built-in primitive.";
            ReportError(location, message);
        }

        public void ReportSyscallFunctionCannotHaveBody(TextLocation location)
        {
            var message = "A syscall function declaration cannot have a body.";
            ReportError(location, message);
        }

        public void ReportSyscallFunctionTopLevel(TextLocation location)
        {
            var message = "A syscall function must be declared inside a class (e.g. `class Runtime { syscall function ... }`).";
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
            var message = $"Using namespace '{name}' could not be resolved in the program, references, or .cod libraries. (Cocoa 不绑定 .NET BCL：System.* 等需自带 System.Core 标准库 / 显式引用)";
            ReportWarning(location, message);
        }

        /// <summary>`using static` 目标必须是类（6e-M18，C# 同构：导入类静态成员）。</summary>
        internal void ReportUsingStaticTargetNotClass(TextLocation location, string name)
        {
            var message = $"using static 的目标 '{name}' 必须是类（导入其静态成员；命名空间用 `using {name};` + 限定访问）。";
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
                case SyntaxKind.MemberCallExpression:
                {
                    ReportUnreachableCode(((MemberCallExpressionSyntax)node).IdentifierToken.Location);
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

        /// <summary>override 签名不匹配（6e-M19 M2-c，CS0115/CS1715 对齐）：基类有同名 virtual/abstract 但签名不同。</summary>
        public void ReportOverrideSignatureMismatch(TextLocation location, string name, TypeSymbol baseReturnType, TypeSymbol overrideReturnType)
        {
            var message = $"方法 '{name}' 标记 override，但与基类同名方法的签名不匹配：返回类型应为 '{baseReturnType}'（实为 '{overrideReturnType}'），参数个数与类型也须逐一相同。";
            ReportError(location, message);
        }

        public void ReportCannotInheritSealed(TextLocation location, string baseName)
        {
            var message = $"不能继承 sealed 类 '{baseName}'。";
            ReportError(location, message);
        }
    }
}
