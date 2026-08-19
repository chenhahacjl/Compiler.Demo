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
        private readonly Dictionary<BoundExpression, int> _temporaryLocalIndices = new Dictionary<BoundExpression, int>();
        private List<IlType>? _currentFunctionLocals;
        private readonly Dictionary<BoundLabel, IlInstruction> _labelTargets = new Dictionary<BoundLabel, IlInstruction>();

        private FunctionSymbol? _entryFunction;
        private bool _entryVoidMain;

        private readonly IlTypeRef _objectType;
        private readonly IlTypeRef _stringType;
        private readonly IlTypeDef _typeDefinition;
        private readonly Dictionary<ClassTypeSymbol, IlTypeDef> _classTypeDefs = new Dictionary<ClassTypeSymbol, IlTypeDef>();
        private readonly Dictionary<FieldSymbol, IlFieldDef> _fieldDefs = new Dictionary<FieldSymbol, IlFieldDef>();
        private bool _currentMethodIsInstance;

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
        private readonly IlMethodRef _stringCharsReference;
        private readonly IlMethodRef _stringLengthReference;
        private readonly IlMethodRef _stringSubstringReference;
        private readonly IlMethodRef _randomCtorReference;
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
            _stringCharsReference = RequireMethod("System.String", "get_Chars", new[] { "System.Int32" });
            _stringLengthReference = RequireMethod("System.String", "get_Length", System.Array.Empty<string>());
            _stringSubstringReference = RequireMethod("System.String", "Substring", new[] { "System.Int32", "System.Int32" });
            _randomCtorReference = RequireMethod("System.Random", ".ctor", System.Array.Empty<string>());
            _randomNextReference = RequireMethod("System.Random", "Next", new[] { "System.Int32" });
            _debuggableAttributeCtorReference = RequireMethod("System.Diagnostics.DebuggableAttribute", ".ctor", new[] { "System.Boolean", "System.Boolean" });

            _typeDefinition = new IlTypeDef("Program", "", _objectType);
            _metadata.AddTypeDef(_typeDefinition);
        }

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath)
            => Emit(program, moduleName, references, outputPath, IlTarget.Default, emitLibrary: false);

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath, IlTarget target)
            => Emit(program, moduleName, references, outputPath, target, emitLibrary: false);

        public static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary)
        {
            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            var emitter = new Emitter(moduleName, references);

            return emitter.Emit(program, outputPath, target, emitLibrary);
        }

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath)
            => Emit(program, outputPath, IlTarget.Default);

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target)
            => Emit(program, outputPath, target, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(BoundProgram program, string outputPath, IlTarget target, bool emitLibrary)
        {
            _entryFunction = emitLibrary ? null : program.MainFunction;

            // 1. 收集 class（按出现顺序）→ 建 IlTypeDef + 字段
            foreach (var classType in program.Classes)
            {
                EmitClassDeclaration(classType);
            }

            // 2. 方法声明（顺序 = 顶层 + 各 class 方法，与 typeDefs 分组一致）
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
                _entryVoidMain = _entryFunction == functionWithBody.Key && functionWithBody.Key.ReturnType == TypeSymbol.Void;
                var (code, localSigToken, maxStack) = EmitFunctionBody(method, functionWithBody.Value);
                bodies.Add(new ManagedPEWriter.MethodBodyBlob(code, localSigToken, (ushort)maxStack));
            }

            _metadata.AddCustomAttribute(new IlCustomAttribute(_debuggableAttributeCtorReference, MetadataBuilder.EncodeDebuggableAttributeBlob()));

            var entryPointToken = program.MainFunction == null ? 0 : _metadata.BuildTokenMap()[_methods[program.MainFunction]];
            var pe = ManagedPEWriter.Build(_moduleName, methods, bodies, _metadata, entryPointToken, target);

            File.WriteAllBytes(outputPath, pe);
            // 库（dll）不直接运行，不写 runtimeconfig；netcore exe 写。
            if (!emitLibrary && target.Runtime == IlRuntime.NetCore)
            {
                WriteRuntimeConfig(outputPath, target);
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        /// <summary>framework-dependent 运行所需的 runtimeconfig.json。</summary>
        private static void WriteRuntimeConfig(string outputPath, IlTarget target)
        {
            var runtimeConfigPath = Path.ChangeExtension(outputPath, ".runtimeconfig.json");
            File.WriteAllText(runtimeConfigPath, target.GetRuntimeConfigJson());
        }

        private void EmitFunctionDeclaration(FunctionSymbol function)
        {
            // 入口统一为 static int Main()：语言 void main（默认返回 0）→ IL 返回 int，尾部补 0
            var returnType = _entryFunction == function && function.ReturnType == TypeSymbol.Void
                ? ToIlType(TypeSymbol.Int32)
                : ToIlType(function.ReturnType);
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

            var isInstance = function.ContainingClass != null;
            var name = function.Syntax is ConstructorDeclarationSyntax ? ".ctor" : function.Name;

            var method = new IlMethodDef(name, returnType, parameterTypes, null, function.IsExtern ? function.DllName : null, null, callingConvention, isStatic: !isInstance);
            _methods.Add(function, method);

            var declaringType = isInstance ? _classTypeDefs[function.ContainingClass!] : _typeDefinition;
            _metadata.AddMethodDef(declaringType, method);
        }

        private void EmitClassDeclaration(ClassTypeSymbol classType)
        {
            var typeDef = new IlTypeDef(classType.Name, classType.Namespace, _objectType, isPublic: classType.IsPublic);
            _classTypeDefs.Add(classType, typeDef);

            foreach (var field in classType.Fields)
            {
                var fieldDef = new IlFieldDef(field.Name, ToIlType(field.Type), isPublic: field.IsPublic);
                typeDef.Fields.Add(fieldDef);
                _fieldDefs.Add(field, fieldDef);
            }

            _metadata.AddTypeDef(typeDef);
        }

        private (byte[] Code, uint LocalSigToken, int MaxStack) EmitFunctionBody(IlMethodDef method, BoundBlockStatement body)
        {
            _locals.Clear();
            _labelTargets.Clear();
            _temporaryLocalIndices.Clear();
            _currentMethodIsInstance = !method.IsStatic;

            var assembler = new IlAssembler();

            // 预收集局部变量（按声明顺序分配索引）
            var localTypes = new List<IlType>();
            _currentFunctionLocals = localTypes;
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

        private IlType ToIlType(TypeSymbol type)
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

            if (type == TypeSymbol.Char)
            {
                return IlType.Char;
            }

            if (type == TypeSymbol.Byte)
            {
                return IlType.Byte;
            }

            if (type == TypeSymbol.Double)
            {
                return IlType.Double;
            }

            if (type == TypeSymbol.String)
            {
                return IlType.String;
            }

            if (type == TypeSymbol.Void)
            {
                return IlType.Void;
            }

            if (type is EnumTypeSymbol)
            {
                return IlType.Int32;
            }

            if (type is ClassTypeSymbol classType)
            {
                if (classType.IsExternal)
                {
                    return IlType.Class(ResolveExternalTypeRef(classType));
                }

                return IlType.Class(_classTypeDefs[classType]);
            }

            if (type.ElementType != null)
            {
                return IlType.SzArrayOf(ToIlType(type.ElementType));
            }

            throw new System.Exception($"Unexpected type {type}");
        }

        private IlTypeRef RequireType(string fullName)
        {
            return _reader.FindType(fullName, _metadata) ?? throw new System.Exception($"Type '{fullName}' not found in references.");
        }

        private IlTypeRef ResolveExternalTypeRef(ClassTypeSymbol classType)
        {
            return RequireType(classType.FullName);
        }

        private IlMethodRef RequireMethod(string typeFullName, string methodName, string[] parameterTypeNames)
        {
            var resolved = _reader.FindMethod(typeFullName, methodName, parameterTypeNames, _metadata);
            if (resolved == null)
            {
                throw new System.Exception($"Method '{typeFullName}.{methodName}' not found in references.");
            }

            return ResolveMethodRef(resolved);
        }

        private IlMethodRef ResolveMethodRef(ResolvedMethodInfo resolved)
        {
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
            else if (_entryVoidMain)
            {
                // void main() 的（显式 return; 或隐式函数尾）返回 = 默认退出码 0
                il.Emit(IlOpCodes.Get("Ldc_I4_0"));
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
                case BoundNodeKind.ArrayCreationExpression:
                    EmitArrayCreationExpression(il, (BoundArrayCreationExpression)node);
                    break;
                case BoundNodeKind.ElementAccessExpression:
                    EmitElementAccessExpression(il, (BoundElementAccessExpression)node);
                    break;
                case BoundNodeKind.ElementAssignmentExpression:
                    EmitElementAssignmentExpression(il, (BoundElementAssignmentExpression)node);
                    break;
                case BoundNodeKind.MemberAccessExpression:
                    EmitMemberAccessExpression(il, (BoundMemberAccessExpression)node);
                    break;
                case BoundNodeKind.MemberCallExpression:
                    EmitMemberCallExpression(il, (BoundMemberCallExpression)node);
                    break;
                case BoundNodeKind.MemberAssignmentExpression:
                    EmitMemberAssignmentExpression(il, (BoundMemberAssignmentExpression)node);
                    break;
                case BoundNodeKind.ObjectCreationExpression:
                    EmitObjectCreationExpression(il, (BoundObjectCreationExpression)node);
                    break;
                case BoundNodeKind.ThisExpression:
                    EmitThisExpression(il, (BoundThisExpression)node);
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
            else if (node.Type == TypeSymbol.Char)
            {
                var value = (int)(char)node.ConstantValue.Value;
                il.Emit(IlOpCodes.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.Byte)
            {
                var value = Convert.ToInt32(node.ConstantValue.Value);
                il.Emit(IlOpCodes.Get("Ldc_I4"), value);
            }
            else if (node.Type == TypeSymbol.Double)
            {
                var value = (double)node.ConstantValue.Value;
                il.Emit(IlOpCodes.Get("Ldc_R8"), value);
            }
            else if (node.Type is EnumTypeSymbol)
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
                // 实例方法 arg0 = this，参数从 arg1 起
                var argIndex = parameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                il.Emit(IlOpCodes.Get("Ldarg"), (ushort)argIndex);
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

                if (node.Left.Type == TypeSymbol.String && node.Right.Type == TypeSymbol.Double)
                {
                    EmitExpression(il, node.Left);
                    EmitExpression(il, node.Right);
                    il.Emit(IlOpCodes.Get("Box"), RequireType("System.Double"));
                    il.Emit(IlOpCodes.Get("Call"), _convertToStringReference);
                    il.Emit(IlOpCodes.Get("Call"), _stringConcat2Reference);
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
                // 6d-4：Random.get_Shared 是 .NET 6+ API，mscorlib 没有；改用 new Random() 双运行时兼容。
                il.Emit(IlOpCodes.Get("Newobj"), _randomCtorReference);
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

            if (node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.String)
            {
                var type = RequireType("System.Char");
                il.Emit(IlOpCodes.Get("Box"), type);
                il.Emit(IlOpCodes.Get("Call"), _convertToStringReference);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Byte && node.Type == TypeSymbol.Int32)
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Byte)
            {
                // 无符号字节截断，与 C# (byte)300 == 44 语义一致
                il.Emit(IlOpCodes.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Double ||
                node.Expression.Type == TypeSymbol.Byte && node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodes.Get("Conv_R8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodes.Get("Conv_I4"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.Byte)
            {
                il.Emit(IlOpCodes.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodes.Get("Box"), RequireType("System.Double"));
                il.Emit(IlOpCodes.Get("Call"), _convertToStringReference);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Char ||
                node.Expression.Type is EnumTypeSymbol && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type is EnumTypeSymbol)
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            var needBoxing = node.Expression.Type == TypeSymbol.Boolean || node.Expression.Type == TypeSymbol.Int32 || node.Expression.Type == TypeSymbol.Char || node.Expression.Type == TypeSymbol.Byte || node.Expression.Type == TypeSymbol.Double || node.Expression.Type is EnumTypeSymbol;
            if (needBoxing)
            {
                var type = node.Expression.Type == TypeSymbol.Boolean
                    ? RequireType("System.Boolean")
                    : node.Expression.Type == TypeSymbol.Int32
                        ? RequireType("System.Int32")
                        : node.Expression.Type == TypeSymbol.Char
                            ? RequireType("System.Char")
                            : node.Expression.Type == TypeSymbol.Byte
                                ? RequireType("System.Byte")
                                : node.Expression.Type == TypeSymbol.Double
                                    ? RequireType("System.Double")
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

        private void EmitArrayCreationExpression(IlAssembler il, BoundArrayCreationExpression node)
        {
            EmitExpression(il, node.Length);
            il.Emit(IlOpCodes.Get("Newarr"), RequireType(RequireArrayElementTypeName(node.Type)));

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                il.Emit(IlOpCodes.Get("Dup"));
                il.Emit(IlOpCodes.Get("Ldc_I4"), i);
                EmitExpression(il, node.Initializers[i]);
                EmitElementStore(il, node.Type.ElementType!);
            }
        }

        private static string RequireArrayElementTypeName(TypeSymbol arrayType)
        {
            if (arrayType.ElementType == TypeSymbol.Int32)
            {
                return "System.Int32";
            }

            if (arrayType.ElementType == TypeSymbol.Char)
            {
                return "System.Char";
            }

            if (arrayType.ElementType == TypeSymbol.Byte)
            {
                return "System.Byte";
            }

            if (arrayType.ElementType == TypeSymbol.Double)
            {
                return "System.Double";
            }

            if (arrayType.ElementType is EnumTypeSymbol)
            {
                return "System.Int32";
            }

            if (arrayType.ElementType == TypeSymbol.Boolean)
            {
                return "System.Boolean";
            }

            if (arrayType.ElementType == TypeSymbol.String)
            {
                return "System.String";
            }

            throw new System.NotSupportedException($"Array of '{arrayType.ElementType}' is not yet supported by the IL emitter.");
        }

        private void EmitElementAccessExpression(IlAssembler il, BoundElementAccessExpression node)
        {
            EmitExpression(il, node.Target);
            EmitExpression(il, node.Index);

            if (node.Target.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodes.Get("Callvirt"), _stringCharsReference);
                return;
            }

            if (node.Type == TypeSymbol.Char)
            {
                il.Emit(IlOpCodes.Get("Ldelem_U2"));
            }
            else if (node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodes.Get("Ldelem_R8"));
            }
            else if (node.Type == TypeSymbol.Boolean || node.Type == TypeSymbol.Byte)
            {
                il.Emit(IlOpCodes.Get("Ldelem_U1"));
            }
            else if (node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodes.Get("Ldelem_Ref"));
            }
            else if (node.Type.ElementType != null)
            {
                throw new System.NotSupportedException("Jagged arrays are not yet supported by the IL emitter.");
            }
            else
            {
                il.Emit(IlOpCodes.Get("Ldelem_I4"));
            }
        }

        private void EmitElementAssignmentExpression(IlAssembler il, BoundElementAssignmentExpression node)
        {
            var temporaryLocal = AllocateTemporaryLocal(node);

            EmitExpression(il, node.Target.Target);
            EmitExpression(il, node.Target.Index);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodes.Get("Dup"));
            il.Emit(IlOpCodes.Get("Stloc"), (ushort)temporaryLocal);
            EmitElementStore(il, node.Type);
            il.Emit(IlOpCodes.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private int AllocateTemporaryLocal(BoundExpression node)
        {
            if (!_temporaryLocalIndices.TryGetValue(node, out var index))
            {
                index = _currentFunctionLocals!.Count;
                _temporaryLocalIndices.Add(node, index);
                _currentFunctionLocals.Add(ToIlType(node.Type));
            }

            return index;
        }

        private static void EmitElementStore(IlAssembler il, TypeSymbol elementType)
        {
            if (elementType == TypeSymbol.Boolean || elementType == TypeSymbol.Byte)
            {
                il.Emit(IlOpCodes.Get("Stelem_I1"));
            }
            else if (elementType == TypeSymbol.Char)
            {
                il.Emit(IlOpCodes.Get("Stelem_I2"));
            }
            else if (elementType == TypeSymbol.Double)
            {
                il.Emit(IlOpCodes.Get("Stelem_R8"));
            }
            else if (elementType == TypeSymbol.String)
            {
                il.Emit(IlOpCodes.Get("Stelem_Ref"));
            }
            else if (elementType.ElementType != null)
            {
                throw new System.NotSupportedException("Jagged arrays are not yet supported by the IL emitter.");
            }
            else
            {
                il.Emit(IlOpCodes.Get("Stelem_I4"));
            }
        }

        private void EmitMemberAccessExpression(IlAssembler il, BoundMemberAccessExpression node)
        {
            EmitExpression(il, node.Target);

            if (node.Field != null)
            {
                il.Emit(IlOpCodes.Get("Ldfld"), _fieldDefs[node.Field]);
                return;
            }

            if (node.Target.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodes.Get("Callvirt"), _stringLengthReference);
                return;
            }

            il.Emit(IlOpCodes.Get("Ldlen"));
        }

        private void EmitMemberCallExpression(IlAssembler il, BoundMemberCallExpression node)
        {
            EmitExpression(il, node.Expression);
            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            if (node.Method != null)
            {
                if (node.Method.ContainingClass!.IsExternal)
                {
                    var parameterNames = new string[node.Arguments.Length];
                    for (var i = 0; i < node.Arguments.Length; i++)
                    {
                        parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                    }

                    var methodRef = _reader.FindMethod(node.Method.ContainingClass.FullName, node.Identifier, parameterNames, _metadata);
                    if (methodRef == null)
                    {
                        throw new System.Exception($"外部方法 {node.Method.ContainingClass.FullName}.{node.Identifier} 未找到。");
                    }

                    il.Emit(IlOpCodes.Get("Callvirt"), ResolveMethodRef(methodRef));
                    return;
                }

                // 本地实例方法：Callvirt（this 已在栈上）
                il.Emit(IlOpCodes.Get("Callvirt"), _methods[node.Method]);
                return;
            }

            if (node.Expression.Type == TypeSymbol.String && node.Identifier == "substring")
            {
                il.Emit(IlOpCodes.Get("Callvirt"), _stringSubstringReference);
                return;
            }

            throw new System.Exception($"Unexpected member call {node.Identifier}");
        }

        private void EmitMemberAssignmentExpression(IlAssembler il, BoundMemberAssignmentExpression node)
        {
            var temporaryLocal = AllocateTemporaryLocal(node);

            EmitExpression(il, node.Target);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodes.Get("Dup"));
            il.Emit(IlOpCodes.Get("Stloc"), (ushort)temporaryLocal);
            il.Emit(IlOpCodes.Get("Stfld"), _fieldDefs[node.Field]);
            il.Emit(IlOpCodes.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private void EmitObjectCreationExpression(IlAssembler il, BoundObjectCreationExpression node)
        {
            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            var classType = (ClassTypeSymbol)node.Type;

            if (classType.IsExternal)
            {
                var parameterNames = new string[node.Arguments.Length];
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                }

                var ctorRef = _reader.FindMethod(classType.FullName, ".ctor", parameterNames, _metadata);
                if (ctorRef == null)
                {
                    throw new System.Exception($"外部类型 {classType.FullName} 的构造函数未找到。");
                }

                il.Emit(IlOpCodes.Get("Newobj"), ResolveMethodRef(ctorRef));
                return;
            }

            var ctor = classType.GetMethod(classType.Name);
            if (ctor == null)
            {
                throw new System.Exception($"Class {classType.Name} has no constructor.");
            }

            il.Emit(IlOpCodes.Get("Newobj"), _methods[ctor]);
        }

        private void EmitThisExpression(IlAssembler il, BoundThisExpression node)
        {
            il.Emit(IlOpCodes.Get("Ldarg"), (ushort)0);
        }
    }
}
