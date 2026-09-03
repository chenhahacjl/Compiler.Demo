using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization
{
    internal static partial class CoaSerializer
    {
        private static BoundStatement ReadStatement(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            var kind = reader.ExpectKind();
            var statement = ReadStatementFromToken(reader, kind, context, labels);
            reader.End();
            return statement;
        }

        private static BoundStatement ReadStatementFromToken(Reader reader, string kind, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "block":
                    {
                        var count = reader.ExpectInt();
                        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                        for (var i = 0; i < count; i++)
                        {
                            statements.Add(ReadStatement(reader, context, labels));
                        }

                        return new BoundBlockStatement(null, statements.ToImmutable());
                    }
                case "nop":
                    return new BoundNopStatement(null);
                case "vardecl":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var initializer = ReadExpression(reader, context, labels);
                        return new BoundVariableDeclaration(null, variable, initializer);
                    }
                case "if":
                    {
                        var condition = ReadExpression(reader, context, labels);
                        var then = ReadStatement(reader, context, labels);
                        var elseStatement = ReadNullableStatement(reader, context, labels);
                        return new BoundIfStatement(null, condition, then, elseStatement);
                    }
                case "while":
                    {
                        var condition = ReadExpression(reader, context, labels);
                        var body = ReadStatement(reader, context, labels);
                        var breakLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        var continueLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        return new BoundWhileStatement(null, condition, body, breakLabel, continueLabel);
                    }
                case "dowhile":
                    {
                        var body = ReadStatement(reader, context, labels);
                        var condition = ReadExpression(reader, context, labels);
                        var breakLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        var continueLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        return new BoundDoWhileStatement(null, body, condition, breakLabel, continueLabel);
                    }
                case "for":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var lowerBound = ReadExpression(reader, context, labels);
                        var upperBound = ReadExpression(reader, context, labels);
                        var step = ReadNullableExpression(reader, context, labels);
                        var body = ReadStatement(reader, context, labels);
                        var breakLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        var continueLabel = GetLabel(labels, Unescape(reader.ExpectString()));
                        return new BoundForRangeStatement(null, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
                    }
                case "label":
                    return new BoundLabelStatement(null, GetLabel(labels, Unescape(reader.ExpectString())));
                case "goto":
                    return new BoundGotoStatement(null, GetLabel(labels, Unescape(reader.ExpectString())));
                case "cgoto":
                    {
                        var label = GetLabel(labels, Unescape(reader.ExpectString()));
                        var condition = ReadExpression(reader, context, labels);
                        var jumpIfTrue = ParseBoolWord(reader.ExpectString());
                        return new BoundConditionalGotoStatement(null, label, condition, jumpIfTrue);
                    }
                case "return":
                    {
                        var expression = ReadNullableExpression(reader, context, labels);
                        return new BoundReturnStatement(null, expression);
                    }
                case "exprstmt":
                    {
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundExpressionStatement(null, expression);
                    }
                default:
                    throw new InvalidDataException($"Unknown statement kind '{kind}'");
            }
        }

        private static BoundStatement? ReadNullableStatement(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            if (reader.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var statement = ReadStatementFromToken(reader, token, context, labels);
            reader.End();
            return statement;
        }

        private static BoundExpression? ReadNullableExpression(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            if (reader.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var expression = ReadExpressionFromToken(reader, token, context, labels);
            reader.End();
            return expression;
        }

        private static BoundExpression ReadExpression(Reader reader, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            var token = reader.ExpectKind();
            var expression = ReadExpressionFromToken(reader, token, context, labels);
            reader.End();
            return expression;
        }

        private static BoundExpression ReadExpressionFromToken(Reader reader, string kind, ReadContext context, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "lit":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var encoded = reader.ExpectString();
                        var value = DecodeValue(encoded);
                        return new BoundLiteralExpression(null, value, type);
                    }
                case "var":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        return new BoundVariableExpression(null, variable);
                    }
                case "assign":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundAssignmentExpression(null, variable, expression);
                    }
                case "cassign":
                    {
                        var variable = ResolveVariable(reader.ExpectString(), context);
                        var op = ReadBinaryOperator(reader, context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundCompoundAssignmentExpression(null, variable, op, expression);
                    }
                case "unary":
                    {
                        var op = ReadUnaryOperator(reader, context);
                        var operand = ReadExpression(reader, context, labels);
                        return new BoundUnaryExpression(null, op, operand);
                    }
                case "binary":
                    {
                        var op = ReadBinaryOperator(reader, context);
                        var left = ReadExpression(reader, context, labels);
                        var right = ReadExpression(reader, context, labels);
                        return new BoundBinaryExpression(null, left, op, right);
                    }
                case "cond":
                    {
                        var condition = ReadExpression(reader, context, labels);
                        var whenTrue = ReadExpression(reader, context, labels);
                        var whenFalse = ReadExpression(reader, context, labels);
                        return new BoundConditionalExpression(null, condition, whenTrue, whenFalse);
                    }
                case "call":
                    {
                        var function = ResolveFunction(reader.ExpectString(), context);
                        var count = reader.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundCallExpression(null, function, arguments.ToImmutable());
                    }
                case "byrefarg":
                    {
                        // 6e-M23 R8锛歰ut/ref 瀹炲弬鍖呰锛堝唴灞備负鍙祴鍊?lvalue锛?
                        var modifier = reader.ExpectString();
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundByRefArgument(null, expression, isRef: modifier == "ref");
                    }
                case "conv":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundConversionExpression(null, type, expression);
                    }
                case "arrnew":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var length = ReadExpression(reader, context, labels);
                        var count = reader.ExpectInt();
                        var initializers = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            initializers.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundArrayCreationExpression(null, type, length, initializers.ToImmutable());
                    }
                case "objnew":
                    {
                        // M0-1c：对象创建 `new Foo(args)`——构造器由类型+元数重解析
                        var type = (NamedTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        var argCount = reader.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < argCount; i++)
                        {
                            arguments.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundObjectCreationExpression(null, type, arguments.ToImmutable());
                    }
                case "elem":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var target = ReadExpression(reader, context, labels);
                        var index = ReadExpression(reader, context, labels);
                        return new BoundElementAccessExpression(null, type, target, index);
                    }
                case "elemassign":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var target = (BoundElementAccessExpression)ReadExpression(reader, context, labels);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundElementAssignmentExpression(null, type, target, expression);
                    }
                case "memberacc":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var identifier = Unescape(reader.ExpectString());

                        // 6e-G7 S2：owner 字段可选携带 → 回填 FieldSymbol（实例化类型的 Fields 经物化钩子可达）
                        FieldSymbol? field = null;
                        var hasOwner = reader.PeekRaw().StartsWith("owner:", StringComparison.Ordinal);
                        if (hasOwner)
                        {
                            var ownerFullName = ReadLabeledField(reader, "owner:");
                            if (ResolveNamedType(ownerFullName, context) is NamedTypeSymbol ownerClass)
                            {
                                field = ownerClass.Fields.FirstOrDefault(f => f.Name == identifier);
                            }
                        }

                        var target = ReadExpression(reader, context, labels);
                        return new BoundMemberAccessExpression(null, type, target, identifier, field);
                    }
                case "memberassign":
                    {
                        // 6e-G7 S2：字段赋值读回——Field 由 target 形态 + 名字解析
                        var target = ReadExpression(reader, context, labels);
                        var fieldName = Unescape(ReadLabeledField(reader, "name:"));
                        _ = ResolveTypeRef(reader.ExpectString(), context);
                        _ = ParseBoolWord(reader.ExpectString());
                        var value = ReadExpression(reader, context, labels);

                        FieldSymbol? field = target switch
                        {
                            // 6e-G7：隐式 this 赋值（`_value = v`）——字段在 this 的类上
                            BoundThisExpression thisExpression => ((NamedTypeSymbol)thisExpression.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            BoundMemberAccessExpression access => access.Field,
                            BoundStaticTypeExpression staticType => ((NamedTypeSymbol)staticType.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            _ => null,
                        };

                        if (field == null)
                        {
                            throw new InvalidDataException($"Unknown field '{fieldName}' in memberassign");
                        }

                        return new BoundMemberAssignmentExpression(null, target, field, value);
                    }
                case "membercall":
                    {
                        var type = ResolveTypeRef(reader.ExpectString(), context);
                        var identifier = Unescape(reader.ExpectString());
                        var methodToken = reader.ExpectString();
                        var method = methodToken == "-" ? null : ResolveFunction(methodToken, context);
                        var count = reader.ExpectInt();
                        var target = ReadExpression(reader, context, labels);
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(reader, context, labels));
                        }

                        return new BoundMemberCallExpression(null, target, identifier, arguments.ToImmutable(), type, method);
                    }
                case "statictype":
                    {
                        var type = (NamedTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        return new BoundStaticTypeExpression(null, type);
                    }
                case "this":
                    {
                        var type = (NamedTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        return new BoundThisExpression(null, type);
                    }
                case "istype":
                    {
                        var targetType = ResolveTypeRef(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundIsExpression(null, expression, targetType);
                    }
                case "astype":
                    {
                        var targetType = ResolveTypeRef(reader.ExpectString(), context);
                        var expression = ReadExpression(reader, context, labels);
                        return new BoundAsExpression(null, expression, targetType);
                    }
                default:
                    throw new InvalidDataException($"Unknown expression kind '{kind}'");
            }
        }

        private static BoundUnaryOperator ReadUnaryOperator(Reader reader, ReadContext context)
        {
            reader.Expect("uop");
            var unaryKind = ParseUnaryOpText(reader.ExpectString());
            var operandType = ResolveTypeRef(reader.ExpectString(), context);
            var op = BoundUnaryOperator.Bind(unaryKind, operandType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind unary operator {unaryKind} on {operandType}");
        }

        private static BoundBinaryOperator ReadBinaryOperator(Reader reader, ReadContext context)
        {
            reader.Expect("bop");
            var binaryKind = ParseBinaryOpText(reader.ExpectString());
            var leftType = ResolveTypeRef(reader.ExpectString(), context);
            var rightType = ResolveTypeRef(reader.ExpectString(), context);
            var op = BoundBinaryOperator.Bind(binaryKind, leftType, rightType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind binary operator {binaryKind} on {leftType} and {rightType}");
        }

        private static BoundLabel GetLabel(Dictionary<string, BoundLabel> labels, string name)
        {
            if (!labels.TryGetValue(name, out var label))
            {
                label = new BoundLabel(name);
                labels[name] = label;
            }

            return label;
        }

        // ---------------------------------------------------------------- read: tokenizer / reader

        private static IEnumerable<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '(' || c == ')')
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }

                    tokens.Add(c.ToString());
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }

                    continue;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        private sealed class Reader
        {
            private readonly string[] _tokens;
            private int _pos;

            public Reader(string[] tokens)
            {
                _tokens = tokens;
            }

            public string Expect(string kind)
            {
                var token = Next();
                if (token != kind)
                {
                    throw new InvalidDataException($"Expected '{kind}' but found '{token}'");
                }

                return token;
            }

            public string ExpectKind()
            {
                var token = Next();
                if (token == "(" || token == ")")
                {
                    throw new InvalidDataException($"Expected kind token but found '{token}'");
                }

                return token;
            }

            public string ExpectString()
            {
                var token = Next();
                if (token == "(" || token == ")")
                {
                    throw new InvalidDataException($"Expected atom but found '{token}'");
                }

                return token;
            }

            public int ExpectInt()
            {
                var token = ExpectString();
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    throw new InvalidDataException($"Expected integer but found '{token}'");
                }

                return value;
            }

            /// <summary>绐ユ帰褰撳墠鍘熷 token锛堜笉璺宠繃 `(`锛夆€斺€旂敤浜庡垽鏂瓙鑺傜偣鏄惁鍑虹幇銆?/summary>
            public string PeekRaw()
            {
                return _pos < _tokens.Length ? _tokens[_pos] : "";
            }

            public bool TryExpect(out string token)
            {
                // 璺宠繃鑺傜偣寮€鎷彿 `(`
                while (_pos < _tokens.Length && _tokens[_pos] == "(")
                {
                    _pos++;
                }

                if (_pos >= _tokens.Length)
                {
                    token = null!;
                    return false;
                }

                // `)` 涓嶆秷璐癸紙鐣欑粰 End()锛夛紝杩斿洖 false 缁堟褰撳墠鍒楄〃
                if (_tokens[_pos] == ")")
                {
                    token = ")";
                    return false;
                }

                token = _tokens[_pos++];
                return true;
            }

            public void End()
            {
                // 褰撳墠 token 搴斾负鑺傜偣闂嫭鍙?`)`锛堢洿鎺ユ秷璐癸紝涓嶈烦杩?`(`锛?
                if (_pos >= _tokens.Length)
                {
                    throw new InvalidDataException($"unexpected end of .coa file at pos {_pos}; context: {Context()}");
                }

                var token = _tokens[_pos++];
                if (token != ")")
                {
                    throw new InvalidDataException($"Expected ')' but found '{token}' at pos {_pos - 1}; context: {Context()}");
                }
            }

            private string Context()
            {
                var start = Math.Max(0, _pos - 12);
                var count = Math.Min(_tokens.Length - start, 24);
                return string.Join(" ", _tokens, start, count);
            }

            private string Next()
            {
                // 璺宠繃鑺傜偣寮€鎷彿 `(`锛涜繑鍥炲師瀛愭垨 `)`锛堝垪琛ㄧ粓姝級
                while (true)
                {
                    if (_pos >= _tokens.Length)
                    {
                        throw new InvalidDataException("unexpected end of .coa file");
                    }

                    var token = _tokens[_pos++];
                    if (token != "(")
                    {
                        return token;
                    }
                }
            }
        }
    }
}
