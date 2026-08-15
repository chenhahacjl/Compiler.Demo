using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit
{
    /// <summary>
    /// IL 路径发射器：绑定树 → 自研 IL 组件（IlAssembler/MetadataBuilder/ManagedPEWriter）。
    /// 发射语义与原 Mono.Cecil 实现一致（表达式/语句 → IL 指令序列）。
    /// </summary>
    internal sealed class Emitter
    {
        private readonly MetadataBuilder _metadata;
        private readonly MetadataReader _reader;
        private readonly string _moduleName;
        private readonly Dictionary<FunctionSymbol, IlMethodDef> _methods = new Dictionary<FunctionSymbol, IlMethodDef>();
        private readonly Dictionary<VariableSymbol, int> _locals = new Dictionary<VariableSymbol, int>();
        private readonly Dictionary<BoundLabel, IlInstruction> _labelTargets = new Dictionary<BoundLabel, IlInstruction>();

        private readonly IlTypeRef _objectType;
        private readonly IlTypeRef _stringType;
        private readonly IlTypeDef _typeDefinition;

        private readonly IlMethodRef _objectEqualsReference;
        private readonly IlMethodRef _consoleReadLineReference;
        private readonly IlMethodRef _consoleWriteLineReference;
        private readonly IlMethodRef _stringConcat2Reference;
        private readonly IlMethodRef _stringConcat3Reference;
        private readonly IlMethodRef _stringConcat4Reference;
        private readonly IlMethodRef _stringConcatArrayReference;
        private readonly IlMethodRef _convertToBooleanReference;
        private readonly IlMethodRef _convertToInt32Reference;
        private readonly IlMethodRef _convertToStringReference;
        private readonly IlMethodRef _randomGetSharedReference;
        private readonly IlMethodRef _randomNextReference;
        private readonly IlMethodRef _debuggableAttributeCtorReference;

        private Emitter(string moduleName, string[] references)
        {
            _moduleName = moduleName;
            _metadata = new MetadataBuilder(moduleName, moduleName);
            _reader = new MetadataReader(references);

            _objectType = RequireType("System.Object");
            _stringType = RequireType("System.String");

            _objectEqualsReference = RequireMethod("System.Object", "Equals", new[] { "System.Object", "System.Object" });
            _consoleReadLineReference = RequireMethod("System.Console", "ReadLine", System.Array.Empty<string>());
            _consoleWriteLineReference = RequireMethod("System.Console", "WriteLine", new[] { "System.Object" });
            _stringConcat2Reference = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String" });
            _stringConcat3Reference = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String", "System.String" });
            _stringConcat4Reference = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String", "System.String", "System.String" });
            _stringConcatArrayReference = RequireMethod("System.String", "Concat", new[] { "System.String[]" });
            _convertToBooleanReference = RequireMethod("System.Convert", "ToBoolean", new[] { "System.Object" });
            _convertToInt32Reference = RequireMethod("System.Convert", "ToInt32", new[] { "System.Object" });
            _convertToStringReference = RequireMethod("System.Convert", "ToString", new[] { "System.Object" });
            _randomGetSharedReference = RequireMethod("System.Random", "get_Shared", System.Array.Empty<string>());
            _randomNextReference = RequireMethod("System.Random", "Next", new[] { "System.Int32" });
            _debuggableAttributeCtorReference = RequireMethod("System.Diagnostics.DebuggableAttribute", ".ctor", new[] { "System.Boolean", "System.Boolean" });

            _typeDefinition = new IlTypeDef("Program", _objectType);
            _metadata.AddTypeDef(_typeDefinition);
        }

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath)
        {
            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            var emitter = new Emitter(moduleName, references);

            return emitter.Emit(program, outputPath);
        }

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath)
        {
            foreach (var functionWithBody in program.Functions)
            {
                EmitFunctionDeclaration(functionWithBody.Key);
            }

            var bodies = new List<ManagedPEWriter.MethodBodyBlob>();
            var methods = new List<IlMethodDef>();

            foreach (var functionWithBody in program.Functions)
            {
                if (functionWithBody.Key.IsExtern)
                {
                    continue;
                }

                var method = _methods[functionWithBody.Key];
                methods.Add(method);
                var (code, localSigToken, maxStack) = EmitFunctionBody(method, functionWithBody.Value);
                bodies.Add(new ManagedPEWriter.MethodBodyBlob(code, localSigToken, (ushort)maxStack));
            }

            _metadata.AddCustomAttribute(new IlCustomAttribute(_debuggableAttributeCtorReference, MetadataBuilder.EncodeDebuggableAttributeBlob()));

            var entryPointToken = program.MainFunction == null ? 0 : _metadata.BuildTokenMap()[_methods[program.MainFunction]];
            var pe = ManagedPEWriter.Build(_moduleName, methods, bodies, _metadata, entryPointToken);

            File.WriteAllBytes(outputPath, pe);
            WriteRuntimeConfig(outputPath);

            return ImmutableArray<Diagnostic>.Empty;
        }

        /// <summary>framework-dependent 运行所需的 runtimeconfig.json。</summary>
        private static void WriteRuntimeConfig(string outputPath)
        {
            var runtimeConfigPath = Path.ChangeExtension(outputPath, ".runtimeconfig.json");
            var json =
                "{\n" +
                "  \"runtimeOptions\": {\n" +
                "    \"tfm\": \"net9.0\",\n" +
                "    \"framework\": {\n" +
                "      \"name\": \"Microsoft.NETCore.App\",\n" +
                "      \"version\": \"9.0.0\"\n" +
                "    }\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(runtimeConfigPath, json);
        }

        private void EmitFunctionDeclaration(FunctionSymbol function)
        {
            var returnType = ToIlType(function.ReturnType);
            var parameterTypes = new List<IlType>();
            foreach (var parameter in function.Parameters)
            {
                parameterTypes.Add(ToIlType(parameter.Type));
            }

            var callingConvention = function.CallingConvention switch
            {
                CallingConvention.Cdecl => IlCallingConvention.Cdecl,
                CallingConvention.StdCall => IlCallingConvention.StdCall,
                _ => IlCallingConvention.Winapi,
            };

            var method = new IlMethodDef(function.Name, returnType, parameterTypes, null, function.IsExtern ? function.DllName : null, null, callingConvention);
            _methods.Add(function, method);
            _metadata.AddMethodDef(method);
        }

        private (byte[] Code, uint LocalSigToken, int MaxStack) EmitFunctionBody(IlMethodDef method, BoundBlockStatement body)
        {
            _locals.Clear();
            _labelTargets.Clear();

            var assembler = new IlAssembler();

            // 预收集局部变量（按声明顺序分配索引）
            var localTypes = new List<IlType>();
            CollectLocals(body, localTypes);

            // 预收集 label 占位（前向引用需要目标指令对象）
            CollectLabels(body);

            foreach (var statement in body.Statements)
            {
                EmitStatement(assembler, statement);
            }

            var code = assembler.Assemble();
            var maxStack = assembler.ComputeMaxStack(assembler.Instructions);

            // 注册 #US 字符串（Ldstr fixup 回填前）
            foreach (var value in assembler.StringFixupValues)
            {
                _metadata.GetOrAddUserString(value);
            }

            // 先注册 StandAloneSig（局部变量签名），再构建 token 映射回填
            uint localSigToken = 0;
            var sigReference = localTypes.Count > 0
                ? _metadata.AddStandAloneSig(_metadata.EncodeLocalVarSignature(localTypes))
                : null;

            var tokenMap = _metadata.BuildTokenMap();
            assembler.PatchTokens(code, tokenMap);
            assembler.PatchStrings(code, _metadata.UserStringTokens);

            if (sigReference != null)
            {
                localSigToken = tokenMap[sigReference];
            }

            return (code, localSigToken, maxStack);
        }

        private void CollectLabels(BoundStatement node)
        {
            switch (node)
            {
                case BoundBlockStatement block:
                    foreach (var statement in block.Statements)
                    {
                        CollectLabels(statement);
                    }

                    break;
                case BoundLabelStatement labelStatement:
                    _labelTargets[labelStatement.Label] = new IlInstruction(IlOpCodes.Get("Nop"), null);
                    break;
                case BoundSequencePointStatement sequencePoint:
                    CollectLabels(sequencePoint.Statement);
                    break;
            }
        }

        private void CollectLocals(BoundStatement node, List<IlType> localTypes)
        {
            switch (node)
            {
                case BoundBlockStatement block:
                    foreach (var statement in block.Statements)
                    {
                        CollectLocals(statement, localTypes);
                    }

                    break;
                case BoundVariableDeclaration variableDeclaration:
                    _locals.Add(variableDeclaration.Variable, localTypes.Count);
                    localTypes.Add(ToIlType(variableDeclaration.Variable.Type));
                    break;
                case BoundSequencePointStatement sequencePoint:
                    CollectLocals(sequencePoint.Statement, localTypes);
                    break;
            }
        }

        private static IlType ToIlType(TypeSymbol type)
        {
            if (type == TypeSymbol.Any)
            {
                return IlType.Object;
            }

            if (type == TypeSymbol.Boolean)
            {
                return IlType.Boolean;
            }

            if (type == TypeSymbol.Int32)
            {
                return IlType.Int32;
            }

            if (type == TypeSymbol.String)
            {
                return IlType.String;
            }

            if (type == TypeSymbol.Void)
            {
                return IlType.Void;
            }

            throw new System.Exception($"Unexpected type {type}");
        }

        private IlTypeRef RequireType(string fullName)
        {
            return _reader.FindType(fullName, _metadata) ?? throw new System.Exception($"Type '{fullName}' not found in references.");
        }

        private IlMethodRef RequireMethod(string typeFullName, string methodName, string[] parameterTypeNames)
        {
            var resolved = _reader.FindMethod(typeFullName, methodName, parameterTypeNames, _metadata);
            if (resolved == null)
            {
                throw new System.Exception($"Method '{typeFullName}.{methodName}' not found in references.");
            }

            var returnType = ResolveClassType(resolved.ReturnType);
            var parameterTypes = new List<IlType>(resolved.ParameterTypes.Count);
            foreach (var parameterType in resolved.ParameterTypes)
            {
                parameterTypes.Add(ResolveClassType(parameterType));
            }

            return _metadata.DefineMethodRef(resolved.DeclaringType, resolved.Name, returnType, parameterTypes, resolved.IsStatic);
        }

        /// <summary>把签名中的 Class TypeRef 解析为带 scope 的注册引用（供签名编码使用）。</summary>
        private IlType ResolveClassType(IlType type)
        {
            if (type.Kind == IlTypeKind.Class)
            {
                var resolved = _reader.FindType(type.Reference!.FullName, _metadata);
                if (resolved != null)
                {
                    return IlType.Class(resolved);
                }
            }

            return type;
        }

        // ------------------------------------------------------------------
        // 语句
        // ------------------------------------------------------------------

        private void EmitStatement(IlAssembler il, BoundStatement node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.NopStatement:
                    il.Emit(IlOpCodes.Get("Nop"));
                    break;
                case BoundNodeKind.VariableDeclaration:
                    EmitVariableDeclaration(il, (BoundVariableDeclaration)node);
                    break;
                case BoundNodeKind.LabelStatement:
                    EmitLabelStatement(il, (BoundLabelStatement)node);
                    break;
                case BoundNodeKind.GotoStatement:
                    EmitGotoStatement(il, (BoundGotoStatement)node);
                    break;
                case BoundNodeKind.ConditionalGotoStatement:
                    EmitConditionalGotoStatement(il, (BoundConditionalGotoStatement)node);
                    break;
                case BoundNodeKind.ReturnStatement:
                    EmitReturnStatement(il, (BoundReturnStatement)node);
                    break;
                case BoundNodeKind.ExpressionStatement:
                    EmitExpressionStatement(il, (BoundExpressionStatement)node);
                    break;
                case BoundNodeKind.SequencePointStatement:
                    EmitSequencePointStatement(il, (BoundSequencePointStatement)node);
                    break;
                default:
                    throw new System.Exception($"Unexpected node kind {node.Kind}");
            }
        }

        private void EmitVariableDeclaration(IlAssembler il, BoundVariableDeclaration node)
        {
            EmitExpression(il, node.Initializer);
            il.Emit(IlOpCodes.Get("Stloc"), (ushort)_locals[node.Variable]);
        }

        private void EmitLabelStatement(IlAssembler il, BoundLabelStatement node)
        {
            // 占位 Nop（CollectLabels 预建）：分支目标引用此指令，编码时自动重定位
            il.Emit(_labelTargets[node.Label]);
        }

        private void EmitGotoStatement(IlAssembler il, BoundGotoStatement node)
        {
            il.Emit(IlOpCodes.Get("Br"), _labelTargets[node.Label]);
        }

        private void EmitConditionalGotoStatement(IlAssembler il, BoundConditionalGotoStatement node)
        {
            EmitExpression(il, node.Condition);
            var opCode = node.JumpIfTrue ? "Brtrue" : "Brfalse";
            il.Emit(IlOpCodes.Get(opCode), _labelTargets[node.Label]);
        }

        private void EmitReturnStatement(IlAssembler il, BoundReturnStatement node)
        {
            if (node.Expression != null)
            {
                EmitExpression(il, node.Expression);
            }

            il.Emit(IlOpCodes.Get("Ret"));
        }

        private void EmitExpressionStatement(IlAssembler il, BoundExpressionStatement node)
        {
            EmitExpression(il, node.Expression);

            if (node.Expression.Type != TypeSymbol.Void)
            {
                il.Emit(IlOpCodes.Get("Pop"));
            }
        }

        private void EmitSequencePointStatement(IlAssembler il, BoundSequencePointStatement node)
        {
            EmitStatement(il, node.Statement);
        }

        // ------------------------------------------------------------------
        // 表达式
        // ------------------------------------------------------------------

        private void EmitExpression(IlAssembler il, BoundExpression node)
        {
            if (node.ConstantValue != null)
            {
                EmitConstantExpression(il, node);
                return;
            }

            switch (node.Kind)
            {
                case BoundNodeKind.VariableExpression:
                    EmitVariableExpression(il, (BoundVariableExpression)node);
                    break;
                case BoundNodeKind.AssignmentExpression:
                    EmitAssignmentExpression(il, (BoundAssignmentExpression)node);
                    break;
                case BoundNodeKind.UnaryExpression:
                    EmitUnaryExpression(il, (BoundUnaryExpression)node);
                    break;
                case BoundNodeKind.BinaryExpression:
                    EmitBinaryExpression(il, (BoundBinaryExpression)node);
                    break;
                case BoundNodeKind.CallExpression:
                    EmitCallExpression(il, (BoundCallExpression)node);
                    break;
                case BoundNodeKind.ConversionExpression:
                    EmitConversionExpression(il, (BoundConversionExpression)node);
                    break;
                default:
                    throw new System.Exception($"Unexpected node kind {node.Kind}");
            }
        }

        private void EmitConstantExpression(IlAssembler il, BoundExpression node)
        {
            if (node.Type == TypeSymbol.Boolean)
            {
                var value = (bool)node.ConstantValue.Value;
                il.Emit(IlOpCodes.Get(value ? "Ldc_I4_1" : "Ldc_I4_0"));
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                var value = (int)node.ConstantValue.Value;
                il.Emit(IlOpCodes.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.String)
            {
                var value = (string)node.ConstantValue.Value;
                il.Emit(IlOpCodes.Get("Ldstr"), value);
            }
            else
            {
                throw new System.Exception($"Unexpected constant expression kind {node.Kind}");
            }
        }

        private void EmitVariableExpression(IlAssembler il, BoundVariableExpression node)
        {
            if (node.Variable is ParameterSymbol parameter)
            {
                il.Emit(IlOpCodes.Get("Ldarg"), (ushort)parameter.Ordinal);
            }
            else
            {
                il.Emit(IlOpCodes.Get("Ldloc"), (ushort)_locals[node.Variable]);
            }
        }

        private void EmitAssignmentExpression(IlAssembler il, BoundAssignmentExpression node)
        {
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodes.Get("Dup"));
            il.Emit(IlOpCodes.Get("Stloc"), (ushort)_locals[node.Variable]);
        }

        private void EmitUnaryExpression(IlAssembler il, BoundUnaryExpression node)
        {
            EmitExpression(il, node.Operand);

            if (node.Op.Kind == BoundUnaryOperatorKind.Identity)
            {
                // Done
            }
            else if (node.Op.Kind == BoundUnaryOperatorKind.LogicalNegation)
            {
                il.Emit(IlOpCodes.Get("Ldc_I4_0"));
                il.Emit(IlOpCodes.Get("Ceq"));
            }
            else if (node.Op.Kind == BoundUnaryOperatorKind.Negation)
            {
                il.Emit(IlOpCodes.Get("Neg"));
            }
            else if (node.Op.Kind == BoundUnaryOperatorKind.OnesComplement)
            {
                il.Emit(IlOpCodes.Get("Not"));
            }
            else
            {
                throw new System.Exception($"Unexpected unary operator {SyntaxFacts.GetText(node.Op.SyntaxKind)}({node.Operand.Type})");
            }
        }

        private void EmitBinaryExpression(IlAssembler il, BoundBinaryExpression node)
        {
            if (node.Op.Kind == BoundBinaryOperatorKind.Addition)
            {
                if (node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.String)
                {
                    EmitStringConcatExpression(il, node);
                    return;
                }
            }

            EmitExpression(il, node.Left);
            EmitExpression(il, node.Right);

            if (node.Op.Kind == BoundBinaryOperatorKind.Equals)
            {
                if (node.Left.Type == TypeSymbol.Any && node.Right.Type == TypeSymbol.Any ||
                    node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.String)
                {
                    il.Emit(IlOpCodes.Get("Call"), _objectEqualsReference);
                    return;
                }
            }

            if (node.Op.Kind == BoundBinaryOperatorKind.NotEquals)
            {
                if (node.Left.Type == TypeSymbol.Any && node.Right.Type == TypeSymbol.Any ||
                    node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.String)
                {
                    il.Emit(IlOpCodes.Get("Call"), _objectEqualsReference);
                    il.Emit(IlOpCodes.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodes.Get("Ceq"));
                    return;
                }
            }

            switch (node.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    il.Emit(IlOpCodes.Get("Add"));
                    break;
                case BoundBinaryOperatorKind.Subtraction:
                    il.Emit(IlOpCodes.Get("Sub"));
                    break;
                case BoundBinaryOperatorKind.Multiplication:
                    il.Emit(IlOpCodes.Get("Mul"));
                    break;
                case BoundBinaryOperatorKind.Division:
                    il.Emit(IlOpCodes.Get("Div"));
                    break;
                case BoundBinaryOperatorKind.LogicalAnd:
                case BoundBinaryOperatorKind.BitwiseAnd:
                    il.Emit(IlOpCodes.Get("And"));
                    break;
                case BoundBinaryOperatorKind.LogicalOr:
                case BoundBinaryOperatorKind.BitwiseOr:
                    il.Emit(IlOpCodes.Get("Or"));
                    break;
                case BoundBinaryOperatorKind.BitwiseXor:
                    il.Emit(IlOpCodes.Get("Xor"));
                    break;
                case BoundBinaryOperatorKind.Equals:
                    il.Emit(IlOpCodes.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.NotEquals:
                    il.Emit(IlOpCodes.Get("Ceq"));
                    il.Emit(IlOpCodes.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodes.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.Less:
                    il.Emit(IlOpCodes.Get("Clt"));
                    break;
                case BoundBinaryOperatorKind.LessOrEquals:
                    il.Emit(IlOpCodes.Get("Cgt"));
                    il.Emit(IlOpCodes.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodes.Get("Ceq"));
                    break;
                case BoundBinaryOperatorKind.Greater:
                    il.Emit(IlOpCodes.Get("Cgt"));
                    break;
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    il.Emit(IlOpCodes.Get("Clt"));
                    il.Emit(IlOpCodes.Get("Ldc_I4_0"));
                    il.Emit(IlOpCodes.Get("Ceq"));
                    break;
                default:
                    throw new System.Exception($"Unexpected binary operator {SyntaxFacts.GetText(node.Op.SyntaxKind)}({node.Left.Type}, {node.Right.Type})");
            }
        }

        private void EmitStringConcatExpression(IlAssembler il, BoundBinaryExpression node)
        {
            var nodes = FoldConstants(node.Syntax, Flatten(node)).ToList();

            switch (nodes.Count)
            {
                case 0:
                    il.Emit(IlOpCodes.Get("Ldstr"), string.Empty);
                    break;
                case 1:
                    EmitExpression(il, nodes[0]);
                    break;
                case 2:
                    EmitExpression(il, nodes[0]);
                    EmitExpression(il, nodes[1]);
                    il.Emit(IlOpCodes.Get("Call"), _stringConcat2Reference);
                    break;
                case 3:
                    EmitExpression(il, nodes[0]);
                    EmitExpression(il, nodes[1]);
                    EmitExpression(il, nodes[2]);
                    il.Emit(IlOpCodes.Get("Call"), _stringConcat3Reference);
                    break;
                case 4:
                    EmitExpression(il, nodes[0]);
                    EmitExpression(il, nodes[1]);
                    EmitExpression(il, nodes[2]);
                    EmitExpression(il, nodes[3]);
                    il.Emit(IlOpCodes.Get("Call"), _stringConcat4Reference);
                    break;
                default:
                    il.Emit(IlOpCodes.Get("Ldc_I4"), nodes.Count);
                    il.Emit(IlOpCodes.Get("Newarr"), _stringType);
                    for (var i = 0; i < nodes.Count; i++)
                    {
                        il.Emit(IlOpCodes.Get("Dup"));
                        il.Emit(IlOpCodes.Get("Ldc_I4"), i);
                        EmitExpression(il, nodes[i]);
                        il.Emit(IlOpCodes.Get("Stelem_Ref"));
                    }

                    il.Emit(IlOpCodes.Get("Call"), _stringConcatArrayReference);
                    break;
            }

            static IEnumerable<BoundExpression> Flatten(BoundExpression node)
            {
                if (node is BoundBinaryExpression binaryExpression &&
                    binaryExpression.Op.Kind == BoundBinaryOperatorKind.Addition &&
                    binaryExpression.Left.Type == TypeSymbol.String &&
                    binaryExpression.Right.Type == TypeSymbol.String)
                {
                    foreach (var result in Flatten(binaryExpression.Left))
                    {
                        yield return result;
                    }

                    foreach (var result in Flatten(binaryExpression.Right))
                    {
                        yield return result;
                    }
                }
                else
                {
                    if (node.Type != TypeSymbol.String)
                    {
                        throw new System.Exception($"Unexpected node type in string concatenation: {node.Type}");
                    }

                    yield return node;
                }
            }

            static IEnumerable<BoundExpression> FoldConstants(SyntaxNode syntax, IEnumerable<BoundExpression> nodes)
            {
                System.Text.StringBuilder? stringBuilder = null;
                foreach (var node in nodes)
                {
                    if (node.ConstantValue != null)
                    {
                        var stringValue = (string)node.ConstantValue.Value;
                        if (string.IsNullOrEmpty(stringValue))
                        {
                            continue;
                        }

                        stringBuilder ??= new System.Text.StringBuilder();
                        stringBuilder.Append(stringValue);
                    }
                    else
                    {
                        if (stringBuilder?.Length > 0)
                        {
                            yield return new BoundLiteralExpression(syntax, stringBuilder.ToString());
                            stringBuilder.Clear();
                        }

                        yield return node;
                    }
                }

                if (stringBuilder?.Length > 0)
                {
                    yield return new BoundLiteralExpression(syntax, stringBuilder.ToString());
                }
            }
        }

        private void EmitCallExpression(IlAssembler il, BoundCallExpression node)
        {
            if (node.Function == BuiltinFunctions.Random)
            {
                il.Emit(IlOpCodes.Get("Call"), _randomGetSharedReference);
                foreach (var argument in node.Arguments)
                {
                    EmitExpression(il, argument);
                }

                il.Emit(IlOpCodes.Get("Callvirt"), _randomNextReference);
                return;
            }

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            if (node.Function == BuiltinFunctions.Print)
            {
                il.Emit(IlOpCodes.Get("Call"), _consoleWriteLineReference);
            }
            else if (node.Function == BuiltinFunctions.Input)
            {
                il.Emit(IlOpCodes.Get("Call"), _consoleReadLineReference);
            }
            else
            {
                var methodDefinition = _methods[node.Function];
                il.Emit(IlOpCodes.Get("Call"), methodDefinition);
            }
        }

        private void EmitConversionExpression(IlAssembler il, BoundConversionExpression node)
        {
            EmitExpression(il, node.Expression);

            var needBoxing = node.Expression.Type == TypeSymbol.Boolean || node.Expression.Type == TypeSymbol.Int32;
            if (needBoxing)
            {
                var type = node.Expression.Type == TypeSymbol.Boolean
                    ? RequireType("System.Boolean")
                    : RequireType("System.Int32");
                il.Emit(IlOpCodes.Get("Box"), type);
            }

            if (node.Type == TypeSymbol.Any)
            {
                // Done
            }
            else if (node.Type == TypeSymbol.Boolean)
            {
                il.Emit(IlOpCodes.Get("Call"), _convertToBooleanReference);
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodes.Get("Call"), _convertToInt32Reference);
            }
            else if (node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodes.Get("Call"), _convertToStringReference);
            }
            else
            {
                throw new System.Exception($"Unexpected conversion from {node.Expression.Type} to {node.Type}");
            }
        }
    }
}
