using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis.Bound;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>
    /// .coa 函数体段读端（自 CoaFormatReader 拆出：body/statement/expression 递归读取）。
    /// </summary>
    internal sealed partial class CoaFormatReader
    {
        // ---------------------------------------------------------------- read: symbols

        private void ReadBodies(ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder bodies)
        {
            while (_tokens.TryExpect(out var kind) && kind == "body")
            {
                var fnKey = _tokens.ExpectString();
                if (!_context.FunctionsByKey.TryGetValue(fnKey, out var function))
                {
                    throw new InvalidDataException($"Unknown function '{fnKey}' in bodies");
                }

                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                var body = (BoundBlockStatement)ReadStatement(labels);

                if (function.IsExtern)
                {
                    body = new BoundBlockStatement(NoSyntax, ImmutableArray<BoundStatement>.Empty);
                }

                bodies[function] = body;
                _tokens.End();
            }

            _tokens.End();
        }

        // ---------------------------------------------------------------- read: type parameters

        /// <summary>
        /// tpar/ftp 子节点读取（6e-G7 S1）：构造符号 + 应用标志 + 登记开放键（类级限定键 !属主.名；
        /// 方法级裸键 !名）+ 暂存约束数。返回 (参数, 约束数)，约束由第二趟解析。
        /// </summary>
        private (TypeParameterSymbol Parameter, int ConstraintCount) ReadTypeParameter(string? ownerFullName)
        {
            _tokens.Expect("tpar");
            var parameterName = CoaText.Unescape(_tokens.ExpectString());
            var ordinal = _tokens.ExpectInt();
            var flagsText = _tokens.ExpectString();
            var varianceText = "-";
            if (_tokens.PeekRaw().StartsWith("v:", StringComparison.Ordinal))
            {
                varianceText = _tokens.ExpectString();
            }

            var constraintCount = _tokens.ReadCountField("c:");

            var parameter = new TypeParameterSymbol(parameterName, ordinal, owningClass: null);
            ApplyTypeParameterFlags(parameter, flagsText);
            ApplyVarianceFlag(parameter, varianceText);

            var openKey = ownerFullName == null
                ? "!" + parameterName
                : "!" + ownerFullName + "." + parameterName;
            _context.OpenTypeParametersByKey[openKey] = parameter;

            return (parameter, constraintCount);
        }

        /// <summary>约束第二趟：兄弟参数已全部注册后解析显式约束类型。</summary>
        private void ResolveDeferredConstraints(TypeParameterSymbol parameter, int constraintCount)
        {
            if (constraintCount == 0)
            {
                _tokens.End();
                return;
            }

            var constraints = ImmutableArray.CreateBuilder<TypeSymbol>(constraintCount);
            for (var c = 0; c < constraintCount; c++)
            {
                constraints.Add(_resolver.ResolveTypeRef(_tokens.ExpectString()));
            }

            parameter.ConstraintTypes = constraints.ToImmutable();
            _tokens.End();
        }

        /// <summary>约束标志解析（gcls.tpar 与 fn.tps 共用，6e-G7 S1）。</summary>
        private static void ApplyTypeParameterFlags(TypeParameterSymbol parameter, string flagsText)
        {
            if (flagsText == "-")
            {
                return;
            }

            foreach (var flag in flagsText.Split('+'))
            {
                switch (flag)
                {
                    case "new":
                        parameter.HasNewConstraint = true;
                        break;
                    case "class":
                        parameter.HasReferenceTypeConstraint = true;
                        break;
                    case "struct":
                        parameter.HasValueTypeConstraint = true;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown type parameter constraint flag '{flag}'");
                }
            }
        }

        /// <summary>型变注解位解析（6e-M22 委托真实类型化）：`v:in` / `v:out` / `v:-`；未知值报错。</summary>
        private static void ApplyVarianceFlag(TypeParameterSymbol parameter, string varianceText)
        {
            switch (varianceText)
            {
                case "v:-":
                case "-":
                    parameter.Variance = VarianceKind.Invariant;
                    break;
                case "v:in":
                case "in":
                    parameter.Variance = VarianceKind.In;
                    break;
                case "v:out":
                case "out":
                    parameter.Variance = VarianceKind.Out;
                    break;
                default:
                    throw new InvalidDataException($"Unknown variance flag '{varianceText}'");
            }
        }

        // ---------------------------------------------------------------- read: interface signatures

        /// <summary>解析接口方法完整签名 `Name[params]:Return` → FunctionSymbol（接口方法无 fn 条目承载时的成员重建）。</summary>
        private FunctionSymbol? ParseInterfaceMethodSignature(string signature, NamedTypeSymbol classType)
        {
            try
            {
                var openBracket = signature.IndexOf('[');
                string retText;
                string paramsText;
                string name;
                if (openBracket >= 0)
                {
                    var closeBracket = signature.LastIndexOf(']');
                    var sep = signature.IndexOf(':', closeBracket + 1);
                    if (sep < 0)
                    {
                        return null;
                    }

                    name = signature.Substring(0, openBracket);
                    paramsText = signature.Substring(openBracket + 1, closeBracket - openBracket - 1);
                    retText = signature.Substring(sep + 1);
                }
                else
                {
                    var sep = signature.IndexOf(':');
                    if (sep < 0)
                    {
                        return null;
                    }

                    name = signature.Substring(0, sep);
                    retText = signature.Substring(sep + 1);
                    paramsText = "";
                }

                var returnType = _resolver.ResolveTypeRef(retText);
                var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
                if (paramsText.Length > 0)
                {
                    var ordinal = 0;
                    foreach (var raw in SplitInterfaceMethodParams(paramsText))
                    {
                        var isOut = raw.StartsWith("out:", StringComparison.Ordinal);
                        var isRef = raw.StartsWith("ref:", StringComparison.Ordinal);
                        var isNone = raw.StartsWith("-", StringComparison.Ordinal);
                        var typeText = isOut ? raw.Substring(4) : isRef ? raw.Substring(4) : isNone ? raw.Substring(1) : raw;
                        parameters.Add(new ParameterSymbol("p" + ordinal, _resolver.ResolveTypeRef(typeText), ordinal, isOut, isRef, isThis: false));
                        ordinal++;
                    }
                }

                return new FunctionSymbol(name, parameters.ToImmutable(), returnType, containingClass: classType);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>签名参数列表按顶层逗号切分（忽略方括号/开放形参引用内的逗号）。</summary>
        private static IEnumerable<string> SplitInterfaceMethodParams(string paramsText)
        {
            var depth = 0;
            var current = new StringBuilder();
            foreach (var ch in paramsText)
            {
                switch (ch)
                {
                    case '[':
                    case ']':
                    case '!':
                        depth++;
                        current.Append(ch);
                        break;
                    case ',' when depth == 0:
                        yield return current.ToString();
                        current.Length = 0;
                        break;
                    default:
                        current.Append(ch);
                        break;
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        // ---------------------------------------------------------------- read: bodies

        private BoundStatement ReadStatement(Dictionary<string, BoundLabel> labels)
        {
            var kind = _tokens.ExpectKind();
            var statement = ReadStatementFromToken(kind, labels);
            _tokens.End();
            return statement;
        }

        private BoundStatement ReadStatementFromToken(string kind, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "block":
                    {
                        var count = _tokens.ExpectInt();
                        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                        for (var i = 0; i < count; i++)
                        {
                            statements.Add(ReadStatement(labels));
                        }

                        return new BoundBlockStatement(NoSyntax, statements.ToImmutable());
                    }
                case "nop":
                    return new BoundNopStatement(NoSyntax);
                case "vardecl":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var initializer = ReadExpression(labels);
                        return new BoundVariableDeclaration(NoSyntax, variable, initializer);
                    }
                case "if":
                    {
                        var condition = ReadExpression(labels);
                        var then = ReadStatement(labels);
                        var elseStatement = ReadNullableStatement(labels);
                        return new BoundIfStatement(NoSyntax, condition, then, elseStatement);
                    }
                case "while":
                    {
                        var condition = ReadExpression(labels);
                        var body = ReadStatement(labels);
                        var breakLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var continueLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        return new BoundWhileStatement(NoSyntax, condition, body, breakLabel, continueLabel);
                    }
                case "dowhile":
                    {
                        var body = ReadStatement(labels);
                        var condition = ReadExpression(labels);
                        var breakLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var continueLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        return new BoundDoWhileStatement(NoSyntax, body, condition, breakLabel, continueLabel);
                    }
                case "for":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var lowerBound = ReadExpression(labels);
                        var upperBound = ReadExpression(labels);
                        var step = ReadNullableExpression(labels);
                        var body = ReadStatement(labels);
                        var breakLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var continueLabel = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        return new BoundForRangeStatement(NoSyntax, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
                    }
                case "label":
                    return new BoundLabelStatement(NoSyntax, GetLabel(labels, CoaText.Unescape(_tokens.ExpectString())));
                case "goto":
                    return new BoundGotoStatement(NoSyntax, GetLabel(labels, CoaText.Unescape(_tokens.ExpectString())));
                case "cgoto":
                    {
                        var label = GetLabel(labels, CoaText.Unescape(_tokens.ExpectString()));
                        var condition = ReadExpression(labels);
                        var jumpIfTrue = _tokens.ParseBoolWord(_tokens.ExpectString());
                        return new BoundConditionalGotoStatement(NoSyntax, label, condition, jumpIfTrue);
                    }
                case "return":
                    {
                        var expression = ReadNullableExpression(labels);
                        return new BoundReturnStatement(NoSyntax, expression);
                    }
                case "exprstmt":
                    {
                        var expression = ReadExpression(labels);
                        return new BoundExpressionStatement(NoSyntax, expression);
                    }
                default:
                    throw new InvalidDataException($"Unknown statement kind '{kind}'");
            }
        }

        private BoundStatement? ReadNullableStatement(Dictionary<string, BoundLabel> labels)
        {
            if (_tokens.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var statement = ReadStatementFromToken(token, labels);
            _tokens.End();
            return statement;
        }

        private BoundExpression? ReadNullableExpression(Dictionary<string, BoundLabel> labels)
        {
            if (_tokens.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var expression = ReadExpressionFromToken(token, labels);
            _tokens.End();
            return expression;
        }

        private BoundExpression ReadExpression(Dictionary<string, BoundLabel> labels)
        {
            var token = _tokens.ExpectKind();
            var expression = ReadExpressionFromToken(token, labels);
            _tokens.End();
            return expression;
        }

        private BoundExpression ReadExpressionFromToken(string kind, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "lit":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var encoded = _tokens.ExpectString();
                        var value = CoaText.DecodeValue(encoded);
                        return new BoundLiteralExpression(NoSyntax, value, type);
                    }
                case "var":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        return new BoundVariableExpression(NoSyntax, variable);
                    }
                case "assign":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundAssignmentExpression(NoSyntax, variable, expression);
                    }
                case "cassign":
                    {
                        var variable = _resolver.ResolveVariable(_tokens.ExpectString());
                        var op = ReadBinaryOperator();
                        var expression = ReadExpression(labels);
                        return new BoundCompoundAssignmentExpression(NoSyntax, variable, op, expression);
                    }
                case "unary":
                    {
                        var op = ReadUnaryOperator();
                        var operand = ReadExpression(labels);
                        return new BoundUnaryExpression(NoSyntax, op, operand);
                    }
                case "binary":
                    {
                        var op = ReadBinaryOperator();
                        var left = ReadExpression(labels);
                        var right = ReadExpression(labels);
                        return new BoundBinaryExpression(NoSyntax, left, op, right);
                    }
                case "cond":
                    {
                        var condition = ReadExpression(labels);
                        var whenTrue = ReadExpression(labels);
                        var whenFalse = ReadExpression(labels);
                        return new BoundConditionalExpression(NoSyntax, condition, whenTrue, whenFalse);
                    }
                case "call":
                    {
                        var function = _resolver.ResolveFunction(_tokens.ExpectString());
                        var count = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundCallExpression(NoSyntax, function, arguments.ToImmutable());
                    }
                case "byrefarg":
                    {
                        // 6e-M23 R8：out/ref 实参包装（内层为可赋值 lvalue）。
                        var modifier = _tokens.ExpectString();
                        var expression = ReadExpression(labels);
                        return new BoundByRefArgument(NoSyntax, expression, isRef: modifier is "ref" or "ref:");
                    }
                case "conv":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundConversionExpression(NoSyntax, type, expression);
                    }
                case "istype":
                    {
                        var targetType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundIsExpression(NoSyntax, expression, targetType);
                    }
                case "astype":
                    {
                        var targetType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var expression = ReadExpression(labels);
                        return new BoundAsExpression(NoSyntax, expression, targetType);
                    }
                case "arrnew":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var length = ReadExpression(labels);
                        var count = _tokens.ExpectInt();
                        var initializers = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            initializers.Add(ReadExpression(labels));
                        }

                        return new BoundArrayCreationExpression(NoSyntax, type, length, initializers.ToImmutable());
                    }
                case "objnew":
                    {
                        var type = (NamedTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        var argCount = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < argCount; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundObjectCreationExpression(NoSyntax, type, arguments.ToImmutable());
                    }
                case "elem":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var target = ReadExpression(labels);
                        var index = ReadExpression(labels);
                        return new BoundElementAccessExpression(NoSyntax, type, target, index);
                    }
                case "elemassign":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var target = (BoundElementAccessExpression)ReadExpression(labels);
                        var expression = ReadExpression(labels);
                        return new BoundElementAssignmentExpression(NoSyntax, type, target, expression);
                    }
                case "memberacc":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var identifier = CoaText.Unescape(_tokens.ExpectString());

                        // 6e-G7 S2：owner 字段可选携带——回填 FieldSymbol（实例化类型的 Fields 经物化钩子可达）
                        FieldSymbol? field = null;
                        var hasOwner = _tokens.PeekRaw().StartsWith("owner:", StringComparison.Ordinal);
                        if (hasOwner)
                        {
                            var ownerFullName = _tokens.ReadLabeledField("owner:");
                            if (_resolver.ResolveNamedType(ownerFullName) is NamedTypeSymbol ownerClass)
                            {
                                field = ownerClass.Fields.FirstOrDefault(f => f.Name == identifier);
                            }
                        }

                        var target = ReadExpression(labels);
                        return new BoundMemberAccessExpression(NoSyntax, type, target, identifier, field);
                    }
                case "memberassign":
                    {
                        // 6e-G7 S2：字段赋值读回——Field 按 target 形态 + 名字解析
                        var target = ReadExpression(labels);
                        var fieldName = CoaText.Unescape(_tokens.ReadLabeledField("name:"));
                        _ = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        _ = _tokens.ParseBoolWord(_tokens.ExpectString());
                        var value = ReadExpression(labels);

                        FieldSymbol? field = target switch
                        {
                            // 6e-G7：隐式 this 赋值（`_value = v`）——字段在 this 的类上
                            BoundThisExpression thisExpression => ((NamedTypeSymbol)thisExpression.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            BoundMemberAccessExpression access => access.Field,
                            BoundStaticTypeExpression staticType => ((NamedTypeSymbol)staticType.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            // 6e-M25：局部/参数（如 `win.Field = v`）——字段在变量的静态类上
                            BoundVariableExpression variable when variable.Type is NamedTypeSymbol variableType => variableType.Fields.FirstOrDefault(f => f.Name == fieldName),
                            _ => null,
                        };

                        if (field == null)
                        {
                            throw new InvalidDataException($"Unknown field '{fieldName}' in memberassign");
                        }

                        return new BoundMemberAssignmentExpression(NoSyntax, target, field, value);
                    }
                case "membercall":
                    {
                        var type = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var identifier = CoaText.Unescape(_tokens.ExpectString());
                        var methodToken = _tokens.ExpectString();
                        var method = methodToken == "-" ? null : _resolver.ResolveFunction(methodToken);
                        var count = _tokens.ExpectInt();
                        var target = ReadExpression(labels);
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundMemberCallExpression(NoSyntax, target, identifier, arguments.ToImmutable(), type, method);
                    }
                case "statictype":
                    {
                        var type = (NamedTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        return new BoundStaticTypeExpression(NoSyntax, type);
                    }
                case "ctorchain":
                    {
                        var kindText = _tokens.ExpectString();
                        var constructorKey = _tokens.ExpectString();
                        FunctionSymbol? constructor = constructorKey == "-" ? null : _resolver.ResolveFunction(constructorKey);
                        var count = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        var initKind = kindText == "this" ? ConstructorInitializerKind.This : ConstructorInitializerKind.Base;
                        return new BoundConstructorChainExpression(NoSyntax, initKind, constructor, arguments.ToImmutable());
                    }
                case "this":
                    {
                        var type = (NamedTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        return new BoundThisExpression(NoSyntax, type);
                    }
                case "fnval":
                    {
                        // Step D-a：函数值/捕获闭包入口——类型 + FnKey + 可选接收者 + 可选闭包环境类
                        var fnType = (FunctionTypeSymbol)_resolver.ResolveTypeRef(_tokens.ExpectString());
                        var function = _resolver.ResolveFunction(_tokens.ExpectString());
                        var receiver = ReadNullableExpression(labels);
                        var envToken = _tokens.ExpectString();
                        var environmentClass = envToken == "-"
                            ? null
                            : _resolver.ResolveNamedType(envToken) as NamedTypeSymbol;

                        return new BoundFunctionValueExpression(NoSyntax, function, receiver, body: null, fnType, environmentClass);
                    }
                case "invoc":
                    {
                        var ivType = _resolver.ResolveTypeRef(_tokens.ExpectString());
                        var count = _tokens.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        var callee = ReadExpression(labels);
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(labels));
                        }

                        return new BoundInvocationExpression(NoSyntax, callee, arguments.ToImmutable(), ivType);
                    }
                default:
                    throw new InvalidDataException($"Unknown expression kind '{kind}'");
            }
        }

    }
}
