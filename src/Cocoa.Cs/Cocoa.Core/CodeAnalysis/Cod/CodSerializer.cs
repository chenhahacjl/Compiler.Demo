using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Cod
{
    /// <summary>
    /// `.cod` 语义层序列化器：符号表 + 降级 BoundProgram（函数体）文本 round-trip。
    /// 双后端共用（native → BoundTreeToIr，IL → IlEmitter）；语法节点（Syntax）不序列化（置 null）。
    /// </summary>
    internal static class CodSerializer
    {
        public const string Magic = "COCOD";
        public const int Version = 1;

        // ---------------------------------------------------------------- write

        public static void Write(TextWriter writer, CodProgram program)
        {
            var registry = new Registry();
            var labelsByFunction = new Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>>(ReferenceEqualityComparer.Instance);

            // 收集符号（分配 id）——函数体按 Functions（声明序）遍历，保证确定性（ImmutableDictionary 迭代序不稳定）
            foreach (var e in program.Enums)
            {
                registry.RegisterType(e);
            }
            foreach (var c in program.Classes)
            {
                registry.RegisterClass(c);
            }
            foreach (var f in program.Functions)
            {
                registry.RegisterFunction(f);
            }
            foreach (var g in program.Globals)
            {
                registry.RegisterVariable(g);
            }
            foreach (var fn in program.Functions)
            {
                if (!program.Bodies.TryGetValue(fn, out var body))
                {
                    continue;
                }

                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                CollectBody(registry, body, labels);
                labelsByFunction[fn] = labels;
            }

            var w = new Writer(writer);
            w.Open("cod");
            w.Field(Magic);
            w.Field(Version);

            // 符号表（按 id 顺序）
            w.Open("symbols");
            foreach (var emitter in registry.Emitters)
            {
                emitter(w, registry);
            }
            w.End();

            // 函数体
            w.Open("bodies");
            foreach (var fn in program.Functions)
            {
                // 容器类方法（静态）序列化函数体；实例方法/隐式构造等常规方法不在容器序列化范围，跳过
                if (fn.ContainingClass != null && !fn.IsStatic)
                {
                    continue;
                }

                if (!program.Bodies.TryGetValue(fn, out var body))
                {
                    continue;
                }

                w.Open("body");
                w.Field(registry.Get(fn));
                WriteStatement(w, registry, labelsByFunction[fn], body);
                w.End();
            }
            w.End();

            // 依赖清单
            w.Open("manifest");
            w.Open("requires");
            w.Field(RequirementName(program.Requires));
            w.End();
            foreach (var p in program.Platforms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                w.Open("platform");
                w.Field(Str(p));
                w.End();
            }
            foreach (var d in program.DotnetReferences.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                w.Open("refdll");
                w.Field(Str(d));
                w.End();
            }
            foreach (var c in program.CodReferences.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                w.Open("refcod");
                w.Field(Str(c));
                w.End();
            }
            foreach (var i in program.NativeImports.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                w.Open("import");
                w.Field(Str(i));
                w.End();
            }
            foreach (var ns in program.Namespaces.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                w.Open("ns");
                w.Field(Str(ns));
                w.End();
            }
            w.End(); // manifest

            w.End(); // cod
        }

        private static string RequirementName(CodRequirement r)
        {
            return r switch
            {
                CodRequirement.Any => "any",
                CodRequirement.DotNet => "dotnet",
                _ => "any",
            };
        }

        private static CodRequirement ParseRequirement(string name)
        {
            return name switch
            {
                "dotnet" => CodRequirement.DotNet,
                _ => CodRequirement.Any,
            };
        }

        private static void CollectBody(Registry registry, BoundStatement statement, Dictionary<string, BoundLabel> labels)
        {
            switch (statement.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    foreach (var s in ((BoundBlockStatement)statement).Statements)
                    {
                        CollectBody(registry, s, labels);
                    }
                    break;
                case BoundNodeKind.VariableDeclaration:
                    {
                        var d = (BoundVariableDeclaration)statement;
                        registry.RegisterVariable(d.Variable);
                        CollectExpression(registry, d.Initializer, labels);
                        break;
                    }
                case BoundNodeKind.IfStatement:
                    {
                        var n = (BoundIfStatement)statement;
                        CollectExpression(registry, n.Condition, labels);
                        CollectBody(registry, n.ThenStatement, labels);
                        if (n.ElseStatement != null)
                        {
                            CollectBody(registry, n.ElseStatement, labels);
                        }
                        break;
                    }
                case BoundNodeKind.WhileStatement:
                    {
                        var n = (BoundWhileStatement)statement;
                        CollectExpression(registry, n.Condition, labels);
                        CollectBody(registry, n.Body, labels);
                        break;
                    }
                case BoundNodeKind.DoWhileStatement:
                    {
                        var n = (BoundDoWhileStatement)statement;
                        CollectBody(registry, n.Body, labels);
                        CollectExpression(registry, n.Condition, labels);
                        break;
                    }
                case BoundNodeKind.ForStatement:
                    {
                        var n = (BoundForStatement)statement;
                        registry.RegisterVariable(n.Variable);
                        CollectExpression(registry, n.LowerBound, labels);
                        CollectExpression(registry, n.UpperBound, labels);
                        if (n.Step != null)
                        {
                            CollectExpression(registry, n.Step, labels);
                        }

                        CollectBody(registry, n.Body, labels);
                        break;
                    }
                case BoundNodeKind.LabelStatement:
                    {
                        var n = (BoundLabelStatement)statement;
                        labels[n.Label.Name] = n.Label;
                        break;
                    }
                case BoundNodeKind.ConditionalGotoStatement:
                    CollectExpression(registry, ((BoundConditionalGotoStatement)statement).Condition, labels);
                    break;
                case BoundNodeKind.ReturnStatement:
                    {
                        var n = (BoundReturnStatement)statement;
                        if (n.Expression != null)
                        {
                            CollectExpression(registry, n.Expression, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ExpressionStatement:
                    CollectExpression(registry, ((BoundExpressionStatement)statement).Expression, labels);
                    break;
                case BoundNodeKind.SequencePointStatement:
                    CollectBody(registry, ((BoundSequencePointStatement)statement).Statement, labels);
                    break;
            }
        }

        private static void CollectExpression(Registry registry, BoundExpression expression, Dictionary<string, BoundLabel> labels)
        {
            switch (expression.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    registry.RegisterType(expression.Type);
                    break;
                case BoundNodeKind.VariableExpression:
                    registry.RegisterVariable(((BoundVariableExpression)expression).Variable);
                    break;
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)expression;
                        registry.RegisterVariable(n.Variable);
                        CollectExpression(registry, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)expression;
                        registry.RegisterVariable(n.Variable);
                        registry.RegisterType(n.Op.LeftType);
                        registry.RegisterType(n.Op.RightType);
                        registry.RegisterType(n.Op.ResultType);
                        CollectExpression(registry, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.UnaryExpression:
                    {
                        var n = (BoundUnaryExpression)expression;
                        registry.RegisterType(n.Op.OperandType);
                        registry.RegisterType(n.Op.ResultType);
                        CollectExpression(registry, n.Operand, labels);
                        break;
                    }
                case BoundNodeKind.BinaryExpression:
                    {
                        var n = (BoundBinaryExpression)expression;
                        registry.RegisterType(n.Op.LeftType);
                        registry.RegisterType(n.Op.RightType);
                        registry.RegisterType(n.Op.ResultType);
                        CollectExpression(registry, n.Left, labels);
                        CollectExpression(registry, n.Right, labels);
                        break;
                    }
                case BoundNodeKind.ConditionalExpression:
                    {
                        var n = (BoundConditionalExpression)expression;
                        CollectExpression(registry, n.Condition, labels);
                        CollectExpression(registry, n.WhenTrue, labels);
                        CollectExpression(registry, n.WhenFalse, labels);
                        break;
                    }
                case BoundNodeKind.CallExpression:
                    {
                        var n = (BoundCallExpression)expression;
                        registry.RegisterFunction(n.Function);
                        foreach (var a in n.Arguments)
                        {
                            CollectExpression(registry, a, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ConversionExpression:
                    {
                        var n = (BoundConversionExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, n.Length, labels);
                        foreach (var i in n.Initializers)
                        {
                            CollectExpression(registry, i, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ElementAccessExpression:
                    {
                        var n = (BoundElementAccessExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, n.Target, labels);
                        CollectExpression(registry, n.Index, labels);
                        break;
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, n.Target, labels);
                        CollectExpression(registry, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.MemberAccessExpression:
                    {
                        var n = (BoundMemberAccessExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, n.Target, labels);
                        break;
                    }
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, n.Expression, labels);
                        foreach (var a in n.Arguments)
                        {
                            CollectExpression(registry, a, labels);
                        }
                        break;
                    }
                case BoundNodeKind.StaticTypeExpression:
                    {
                        var n = (BoundStaticTypeExpression)expression;
                        registry.RegisterType(n.Type);
                        break;
                    }
                case BoundNodeKind.IsExpression:
                    {
                        var n = (BoundIsExpression)expression;
                        registry.RegisterType(n.TargetType);
                        CollectExpression(registry, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.AsExpression:
                    {
                        var n = (BoundAsExpression)expression;
                        registry.RegisterType(n.TargetType);
                        CollectExpression(registry, n.Expression, labels);
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- write: statements

        private static void WriteStatement(Writer w, Registry registry, Dictionary<string, BoundLabel> labels, BoundStatement statement)
        {
            switch (statement.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    {
                        var n = (BoundBlockStatement)statement;
                        w.Open("block");
                        w.Field(n.Statements.Length);
                        foreach (var s in n.Statements)
                        {
                            WriteStatement(w, registry, labels, s);
                        }
                        w.End();
                        break;
                    }
                case BoundNodeKind.NopStatement:
                    w.Open("nop");
                    w.End();
                    break;
                case BoundNodeKind.VariableDeclaration:
                    {
                        var n = (BoundVariableDeclaration)statement;
                        w.Open("vardecl");
                        w.Field(registry.Get(n.Variable));
                        WriteExpression(w, registry, labels, n.Initializer);
                        w.End();
                        break;
                    }
                case BoundNodeKind.IfStatement:
                    {
                        var n = (BoundIfStatement)statement;
                        w.Open("if");
                        WriteExpression(w, registry, labels, n.Condition);
                        WriteStatement(w, registry, labels, n.ThenStatement);
                        WriteNullableStatement(w, registry, labels, n.ElseStatement);
                        w.End();
                        break;
                    }
                case BoundNodeKind.WhileStatement:
                    {
                        var n = (BoundWhileStatement)statement;
                        w.Open("while");
                        WriteExpression(w, registry, labels, n.Condition);
                        WriteStatement(w, registry, labels, n.Body);
                        w.Field(Str(n.BreakLabel.Name));
                        w.Field(Str(n.ContinueLabel.Name));
                        w.End();
                        break;
                    }
                case BoundNodeKind.DoWhileStatement:
                    {
                        var n = (BoundDoWhileStatement)statement;
                        w.Open("dowhile");
                        WriteStatement(w, registry, labels, n.Body);
                        WriteExpression(w, registry, labels, n.Condition);
                        w.Field(Str(n.BreakLabel.Name));
                        w.Field(Str(n.ContinueLabel.Name));
                        w.End();
                        break;
                    }
                case BoundNodeKind.ForStatement:
                    {
                        var n = (BoundForStatement)statement;
                        w.Open("for");
                        w.Field(registry.Get(n.Variable));
                        WriteExpression(w, registry, labels, n.LowerBound);
                        WriteExpression(w, registry, labels, n.UpperBound);
                        WriteNullableExpression(w, registry, labels, n.Step);
                        WriteStatement(w, registry, labels, n.Body);
                        w.Field(Str(n.BreakLabel.Name));
                        w.Field(Str(n.ContinueLabel.Name));
                        w.End();
                        break;
                    }
                case BoundNodeKind.LabelStatement:
                    {
                        var n = (BoundLabelStatement)statement;
                        w.Open("label");
                        w.Field(Str(n.Label.Name));
                        w.End();
                        break;
                    }
                case BoundNodeKind.GotoStatement:
                    {
                        var n = (BoundGotoStatement)statement;
                        w.Open("goto");
                        w.Field(Str(n.Label.Name));
                        w.End();
                        break;
                    }
                case BoundNodeKind.ConditionalGotoStatement:
                    {
                        var n = (BoundConditionalGotoStatement)statement;
                        w.Open("cgoto");
                        w.Field(Str(n.Label.Name));
                        WriteExpression(w, registry, labels, n.Condition);
                        w.Field(n.JumpIfTrue ? 1 : 0);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ReturnStatement:
                    {
                        var n = (BoundReturnStatement)statement;
                        w.Open("return");
                        WriteNullableExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ExpressionStatement:
                    {
                        var n = (BoundExpressionStatement)statement;
                        w.Open("exprstmt");
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.SequencePointStatement:
                    // 调试信息降级：仅序列化内层语句
                    WriteStatement(w, registry, labels, ((BoundSequencePointStatement)statement).Statement);
                    break;
            }
        }

        private static void WriteNullableStatement(Writer w, Registry registry, Dictionary<string, BoundLabel> labels, BoundStatement? statement)
        {
            if (statement == null)
            {
                w.Field("-");
                return;
            }

            WriteStatement(w, registry, labels, statement);
        }

        private static void WriteNullableExpression(Writer w, Registry registry, Dictionary<string, BoundLabel> labels, BoundExpression? expression)
        {
            if (expression == null)
            {
                w.Field("-");
                return;
            }

            WriteExpression(w, registry, labels, expression);
        }

        // ---------------------------------------------------------------- write: expressions

        private static void WriteExpression(Writer w, Registry registry, Dictionary<string, BoundLabel> labels, BoundExpression expression)
        {
            switch (expression.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    {
                        var n = (BoundLiteralExpression)expression;
                        w.Open("lit");
                        w.Field(registry.Get(n.Type));
                        w.Field(EncodeValue(n.Value));
                        w.End();
                        break;
                    }
                case BoundNodeKind.VariableExpression:
                    {
                        var n = (BoundVariableExpression)expression;
                        w.Open("var");
                        w.Field(registry.Get(n.Variable));
                        w.End();
                        break;
                    }
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)expression;
                        w.Open("assign");
                        w.Field(registry.Get(n.Variable));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)expression;
                        w.Open("cassign");
                        w.Field(registry.Get(n.Variable));
                        WriteBinaryOperator(w, registry, n.Op);
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.UnaryExpression:
                    {
                        var n = (BoundUnaryExpression)expression;
                        w.Open("unary");
                        WriteUnaryOperator(w, registry, n.Op);
                        WriteExpression(w, registry, labels, n.Operand);
                        w.End();
                        break;
                    }
                case BoundNodeKind.BinaryExpression:
                    {
                        var n = (BoundBinaryExpression)expression;
                        w.Open("binary");
                        WriteBinaryOperator(w, registry, n.Op);
                        WriteExpression(w, registry, labels, n.Left);
                        WriteExpression(w, registry, labels, n.Right);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ConditionalExpression:
                    {
                        var n = (BoundConditionalExpression)expression;
                        w.Open("cond");
                        WriteExpression(w, registry, labels, n.Condition);
                        WriteExpression(w, registry, labels, n.WhenTrue);
                        WriteExpression(w, registry, labels, n.WhenFalse);
                        w.End();
                        break;
                    }
                case BoundNodeKind.CallExpression:
                    {
                        var n = (BoundCallExpression)expression;
                        w.Open("call");
                        w.Field(registry.Get(n.Function));
                        w.Field(n.Arguments.Length);
                        foreach (var a in n.Arguments)
                        {
                            WriteExpression(w, registry, labels, a);
                        }
                        w.End();
                        break;
                    }
                case BoundNodeKind.ConversionExpression:
                    {
                        var n = (BoundConversionExpression)expression;
                        w.Open("conv");
                        w.Field(registry.Get(n.Type));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.IsExpression:
                    {
                        var n = (BoundIsExpression)expression;
                        w.Open("istype");
                        w.Field(registry.Get(n.TargetType));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.AsExpression:
                    {
                        var n = (BoundAsExpression)expression;
                        w.Open("astype");
                        w.Field(registry.Get(n.TargetType));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)expression;
                        w.Open("arrnew");
                        w.Field(registry.Get(n.Type));
                        WriteExpression(w, registry, labels, n.Length);
                        w.Field(n.Initializers.Length);
                        foreach (var i in n.Initializers)
                        {
                            WriteExpression(w, registry, labels, i);
                        }
                        w.End();
                        break;
                    }
                case BoundNodeKind.ElementAccessExpression:
                    {
                        var n = (BoundElementAccessExpression)expression;
                        w.Open("elem");
                        w.Field(registry.Get(n.Type));
                        WriteExpression(w, registry, labels, n.Target);
                        WriteExpression(w, registry, labels, n.Index);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)expression;
                        w.Open("elemassign");
                        w.Field(registry.Get(n.Type));
                        WriteExpression(w, registry, labels, n.Target);
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.MemberAccessExpression:
                    {
                        // 仅数组/字符串 `.Length`（Field == null）；类字段访问 OOP，v1 拒绝
                        var n = (BoundMemberAccessExpression)expression;
                        w.Open("memberacc");
                        w.Field(registry.Get(n.Type));
                        w.Field(Str(n.Identifier));
                        WriteExpression(w, registry, labels, n.Target);
                        w.End();
                        break;
                    }
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)expression;
                        w.Open("membercall");
                        w.Field(registry.Get(n.Type));
                        w.Field(Str(n.Identifier));
                        w.Field(n.Method != null ? registry.Get(n.Method) : -1);
                        w.Field(n.Arguments.Length);
                        WriteExpression(w, registry, labels, n.Expression);
                        foreach (var a in n.Arguments)
                        {
                            WriteExpression(w, registry, labels, a);
                        }
                        w.End();
                        break;
                    }
                case BoundNodeKind.StaticTypeExpression:
                    {
                        var n = (BoundStaticTypeExpression)expression;
                        w.Open("statictype");
                        w.Field(registry.Get(n.Type));
                        w.End();
                        break;
                    }
                case BoundNodeKind.ThisExpression:
                    {
                        var n = (BoundThisExpression)expression;
                        w.Open("this");
                        w.Field(registry.Get(n.Type));
                        w.End();
                        break;
                    }
            }
        }

        private static void WriteUnaryOperator(Writer w, Registry registry, BoundUnaryOperator op)
        {
            w.Open("uop");
            w.Field((int)op.SyntaxKind);
            w.Field(registry.Get(op.OperandType));
            w.End();
        }

        private static void WriteBinaryOperator(Writer w, Registry registry, BoundBinaryOperator op)
        {
            w.Open("bop");
            w.Field((int)op.SyntaxKind);
            w.Field(registry.Get(op.LeftType));
            w.Field(registry.Get(op.RightType));
            w.End();
        }

        // ---------------------------------------------------------------- write: symbols

        private static void EmitTypeSymbol(Writer w, Registry registry, TypeSymbol type)
        {
            if (type is EnumTypeSymbol e)
            {
                w.Open("enum");
                w.Field(registry.Get(e));
                w.Field(Str(e.Namespace));
                w.Field(Str(e.Name));
                var members = e.MemberNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                w.Field(members.Length);
                foreach (var name in members)
                {
                    e.TryGetMember(name, out var value);
                    w.Open("m");
                    w.Field(Str(name));
                    w.Field(value);
                    w.End();
                }
                w.End();
            }
            else if (type.ElementType != null)
            {
                w.Open("arr");
                w.Field(registry.Get(type.ElementType));
                w.End();
            }
            else
            {
                w.Open("type");
                w.Field(Str(type.Name));
                w.End();
            }
        }

        /// <summary>6e-M19 M2-c：内建单例（System.Object/System.Type）按全名序列化，读侧映射回单例。</summary>
        private static void EmitBuiltinSystemClass(Writer w, Registry registry, ClassTypeSymbol classType)
        {
            w.Open("systype");
            w.Field(registry.Get(classType));
            w.Field(Str(classType.FullName));
            w.End();
        }

        private static void EmitClassSymbol(Writer w, Registry registry, ClassTypeSymbol classType)
        {
            w.Open("cls");
            w.Field(registry.Get(classType));
            w.Field(Str(classType.Namespace));
            w.Field(Str(classType.Name));
            w.Field((int)classType.Visibility);
            // 序列化全部静态方法（6e-M18：容器类允许带体静态方法，如 Console.WriteLine/Math.Max；syscall/extern 亦为静态）
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field(methods.Length);
            foreach (var method in methods)
            {
                w.Field(registry.Get(method));
            }
            w.End();
        }

        private static void EmitFunctionSymbol(Writer w, Registry registry, FunctionSymbol fn)
        {
            w.Open("fn");
            w.Field(registry.Get(fn));
            w.Field(Str(fn.Name));
            w.Field(registry.Get(fn.ReturnType));
            w.Field(fn.IsExtern ? 1 : 0);
            w.Field(fn.DllName != null ? Str(fn.DllName) : "-");
            w.Field((int)fn.CallingConvention);
            w.Field(fn.Namespace.Length > 0 ? Str(fn.Namespace) : "-");
            w.Field(fn.ContainingClass != null ? registry.Get(fn.ContainingClass) : -1);
            w.Field(fn.BuiltinKind != null ? Str(fn.BuiltinKind.Value.ToString()) : "-");
            w.Field(fn.EntryPoint != null ? Str(fn.EntryPoint) : "-");
            w.Field(fn.CharSet != null ? (int)fn.CharSet.Value : -1);
            w.Field(fn.Parameters.Length);
            foreach (var p in fn.Parameters)
            {
                w.Open("par");
                w.Field(registry.Get(p));
                w.Field(Str(p.Name));
                w.Field(registry.Get(p.Type));
                w.Field(p.Ordinal);
                w.End();
            }
            w.End();
        }

        private static void EmitVariableSymbol(Writer w, Registry registry, VariableSymbol v)
        {
            if (v is GlobalVariableSymbol)
            {
                w.Open("glb");
            }
            else
            {
                w.Open("loc");
            }

            w.Field(registry.Get(v));
            w.Field(Str(v.Name));
            w.Field(v.IsReadOnly ? 1 : 0);
            w.Field(registry.Get(v.Type));
            if (v.Constant != null)
            {
                w.Open("const");
                w.Field(registry.Get(v.Type));
                w.Field(EncodeValue(v.Constant.Value));
                w.End();
            }
            else
            {
                w.Field("-");
            }

            w.End();
        }

        // ---------------------------------------------------------------- write: value encoding

        private static string EncodeValue(object value)
        {
            switch (value)
            {
                case null: return "n:"; // 6e-M19 M5-a：null 常量
                case int i: return "i:" + i.ToString(CultureInfo.InvariantCulture);
                case bool b: return "b:" + (b ? 1 : 0);
                case char c: return "c:" + ((int)c).ToString(CultureInfo.InvariantCulture);
                case byte u: return "u:" + u.ToString(CultureInfo.InvariantCulture);
                case double d: return "d:" + d.ToString("R", CultureInfo.InvariantCulture);
                case string s: return "s:" + Escape(s);
                default:
                    throw new NotSupportedException($"Unsupported constant value type '{value.GetType()}'");
            }
        }

        private static object DecodeValue(string token)
        {
            var kind = token[0];
            var rest = token.Substring(2);
            switch (kind)
            {
                case 'n': return null!; // 6e-M19 M5-a：null 常量
                case 'i': return int.Parse(rest, CultureInfo.InvariantCulture);
                case 'b': return rest == "1";
                case 'c': return (char)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'u': return (byte)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'd': return double.Parse(rest, NumberStyles.Float, CultureInfo.InvariantCulture);
                case 's': return Unescape(rest);
                default:
                    throw new InvalidDataException($"Unknown constant encoding '{token}'");
            }
        }

        // ---------------------------------------------------------------- write: string escaping

        private static string Escape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case ' ': sb.Append("\\s"); break;
                    case '(': sb.Append("\\("); break;
                    case ')': sb.Append("\\)"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (char.IsControl(c))
                        {
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }

        private static string Unescape(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 >= text.Length)
                {
                    sb.Append('\\');
                    break;
                }

                var e = text[++i];
                switch (e)
                {
                    case '\\': sb.Append('\\'); break;
                    case 's': sb.Append(' '); break;
                    case '(': sb.Append('('); break;
                    case ')': sb.Append(')'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (i + 4 < text.Length)
                        {
                            var hex = text.Substring(i + 1, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                        }
                        else
                        {
                            sb.Append('u');
                        }
                        break;
                    default:
                        sb.Append(e);
                        break;
                }
            }

            return sb.ToString();
        }

        private static string Str(string text) => Escape(text);

        // ---------------------------------------------------------------- write: helpers

        private sealed class Writer
        {
            private readonly TextWriter _w;
            private int _depth;

            public Writer(TextWriter writer)
            {
                _w = writer;
            }

            public void Open(string kind)
            {
                Indent();
                _w.Write('(');
                _w.Write(kind);
                _depth++;
            }

            public void Field(object value)
            {
                _w.Write(' ');
                _w.Write(value);
            }

            public void End()
            {
                _depth--;
                _w.WriteLine(')');
            }

            private void Indent()
            {
                if (_depth > 0)
                {
                    _w.WriteLine();
                    _w.Write(new string(' ', _depth * 2));
                }
            }
        }

        /// <summary>写侧符号注册表：id 分配 + 发射器（id 顺序 = 分配顺序）。</summary>
        private sealed class Registry
        {
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);

            public List<Action<Writer, Registry>> Emitters { get; } = new();

            public int Get(object symbol) => _ids[symbol];

            public int RegisterType(TypeSymbol type)
            {
                if (_ids.TryGetValue(type, out var id))
                {
                    return id;
                }

                // 类类型：注册为独立符号（cls）——id 先于引用它的函数
                if (type is ClassTypeSymbol classType)
                {
                    return RegisterClass(classType);
                }

                // 数组元素类型先注册（元素 id < 数组 id），保证读侧按 id 序可解析
                if (type.ElementType != null)
                {
                    RegisterType(type.ElementType);
                }

                id = _ids.Count;
                _ids[type] = id;
                Emitters.Add((w, r) => EmitTypeSymbol(w, r, type));
                return id;
            }

            public int RegisterClass(ClassTypeSymbol classType)
            {
                if (_ids.TryGetValue(classType, out var id))
                {
                    return id;
                }

                // 6e-M19 M2-c：内建单例（System.Object/System.Type）不发 cls——读侧会造出新类破坏单例同一性；
                // 发 systype 按全名映射回单例（成员面由 Ensure 内建注入，不序列化）
                if (SystemObjectMembers.IsBuiltinSystemClass(classType))
                {
                    id = _ids.Count;
                    _ids[classType] = id;
                    Emitters.Add((w, r) => EmitBuiltinSystemClass(w, r, classType));
                    return id;
                }

                // 纯容器类（仅 syscall/extern 静态方法）：方法符号已在 Functions 注册，这里只发壳
                id = _ids.Count;
                _ids[classType] = id;
                Emitters.Add((w, r) => EmitClassSymbol(w, r, classType));
                return id;
            }

            public int RegisterFunction(FunctionSymbol fn)
            {
                if (_ids.TryGetValue(fn, out var id))
                {
                    return id;
                }

                // 类方法：容器类全静态（syscall/extern 及带体静态方法，6e-M18）作为独立 fn 序列化；实例方法/构造由类壳过滤。
                // 例外：Object 内建方法（M2-c）带 BuiltinKind，读侧经单例复用重建，须随引用序列化
                if (fn.ContainingClass != null && !fn.IsStatic && !SystemObjectMembers.IsBuiltinSystemClass(fn.ContainingClass))
                {
                    return -1;
                }

                // 先注册返回类型/参数类型（id 序在 fn 之前），保证读侧按 id 序可解析
                RegisterType(fn.ReturnType);
                foreach (var p in fn.Parameters)
                {
                    RegisterType(p.Type);
                }

                id = _ids.Count;
                _ids[fn] = id;
                foreach (var p in fn.Parameters)
                {
                    _ids[p] = _ids.Count;
                }

                Emitters.Add((w, r) => EmitFunctionSymbol(w, r, fn));
                return id;
            }

            public int RegisterVariable(VariableSymbol v)
            {
                if (_ids.TryGetValue(v, out var id))
                {
                    return id;
                }

                // 先注册类型（id 序在变量之前）
                RegisterType(v.Type);

                id = _ids.Count;
                _ids[v] = id;

                Emitters.Add((w, r) => EmitVariableSymbol(w, r, v));
                return id;
            }
        }

        // ---------------------------------------------------------------- read

        /// <summary>从 `.cod` 文件加载程序集。</summary>
        public static CodProgram Load(string path)
        {
            return Read(File.ReadAllText(path));
        }

        public static CodProgram Read(string text)
        {
            var tokens = Tokenize(text).ToArray();
            var reader = new Reader(tokens);
            reader.Expect("cod");

            var magic = reader.ExpectString();
            if (magic != Magic)
            {
                throw new InvalidDataException($"invalid .cod magic '{magic}'");
            }

            var version = reader.ExpectInt();
            if (version != Version)
            {
                throw new InvalidDataException($".cod version {version} is not supported (expected {Version}); rebuild the library");
            }

            var symbolsById = new List<object>();
            var bodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var requires = CodRequirement.Any;
            var platforms = ImmutableArray.CreateBuilder<string>();
            var dotnetRefs = ImmutableArray.CreateBuilder<string>();
            var codRefs = ImmutableArray.CreateBuilder<string>();
            var imports = ImmutableArray.CreateBuilder<string>();
            var namespaces = ImmutableArray.CreateBuilder<string>();

            while (reader.TryExpect(out var child))
            {
                switch (child)
                {
                    case "symbols":
                        ReadSymbols(reader, symbolsById);
                        break;
                    case "bodies":
                        ReadBodies(reader, symbolsById, bodies);
                        break;
                    case "manifest":
                        while (reader.TryExpect(out var item))
                        {
                            switch (item)
                            {
                                case "requires":
                                    requires = ParseRequirement(reader.ExpectString());
                                    break;
                                case "platform":
                                    platforms.Add(reader.ExpectString());
                                    break;
                                case "refdll":
                                    dotnetRefs.Add(reader.ExpectString());
                                    break;
                                case "refcod":
                                    codRefs.Add(reader.ExpectString());
                                    break;
                                case "import":
                                    imports.Add(reader.ExpectString());
                                    break;
                                case "ns":
                                    namespaces.Add(reader.ExpectString());
                                    break;
                            }

                            reader.End();
                        }

                        reader.End();
                        break;
                }
            }

            var functions = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (var symbol in symbolsById)
            {
                if (symbol is FunctionSymbol fn)
                {
                    functions.Add(fn);
                }
            }

            var globals = ImmutableArray.CreateBuilder<GlobalVariableSymbol>();
            foreach (var symbol in symbolsById)
            {
                if (symbol is GlobalVariableSymbol g)
                {
                    globals.Add(g);
                }
            }

            var enums = ImmutableArray.CreateBuilder<EnumTypeSymbol>();
            foreach (var symbol in symbolsById)
            {
                if (symbol is EnumTypeSymbol e)
                {
                    enums.Add(e);
                }
            }

            var classes = ImmutableArray.CreateBuilder<ClassTypeSymbol>();
            foreach (var symbol in symbolsById)
            {
                if (symbol is ClassTypeSymbol c)
                {
                    classes.Add(c);
                }
            }

            return new CodProgram(
                functions.ToImmutable(),
                globals.ToImmutable(),
                enums.ToImmutable(),
                classes.ToImmutable(),
                bodies.ToImmutable(),
                requires,
                platforms.ToImmutable(),
                dotnetRefs.ToImmutable(),
                imports.ToImmutable(),
                codRefs.ToImmutable(),
                namespaces.ToImmutable());
        }

        private static void ReadSymbols(Reader reader, List<object> symbolsById)
        {
            while (reader.TryExpect(out var kind))
            {
                switch (kind)
                {
                    case "type":
                        {
                            var name = reader.ExpectString();
                            symbolsById.Add(ResolveBuiltinType(name));
                            reader.End();
                            break;
                        }
                    case "systype":
                        {
                            // 6e-M19 M2-c：内建单例按全名映射（成员面已由 Ensure 内建注入）
                            var id = reader.ExpectInt();
                            var fullName = reader.ExpectString();
                            var singleton = fullName switch
                            {
                                "System.Object" => (object)ClassTypeSymbol.SystemObject,
                                "System.Type" => ClassTypeSymbol.SystemType,
                                _ => throw new InvalidDataException($"Unknown builtin system class '{fullName}'"),
                            };
                            SetAt(symbolsById, id, singleton);
                            reader.End();
                            break;
                        }
                    case "arr":
                        {
                            var elementId = reader.ExpectInt();
                            // 元素类型 id 已先注册（数组 id > 元素 id），直接构建
                            var elementType = (TypeSymbol)symbolsById[elementId];
                            symbolsById.Add(TypeSymbol.ArrayOf(elementType));
                            reader.End();
                            break;
                        }
                    case "enum":
                        {
                            var id = reader.ExpectInt();
                            var ns = reader.ExpectString();
                            var name = reader.ExpectString();
                            var count = reader.ExpectInt();
                            var members = new Dictionary<string, int>();
                            for (var i = 0; i < count; i++)
                            {
                                reader.Expect("m");
                                var memberName = reader.ExpectString();
                                var value = reader.ExpectInt();
                                members[memberName] = value;
                                reader.End();
                            }

                            var enumType = new EnumTypeSymbol(name, members, ns);
                            SetAt(symbolsById, id, enumType);
                            reader.End();
                            break;
                        }
                    case "fn":
                        ReadFunction(reader, symbolsById);
                        break;
                    case "cls":
                        ReadClass(reader, symbolsById);
                        break;
                    case "glb":
                        ReadVariable(reader, symbolsById, isGlobal: true);
                        break;
                    case "loc":
                        ReadVariable(reader, symbolsById, isGlobal: false);
                        break;
                }
            }

            reader.End();
        }

        private static void ReadClass(Reader reader, List<object> symbolsById)
        {
            var id = reader.ExpectInt();
            var ns = reader.ExpectString();
            var name = reader.ExpectString();
            var visibility = (Visibility)reader.ExpectInt();
            var methodCount = reader.ExpectInt();
            // 方法函数符号按 id 序在 cls 之后读，这里只消费 id（方法回填由 ReadFunction 的 containingClassId 完成）
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectInt();
            }

            var classType = new ClassTypeSymbol(name, ns, visibility, declaration: null);
            // 6e-M19 M2-c：.cod 类默认继承 System.Object（与源码绑定一致；.cod v1 不序列化接口声明）
            classType.BaseType = ClassTypeSymbol.SystemObject;
            SetAt(symbolsById, id, classType);
            reader.End();
        }

        private static void ReadFunction(Reader reader, List<object> symbolsById)
        {
            var id = reader.ExpectInt();
            var name = reader.ExpectString();
            var returnTypeId = reader.ExpectInt();
            var isExtern = reader.ExpectInt() == 1;
            var dllToken = reader.ExpectString();
            var dllName = dllToken == "-" ? null : dllToken;
            var cc = (CallingConvention)reader.ExpectInt();
            var nsToken = reader.ExpectString();
            var ns = nsToken == "-" ? "" : nsToken;
            var containingClassId = reader.ExpectInt();
            var builtinKindToken = reader.ExpectString();
            var builtinKind = builtinKindToken == "-" ? (BuiltinKind?)null : BuiltinFunctions.GetByKindName(builtinKindToken) ?? SystemObjectMembers.GetByKindName(builtinKindToken);
            var entryPointToken = reader.ExpectString();
            var entryPoint = entryPointToken == "-" ? null : entryPointToken;
            var charSetValue = reader.ExpectInt();
            var charSet = charSetValue >= 0 ? (CharSet)charSetValue : (CharSet?)null;
            var paramCount = reader.ExpectInt();
            var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
            for (var i = 0; i < paramCount; i++)
            {
                reader.Expect("par");
                var pId = reader.ExpectInt();
                var pName = reader.ExpectString();
                var pTypeId = reader.ExpectInt();
                var ordinal = reader.ExpectInt();
                var parameter = new ParameterSymbol(pName, (TypeSymbol)symbolsById[pTypeId], ordinal);
                parameters.Add(parameter);
                SetAt(symbolsById, pId, parameter);
                reader.End();
            }

            var returnType = (TypeSymbol)symbolsById[returnTypeId];
            var containingClass = containingClassId >= 0 ? (ClassTypeSymbol)symbolsById[containingClassId] : null;

            // 6e-M19 M2-c：Object 内建方法复用单例（保持符号同一性，发射器按 BuiltinKind 分发）
            if (containingClass != null && builtinKind != null && SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                var singleton = SystemObjectMembers.GetByKind(builtinKind.Value);
                if (singleton != null)
                {
                    SetAt(symbolsById, id, singleton);
                    reader.End();
                    return;
                }
            }

            // 含类归属或内置种类：不复用全局单例（内置单例无类归属），重建带上下文符号
            FunctionSymbol function;
            if (containingClass != null || builtinKind != null)
            {
                function = new FunctionSymbol(
                    name,
                    parameters.ToImmutable(),
                    returnType,
                    isExtern: isExtern,
                    dllName: dllName,
                    callingConvention: cc,
                    containingClass: containingClass,
                    builtinKind: builtinKind,
                    @namespace: ns,
                    entryPoint: entryPoint,
                    charSet: charSet);
            }
            else
            {
                function = BuiltinFunctions.GetByName(name) ?? new FunctionSymbol(
                    name,
                    parameters.ToImmutable(),
                    returnType,
                    isExtern: isExtern,
                    dllName: dllName,
                    callingConvention: cc,
                    @namespace: ns,
                    entryPoint: entryPoint,
                    charSet: charSet);
            }

            SetAt(symbolsById, id, function);

            // 类方法回填：含类归属的 fn 归入其类（6e-M18：容器类全静态——syscall/extern 及带体静态方法）。
            // 内建单例（System.Object/System.Type，M2-c）成员已由 Ensure 注入，跳过回填防重复/防误标 static
            if (containingClass != null && !SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                function.IsStatic = true;
                containingClass.AddMethod(function);
            }

            reader.End();
        }

        private static void ReadVariable(Reader reader, List<object> symbolsById, bool isGlobal)
        {
            var id = reader.ExpectInt();
            var name = reader.ExpectString();
            var isReadOnly = reader.ExpectInt() == 1;
            var typeId = reader.ExpectInt();
            var type = (TypeSymbol)symbolsById[typeId];
            BoundConstant? constant = null;

            if (reader.TryExpect(out var constToken) && constToken == "const")
            {
                var constTypeId = reader.ExpectInt();
                var encoded = reader.ExpectString();
                var value = DecodeValue(encoded);
                var constType = (TypeSymbol)symbolsById[constTypeId];
                constant = new BoundConstant(value);
                reader.End();
            }

            VariableSymbol variable = isGlobal
                ? new GlobalVariableSymbol(name, isReadOnly, type, constant)
                : new LocalVariableSymbol(name, isReadOnly, type, constant);

            SetAt(symbolsById, id, variable);
            reader.End();
        }

        private static void ReadBodies(Reader reader, List<object> symbolsById, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder bodies)
        {
            while (reader.TryExpect(out var kind) && kind == "body")
            {
                var fnId = reader.ExpectInt();
                var function = (FunctionSymbol)symbolsById[fnId];
                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                var body = (BoundBlockStatement)ReadStatement(reader, symbolsById, labels);

                // extern 函数无实现：空 body（与 Binder.BindProgram 一致）
                if (function.IsExtern)
                {
                    body = new BoundBlockStatement(null, ImmutableArray<BoundStatement>.Empty);
                }

                bodies[function] = body;
                reader.End();
            }

            reader.End();
        }

        private static BoundStatement ReadStatement(Reader reader, List<object> symbolsById, Dictionary<string, BoundLabel> labels)
        {
            var kind = reader.ExpectKind();
            var statement = ReadStatementFromToken(reader, kind, symbolsById, labels);
            reader.End();
            return statement;
        }

        private static BoundStatement ReadStatementFromToken(Reader reader, string kind, List<object> symbolsById, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "block":
                    {
                        var count = reader.ExpectInt();
                        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                        for (var i = 0; i < count; i++)
                        {
                            statements.Add(ReadStatement(reader, symbolsById, labels));
                        }

                        return new BoundBlockStatement(null, statements.ToImmutable());
                    }
                case "nop":
                    return new BoundNopStatement(null);
                case "vardecl":
                    {
                        var variableId = reader.ExpectInt();
                        var variable = (VariableSymbol)symbolsById[variableId];
                        var initializer = ReadExpression(reader, symbolsById, labels);
                        return new BoundVariableDeclaration(null, variable, initializer);
                    }
                case "if":
                    {
                        var condition = ReadExpression(reader, symbolsById, labels);
                        var then = ReadStatement(reader, symbolsById, labels);
                        var elseStatement = ReadNullableStatement(reader, symbolsById, labels);
                        return new BoundIfStatement(null, condition, then, elseStatement);
                    }
                case "while":
                    {
                        var condition = ReadExpression(reader, symbolsById, labels);
                        var body = ReadStatement(reader, symbolsById, labels);
                        var breakLabel = GetLabel(labels, reader.ExpectString());
                        var continueLabel = GetLabel(labels, reader.ExpectString());
                        return new BoundWhileStatement(null, condition, body, breakLabel, continueLabel);
                    }
                case "dowhile":
                    {
                        var body = ReadStatement(reader, symbolsById, labels);
                        var condition = ReadExpression(reader, symbolsById, labels);
                        var breakLabel = GetLabel(labels, reader.ExpectString());
                        var continueLabel = GetLabel(labels, reader.ExpectString());
                        return new BoundDoWhileStatement(null, body, condition, breakLabel, continueLabel);
                    }
                case "for":
                    {
                        var variableId = reader.ExpectInt();
                        var variable = (VariableSymbol)symbolsById[variableId];
                        var lowerBound = ReadExpression(reader, symbolsById, labels);
                        var upperBound = ReadExpression(reader, symbolsById, labels);
                        var step = ReadNullableExpression(reader, symbolsById, labels);
                        var body = ReadStatement(reader, symbolsById, labels);
                        var breakLabel = GetLabel(labels, reader.ExpectString());
                        var continueLabel = GetLabel(labels, reader.ExpectString());
                        return new BoundForStatement(null, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
                    }
                case "label":
                    return new BoundLabelStatement(null, GetLabel(labels, reader.ExpectString()));
                case "goto":
                    return new BoundGotoStatement(null, GetLabel(labels, reader.ExpectString()));
                case "cgoto":
                    {
                        var label = GetLabel(labels, reader.ExpectString());
                        var condition = ReadExpression(reader, symbolsById, labels);
                        var jumpIfTrue = reader.ExpectInt() == 1;
                        return new BoundConditionalGotoStatement(null, label, condition, jumpIfTrue);
                    }
                case "return":
                    {
                        var expression = ReadNullableExpression(reader, symbolsById, labels);
                        return new BoundReturnStatement(null, expression);
                    }
                case "exprstmt":
                    {
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundExpressionStatement(null, expression);
                    }
                default:
                    throw new InvalidDataException($"Unknown statement kind '{kind}'");
            }
        }

        private static BoundStatement? ReadNullableStatement(Reader reader, List<object> symbolsById, Dictionary<string, BoundLabel> labels)
        {
            if (reader.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var statement = ReadStatementFromToken(reader, token, symbolsById, labels);
            reader.End();
            return statement;
        }

        private static BoundExpression? ReadNullableExpression(Reader reader, List<object> symbolsById, Dictionary<string, BoundLabel> labels)
        {
            if (reader.TryExpect(out var token) && token == "-")
            {
                return null;
            }

            var expression = ReadExpressionFromToken(reader, token, symbolsById, labels);
            reader.End();
            return expression;
        }

        private static BoundExpression ReadExpression(Reader reader, List<object> symbolsById, Dictionary<string, BoundLabel> labels)
        {
            var token = reader.ExpectKind();
            var expression = ReadExpressionFromToken(reader, token, symbolsById, labels);
            reader.End();
            return expression;
        }

        private static BoundExpression ReadExpressionFromToken(Reader reader, string kind, List<object> symbolsById, Dictionary<string, BoundLabel> labels)
        {
            switch (kind)
            {
                case "lit":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var encoded = reader.ExpectString();
                        var value = DecodeValue(encoded);
                        return new BoundLiteralExpression(null, value, type);
                    }
                case "var":
                    {
                        var id = reader.ExpectInt();
                        var variable = (VariableSymbol)symbolsById[id];
                        return new BoundVariableExpression(null, variable);
                    }
                case "assign":
                    {
                        var id = reader.ExpectInt();
                        var variable = (VariableSymbol)symbolsById[id];
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundAssignmentExpression(null, variable, expression);
                    }
                case "cassign":
                    {
                        var id = reader.ExpectInt();
                        var variable = (VariableSymbol)symbolsById[id];
                        var op = ReadBinaryOperator(reader, symbolsById);
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundCompoundAssignmentExpression(null, variable, op, expression);
                    }
                case "unary":
                    {
                        var op = ReadUnaryOperator(reader, symbolsById);
                        var operand = ReadExpression(reader, symbolsById, labels);
                        return new BoundUnaryExpression(null, op, operand);
                    }
                case "binary":
                    {
                        var op = ReadBinaryOperator(reader, symbolsById);
                        var left = ReadExpression(reader, symbolsById, labels);
                        var right = ReadExpression(reader, symbolsById, labels);
                        return new BoundBinaryExpression(null, left, op, right);
                    }
                case "cond":
                    {
                        var condition = ReadExpression(reader, symbolsById, labels);
                        var whenTrue = ReadExpression(reader, symbolsById, labels);
                        var whenFalse = ReadExpression(reader, symbolsById, labels);
                        return new BoundConditionalExpression(null, condition, whenTrue, whenFalse);
                    }
                case "call":
                    {
                        var fnId = reader.ExpectInt();
                        var function = (FunctionSymbol)symbolsById[fnId];
                        var count = reader.ExpectInt();
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(reader, symbolsById, labels));
                        }

                        return new BoundCallExpression(null, function, arguments.ToImmutable());
                    }
                case "conv":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundConversionExpression(null, type, expression);
                    }
                case "arrnew":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var length = ReadExpression(reader, symbolsById, labels);
                        var count = reader.ExpectInt();
                        var initializers = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            initializers.Add(ReadExpression(reader, symbolsById, labels));
                        }

                        return new BoundArrayCreationExpression(null, type, length, initializers.ToImmutable());
                    }
                case "elem":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var target = ReadExpression(reader, symbolsById, labels);
                        var index = ReadExpression(reader, symbolsById, labels);
                        return new BoundElementAccessExpression(null, type, target, index);
                    }
                case "elemassign":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var target = (BoundElementAccessExpression)ReadExpression(reader, symbolsById, labels);
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundElementAssignmentExpression(null, type, target, expression);
                    }
                case "memberacc":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var identifier = reader.ExpectString();
                        var target = ReadExpression(reader, symbolsById, labels);
                        return new BoundMemberAccessExpression(null, type, target, identifier);
                    }
                case "membercall":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        var identifier = reader.ExpectString();
                        var methodId = reader.ExpectInt();
                        var method = methodId >= 0 ? (FunctionSymbol)symbolsById[methodId] : null;
                        var count = reader.ExpectInt();
                        var target = ReadExpression(reader, symbolsById, labels);
                        var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                        for (var i = 0; i < count; i++)
                        {
                            arguments.Add(ReadExpression(reader, symbolsById, labels));
                        }

                        return new BoundMemberCallExpression(null, target, identifier, arguments.ToImmutable(), type, method);
                    }
                case "statictype":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (ClassTypeSymbol)symbolsById[typeId];
                        return new BoundStaticTypeExpression(null, type);
                    }
                case "this":
                    {
                        var typeId = reader.ExpectInt();
                        var type = (TypeSymbol)symbolsById[typeId];
                        return new BoundThisExpression(null, (ClassTypeSymbol)type);
                    }
                case "istype":
                    {
                        var typeId = reader.ExpectInt();
                        var targetType = (TypeSymbol)symbolsById[typeId];
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundIsExpression(null, expression, targetType);
                    }
                case "astype":
                    {
                        var typeId = reader.ExpectInt();
                        var targetType = (TypeSymbol)symbolsById[typeId];
                        var expression = ReadExpression(reader, symbolsById, labels);
                        return new BoundAsExpression(null, expression, targetType);
                    }
                default:
                    throw new InvalidDataException($"Unknown expression kind '{kind}'");
            }
        }

        private static BoundUnaryOperator ReadUnaryOperator(Reader reader, List<object> symbolsById)
        {
            reader.Expect("uop");
            var syntaxKind = (SyntaxKind)reader.ExpectInt();
            var operandTypeId = reader.ExpectInt();
            var operandType = (TypeSymbol)symbolsById[operandTypeId];
            var op = BoundUnaryOperator.Bind(syntaxKind, operandType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind unary operator {syntaxKind} on {operandType}");
        }

        private static BoundBinaryOperator ReadBinaryOperator(Reader reader, List<object> symbolsById)
        {
            reader.Expect("bop");
            var syntaxKind = (SyntaxKind)reader.ExpectInt();
            var leftTypeId = reader.ExpectInt();
            var rightTypeId = reader.ExpectInt();
            var leftType = (TypeSymbol)symbolsById[leftTypeId];
            var rightType = (TypeSymbol)symbolsById[rightTypeId];
            var op = BoundBinaryOperator.Bind(syntaxKind, leftType, rightType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind binary operator {syntaxKind} on {leftType} and {rightType}");
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

        private static TypeSymbol ResolveBuiltinType(string name)
        {
            return name switch
            {
                "any" => TypeSymbol.Any,
                "null" => TypeSymbol.Null, // 6e-M19 M5-a
                "bool" => TypeSymbol.Boolean,
                "byte" => TypeSymbol.UInt8,
                "sbyte" => TypeSymbol.Int8,
                "short" => TypeSymbol.Int16,
                "ushort" => TypeSymbol.UInt16,
                "int" => TypeSymbol.Int32,
                "uint" => TypeSymbol.UInt32,
                "long" => TypeSymbol.Int64,
                "ulong" => TypeSymbol.UInt64,
                "float" => TypeSymbol.Float,
                "double" => TypeSymbol.Double,
                "char" => TypeSymbol.Char,
                "string" => TypeSymbol.String,
                "void" => TypeSymbol.Void,
                "i128" => TypeSymbol.Int128,
                "u128" => TypeSymbol.UInt128,
                "f128" => TypeSymbol.Float128,
                "error" => TypeSymbol.Error,
                _ => throw new InvalidDataException($"Unknown type '{name}'"),
            };
        }

        private static void SetAt(List<object> symbolsById, int id, object symbol)
        {
            while (symbolsById.Count <= id)
            {
                symbolsById.Add(null!);
            }

            symbolsById[id] = symbol;
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

                return Unescape(token);
            }

            public int ExpectInt()
            {
                return int.Parse(ExpectString(), CultureInfo.InvariantCulture);
            }

            public bool TryExpect(out string token)
            {
                // 跳过节点开括号 `(`
                while (_pos < _tokens.Length && _tokens[_pos] == "(")
                {
                    _pos++;
                }

                if (_pos >= _tokens.Length)
                {
                    token = null!;
                    return false;
                }

                // `)` 不消费（留给 End()），返回 false 终止当前列表
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
                // 当前 token 应为节点闭括号 `)`（直接消费，不跳过 `(`）
                if (_pos >= _tokens.Length)
                {
                    throw new InvalidDataException($"unexpected end of .cod file at pos {_pos}; context: {Context()}");
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
                // 跳过节点开括号 `(`；返回原子或 `)`（列表终止）
                while (true)
                {
                    if (_pos >= _tokens.Length)
                    {
                        throw new InvalidDataException("unexpected end of .cod file");
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
