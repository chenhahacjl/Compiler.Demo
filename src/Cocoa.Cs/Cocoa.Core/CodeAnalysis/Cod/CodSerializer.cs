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
    ///
    /// 文本格式（可读优先，类型/函数/变量一律按名字引用，不用数字 id）：
    ///   (type)     内建/数组类型内联为名字引用：int / int[] / int[][]；类/枚举用全名 System.Console
    ///   (enum)     (enum MyLib.Color members:3 (Red 0) (Green 1) (Blue 2))
    ///   (systype)  (systype System.Object)——内建单例按全名映射
    ///   (cls)      (cls System.Console public methods:2 WriteLine Write)
    ///   (fn)       (fn MyLib.Add(i32,i32) name:Add ret:i32 ns:MyLib owner:- extern:false ...
    ///               params:2 (par MyLib.Add/a a i32 0) ...)
    ///              函数键 = [命名空间或宿主类.]函数名(参数类型列表)，重载靠参数类型区分
    ///   (glb/loc)  (glb global:version true i32 (const i:1)) / (loc MyLib.Factorial/result false i32)
    ///              变量键：全局 global:名字；局部/参数 函数键/名字（同名冲突加 #2、#3 后缀）
    ///   运算符      文本记号 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   布尔/枚举词  true false；public internal protected private；winapi cdecl stdcall；unicode ansi auto
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

            // 收集符号——函数体按 Functions（声明序）遍历，保证确定性（ImmutableDictionary 迭代序不稳定）
            foreach (var e in program.Enums)
            {
                registry.RegisterType(e);
            }
            foreach (var c in program.Classes)
            {
                registry.RegisterType(c);
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
                CollectBody(registry, fn, body, labels);
                labelsByFunction[fn] = labels;
            }

            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）
            registry.Seal();

            var w = new Writer(writer);
            w.Open("cod");
            w.Field(Magic);
            w.Field(Version);

            // 符号表（按注册序）
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
                w.Field(registry.FnKey(fn));
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

        private static void CollectBody(Registry registry, FunctionSymbol owner, BoundStatement statement, Dictionary<string, BoundLabel> labels)
        {
            switch (statement.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    foreach (var s in ((BoundBlockStatement)statement).Statements)
                    {
                        CollectBody(registry, owner, s, labels);
                    }
                    break;
                case BoundNodeKind.VariableDeclaration:
                    {
                        var d = (BoundVariableDeclaration)statement;
                        registry.RegisterVariable(d.Variable, owner);
                        CollectExpression(registry, owner, d.Initializer, labels);
                        break;
                    }
                case BoundNodeKind.IfStatement:
                    {
                        var n = (BoundIfStatement)statement;
                        CollectExpression(registry, owner, n.Condition, labels);
                        CollectBody(registry, owner, n.ThenStatement, labels);
                        if (n.ElseStatement != null)
                        {
                            CollectBody(registry, owner, n.ElseStatement, labels);
                        }
                        break;
                    }
                case BoundNodeKind.WhileStatement:
                    {
                        var n = (BoundWhileStatement)statement;
                        CollectExpression(registry, owner, n.Condition, labels);
                        CollectBody(registry, owner, n.Body, labels);
                        break;
                    }
                case BoundNodeKind.DoWhileStatement:
                    {
                        var n = (BoundDoWhileStatement)statement;
                        CollectBody(registry, owner, n.Body, labels);
                        CollectExpression(registry, owner, n.Condition, labels);
                        break;
                    }
                case BoundNodeKind.ForStatement:
                    {
                        var n = (BoundForStatement)statement;
                        registry.RegisterVariable(n.Variable, owner);
                        CollectExpression(registry, owner, n.LowerBound, labels);
                        CollectExpression(registry, owner, n.UpperBound, labels);
                        if (n.Step != null)
                        {
                            CollectExpression(registry, owner, n.Step, labels);
                        }

                        CollectBody(registry, owner, n.Body, labels);
                        break;
                    }
                case BoundNodeKind.LabelStatement:
                    {
                        var n = (BoundLabelStatement)statement;
                        labels[n.Label.Name] = n.Label;
                        break;
                    }
                case BoundNodeKind.ConditionalGotoStatement:
                    CollectExpression(registry, owner, ((BoundConditionalGotoStatement)statement).Condition, labels);
                    break;
                case BoundNodeKind.ReturnStatement:
                    {
                        var n = (BoundReturnStatement)statement;
                        if (n.Expression != null)
                        {
                            CollectExpression(registry, owner, n.Expression, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ExpressionStatement:
                    CollectExpression(registry, owner, ((BoundExpressionStatement)statement).Expression, labels);
                    break;
                case BoundNodeKind.SequencePointStatement:
                    CollectBody(registry, owner, ((BoundSequencePointStatement)statement).Statement, labels);
                    break;
            }
        }

        private static void CollectExpression(Registry registry, FunctionSymbol owner, BoundExpression expression, Dictionary<string, BoundLabel> labels)
        {
            switch (expression.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    registry.RegisterType(expression.Type);
                    break;
                case BoundNodeKind.VariableExpression:
                    registry.RegisterVariable(((BoundVariableExpression)expression).Variable, owner);
                    break;
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)expression;
                        registry.RegisterVariable(n.Variable, owner);
                        CollectExpression(registry, owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)expression;
                        registry.RegisterVariable(n.Variable, owner);
                        registry.RegisterType(n.Op.LeftType);
                        registry.RegisterType(n.Op.RightType);
                        registry.RegisterType(n.Op.ResultType);
                        CollectExpression(registry, owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.UnaryExpression:
                    {
                        var n = (BoundUnaryExpression)expression;
                        registry.RegisterType(n.Op.OperandType);
                        registry.RegisterType(n.Op.ResultType);
                        CollectExpression(registry, owner, n.Operand, labels);
                        break;
                    }
                case BoundNodeKind.BinaryExpression:
                    {
                        var n = (BoundBinaryExpression)expression;
                        registry.RegisterType(n.Op.LeftType);
                        registry.RegisterType(n.Op.RightType);
                        registry.RegisterType(n.Op.ResultType);
                        CollectExpression(registry, owner, n.Left, labels);
                        CollectExpression(registry, owner, n.Right, labels);
                        break;
                    }
                case BoundNodeKind.ConditionalExpression:
                    {
                        var n = (BoundConditionalExpression)expression;
                        CollectExpression(registry, owner, n.Condition, labels);
                        CollectExpression(registry, owner, n.WhenTrue, labels);
                        CollectExpression(registry, owner, n.WhenFalse, labels);
                        break;
                    }
                case BoundNodeKind.CallExpression:
                    {
                        var n = (BoundCallExpression)expression;
                        registry.RegisterFunction(n.Function);
                        foreach (var a in n.Arguments)
                        {
                            CollectExpression(registry, owner, a, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ConversionExpression:
                    {
                        var n = (BoundConversionExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, owner, n.Length, labels);
                        foreach (var i in n.Initializers)
                        {
                            CollectExpression(registry, owner, i, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ElementAccessExpression:
                    {
                        var n = (BoundElementAccessExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, owner, n.Target, labels);
                        CollectExpression(registry, owner, n.Index, labels);
                        break;
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, owner, n.Target, labels);
                        CollectExpression(registry, owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.MemberAccessExpression:
                    {
                        var n = (BoundMemberAccessExpression)expression;
                        registry.RegisterType(n.Type);
                        CollectExpression(registry, owner, n.Target, labels);
                        break;
                    }
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)expression;
                        registry.RegisterType(n.Type);
                        if (n.Method != null)
                        {
                            registry.RegisterFunction(n.Method);
                        }
                        CollectExpression(registry, owner, n.Expression, labels);
                        foreach (var a in n.Arguments)
                        {
                            CollectExpression(registry, owner, a, labels);
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
                        CollectExpression(registry, owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.AsExpression:
                    {
                        var n = (BoundAsExpression)expression;
                        registry.RegisterType(n.TargetType);
                        CollectExpression(registry, owner, n.Expression, labels);
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
                        w.Field(registry.VarKey(n.Variable));
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
                        w.Field(registry.VarKey(n.Variable));
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
                        w.Field(BoolWord(n.JumpIfTrue));
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
                        w.Field(TypeRef(n.Type));
                        w.Field(EncodeValue(n.Value));
                        w.End();
                        break;
                    }
                case BoundNodeKind.VariableExpression:
                    {
                        var n = (BoundVariableExpression)expression;
                        w.Open("var");
                        w.Field(registry.VarKey(n.Variable));
                        w.End();
                        break;
                    }
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)expression;
                        w.Open("assign");
                        w.Field(registry.VarKey(n.Variable));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)expression;
                        w.Open("cassign");
                        w.Field(registry.VarKey(n.Variable));
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
                        w.Field(registry.FnKey(n.Function));
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
                        w.Field(TypeRef(n.Type));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.IsExpression:
                    {
                        var n = (BoundIsExpression)expression;
                        w.Open("istype");
                        w.Field(TypeRef(n.TargetType));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.AsExpression:
                    {
                        var n = (BoundAsExpression)expression;
                        w.Open("astype");
                        w.Field(TypeRef(n.TargetType));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)expression;
                        w.Open("arrnew");
                        w.Field(TypeRef(n.Type));
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
                        w.Field(TypeRef(n.Type));
                        WriteExpression(w, registry, labels, n.Target);
                        WriteExpression(w, registry, labels, n.Index);
                        w.End();
                        break;
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)expression;
                        w.Open("elemassign");
                        w.Field(TypeRef(n.Type));
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
                        w.Field(TypeRef(n.Type));
                        w.Field(Str(n.Identifier));
                        WriteExpression(w, registry, labels, n.Target);
                        w.End();
                        break;
                    }
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)expression;
                        w.Open("membercall");
                        w.Field(TypeRef(n.Type));
                        w.Field(Str(n.Identifier));
                        w.Field(n.Method != null ? registry.FnKey(n.Method) : "-");
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
                        w.Field(TypeRef(n.Type));
                        w.End();
                        break;
                    }
                case BoundNodeKind.ThisExpression:
                    {
                        var n = (BoundThisExpression)expression;
                        w.Open("this");
                        w.Field(TypeRef(n.Type));
                        w.End();
                        break;
                    }
            }
        }

        private static void WriteUnaryOperator(Writer w, Registry registry, BoundUnaryOperator op)
        {
            w.Open("uop");
            w.Field(UnaryOpText(op.SyntaxKind));
            w.Field(TypeRef(op.OperandType));
            w.End();
        }

        private static void WriteBinaryOperator(Writer w, Registry registry, BoundBinaryOperator op)
        {
            w.Open("bop");
            w.Field(BinaryOpText(op.SyntaxKind));
            w.Field(TypeRef(op.LeftType));
            w.Field(TypeRef(op.RightType));
            w.End();
        }

        // ---------------------------------------------------------------- write: symbols

        private static void EmitEnumSymbol(Writer w, Registry registry, EnumTypeSymbol e)
        {
            w.Open("enum");
            w.Field(e.FullName);
            var members = e.MemberNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            w.Field("members:" + members.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var name in members)
            {
                e.TryGetMember(name, out var value);
                w.Open(name);
                w.Field(value);
                w.End();
            }
            w.End();
        }

        /// <summary>6e-M19 M2-c：内建单例（System.Object/System.Type）按全名序列化，读侧映射回单例。</summary>
        private static void EmitBuiltinSystemClass(Writer w, Registry registry, ClassTypeSymbol classType)
        {
            w.Open("systype");
            w.Field(classType.FullName);
            w.End();
        }

        private static void EmitClassSymbol(Writer w, Registry registry, ClassTypeSymbol classType)
        {
            w.Open("cls");
            w.Field(classType.FullName);
            w.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 序列化全部静态方法名（6e-M18：容器类允许带体静态方法，如 Console.WriteLine/Math.Max；syscall/extern 亦为静态）。
            // 方法本体由各自 fn 条目携带（owner 字段回填类归属），这里仅列名供阅读。
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(method.Name);
            }
            w.End();
        }

        private static void EmitFunctionSymbol(Writer w, Registry registry, FunctionSymbol fn)
        {
            w.Open("fn");
            w.Field(registry.FnKey(fn));
            w.Field("name:" + Str(fn.Name));
            w.Field("ret:" + TypeRef(fn.ReturnType));
            w.Field("ns:" + (fn.Namespace.Length > 0 ? Str(fn.Namespace) : "-"));
            w.Field("owner:" + (fn.ContainingClass != null ? fn.ContainingClass.FullName : "-"));
            w.Field("extern:" + BoolWord(fn.IsExtern));
            w.Field("dll:" + (fn.DllName != null ? Str(fn.DllName) : "-"));
            w.Field("cc:" + fn.CallingConvention.ToString().ToLowerInvariant());
            w.Field("builtin:" + (fn.BuiltinKind != null ? fn.BuiltinKind.Value.ToString() : "-"));
            w.Field("entry:" + (fn.EntryPoint != null ? Str(fn.EntryPoint) : "-"));
            w.Field("charset:" + (fn.CharSet != null ? fn.CharSet.Value.ToString().ToLowerInvariant() : "-"));
            w.Field("params:" + fn.Parameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var p in fn.Parameters)
            {
                w.Open("par");
                w.Field(registry.VarKey(p));
                w.Field(Str(p.Name));
                w.Field(TypeRef(p.Type));
                w.Field(p.Ordinal);
                w.End();
            }
            w.End();
        }

        private static void EmitVariableSymbol(Writer w, Registry registry, VariableSymbol v)
        {
            w.Open(v is GlobalVariableSymbol ? "glb" : "loc");
            w.Field(registry.VarKey(v));
            w.Field(BoolWord(v.IsReadOnly));
            w.Field(TypeRef(v.Type));
            if (v.Constant != null)
            {
                w.Open("const");
                w.Field(EncodeValue(v.Constant.Value));
                w.End();
            }

            w.End();
        }

        // ---------------------------------------------------------------- write: naming

        /// <summary>类型的文本引用：内建/数组用短名（int / int[][]），类/枚举用全名。</summary>
        private static string TypeRef(TypeSymbol type)
        {
            if (type is EnumTypeSymbol enumType)
            {
                return enumType.FullName;
            }

            if (type is ClassTypeSymbol classType)
            {
                return classType.FullName;
            }

            return type.Name;
        }

        private static string BoolWord(bool value)
        {
            return value ? "true" : "false";
        }

        private static string UnaryOpText(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.PlusToken => "+",
                SyntaxKind.MinusToken => "-",
                SyntaxKind.BangToken => "!",
                SyntaxKind.TildeToken => "~",
                _ => throw new NotSupportedException($"Unsupported unary operator '{kind}'"),
            };
        }

        private static string BinaryOpText(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.PlusToken => "+",
                SyntaxKind.MinusToken => "-",
                SyntaxKind.StarToken => "*",
                SyntaxKind.SlashToken => "/",
                SyntaxKind.PercentToken => "%",
                SyntaxKind.ShiftLeftToken => "<<",
                SyntaxKind.ShiftRightToken => ">>",
                SyntaxKind.AmpersandToken => "&",
                SyntaxKind.PipeToken => "|",
                SyntaxKind.HatToken => "^",
                SyntaxKind.EqualsEqualsToken => "==",
                SyntaxKind.BangEqualsToken => "!=",
                SyntaxKind.LessToken => "<",
                SyntaxKind.LessOrEqualsToken => "<=",
                SyntaxKind.GreaterToken => ">",
                SyntaxKind.GreaterOrEqualsToken => ">=",
                SyntaxKind.AmpersandAmpersandToken => "&&",
                SyntaxKind.PipePipeToken => "||",
                _ => throw new NotSupportedException($"Unsupported binary operator '{kind}'"),
            };
        }

        private static SyntaxKind ParseUnaryOpText(string text)
        {
            return text switch
            {
                "+" => SyntaxKind.PlusToken,
                "-" => SyntaxKind.MinusToken,
                "!" => SyntaxKind.BangToken,
                "~" => SyntaxKind.TildeToken,
                _ => throw new InvalidDataException($"Unknown unary operator '{text}'"),
            };
        }

        private static SyntaxKind ParseBinaryOpText(string text)
        {
            return text switch
            {
                "+" => SyntaxKind.PlusToken,
                "-" => SyntaxKind.MinusToken,
                "*" => SyntaxKind.StarToken,
                "/" => SyntaxKind.SlashToken,
                "%" => SyntaxKind.PercentToken,
                "<<" => SyntaxKind.ShiftLeftToken,
                ">>" => SyntaxKind.ShiftRightToken,
                "&" => SyntaxKind.AmpersandToken,
                "|" => SyntaxKind.PipeToken,
                "^" => SyntaxKind.HatToken,
                "==" => SyntaxKind.EqualsEqualsToken,
                "!=" => SyntaxKind.BangEqualsToken,
                "<" => SyntaxKind.LessToken,
                "<=" => SyntaxKind.LessOrEqualsToken,
                ">" => SyntaxKind.GreaterToken,
                ">=" => SyntaxKind.GreaterOrEqualsToken,
                "&&" => SyntaxKind.AmpersandAmpersandToken,
                "||" => SyntaxKind.PipePipeToken,
                _ => throw new InvalidDataException($"Unknown binary operator '{text}'"),
            };
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

        /// <summary>写侧符号注册表：去重 + 发射顺序（id 仅用于排序，不写入文件）。</summary>
        private sealed class Registry
        {
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            private readonly List<FunctionSymbol> _functions = new();
            private readonly List<(VariableSymbol Symbol, FunctionSymbol? Owner)> _variables = new();
            private readonly Dictionary<FunctionSymbol, string> _fnKeys = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, string> _varKeys = new(ReferenceEqualityComparer.Instance);

            public List<Action<Writer, Registry>> Emitters { get; } = new();

            public string FnKey(FunctionSymbol fn) => _fnKeys[fn];

            public string VarKey(VariableSymbol v) => _varKeys[v];

            public void RegisterType(TypeSymbol type)
            {
                if (_ids.ContainsKey(type))
                {
                    return;
                }

                _ids[type] = _ids.Count;

                if (type is ClassTypeSymbol classType)
                {
                    RegisterClassCore(classType);
                }
                else if (type is EnumTypeSymbol enumType)
                {
                    Emitters.Add((w, r) => EmitEnumSymbol(w, r, enumType));
                }
                // 其余（内建/数组）自描述，无需独立条目
            }

            private void RegisterClassCore(ClassTypeSymbol classType)
            {
                // 6e-M19 M2-c：内建单例（System.Object/System.Type）不发 cls——读侧会造出新类破坏单例同一性；
                // 发 systype 按全名映射回单例（成员面由 Ensure 内建注入，不序列化）
                if (SystemObjectMembers.IsBuiltinSystemClass(classType))
                {
                    Emitters.Add((w, r) => EmitBuiltinSystemClass(w, r, classType));
                    return;
                }

                Emitters.Add((w, r) => EmitClassSymbol(w, r, classType));
            }

            public void RegisterFunction(FunctionSymbol fn)
            {
                if (_ids.ContainsKey(fn))
                {
                    return;
                }

                // 类方法：容器类全静态（syscall/extern 及带体静态方法，6e-M18）作为独立 fn 序列化；实例方法/构造由类壳过滤。
                // 例外：Object 内建方法（M2-c）带 BuiltinKind，读侧经单例复用重建，须随引用序列化
                if (fn.ContainingClass != null && !fn.IsStatic && !SystemObjectMembers.IsBuiltinSystemClass(fn.ContainingClass))
                {
                    return;
                }

                _ids[fn] = _ids.Count;
                _functions.Add(fn);

                RegisterType(fn.ReturnType);
                foreach (var p in fn.Parameters)
                {
                    RegisterType(p.Type);
                }

                Emitters.Add((w, r) => EmitFunctionSymbol(w, r, fn));

                foreach (var p in fn.Parameters)
                {
                    _ids[p] = _ids.Count;
                    _variables.Add((p, fn));
                }
            }

            public void RegisterVariable(VariableSymbol v, FunctionSymbol? owner = null)
            {
                if (_ids.ContainsKey(v))
                {
                    return;
                }

                RegisterType(v.Type);

                _ids[v] = _ids.Count;
                _variables.Add((v, owner));
                Emitters.Add((w, r) => EmitVariableSymbol(w, r, v));
            }

            /// <summary>收集完成后统一命名：函数键与变量键（全局 global:名字；局部/参数 函数键/名字；冲突加 #2/#3）。</summary>
            public void Seal()
            {
                foreach (var fn in _functions)
                {
                    var paramTypes = string.Join(",", fn.Parameters.Select(p => TypeRef(p.Type)));
                    var head = fn.ContainingClass != null
                        ? fn.ContainingClass.FullName + "." + fn.Name
                        : fn.Namespace.Length > 0 ? fn.Namespace + "." + fn.Name : fn.Name;
                    // 方括号包裹参数类型（圆括号会被 .cod 分词器当结构符拆开）
                    _fnKeys[fn] = head + "[" + paramTypes + "]";
                }

                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (symbol, owner) in _variables)
                {
                    var baseKey = owner == null
                        ? "global:" + symbol.Name
                        : _fnKeys[owner] + "/" + symbol.Name;
                    var key = baseKey;
                    var suffix = 2;
                    while (!used.Add(key))
                    {
                        key = baseKey + "#" + suffix;
                        suffix++;
                    }

                    _varKeys[symbol] = key;
                }
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

            var context = new ReadContext();
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
                        ReadSymbols(reader, context);
                        break;
                    case "bodies":
                        ReadBodies(reader, context, bodies);
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
                                    platforms.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "refdll":
                                    dotnetRefs.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "refcod":
                                    codRefs.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "import":
                                    imports.Add(Unescape(reader.ExpectString()));
                                    break;
                                case "ns":
                                    namespaces.Add(Unescape(reader.ExpectString()));
                                    break;
                            }

                            reader.End();
                        }

                        reader.End();
                        break;
                }
            }

            return new CodProgram(
                context.Functions.ToImmutable(),
                context.Globals.ToImmutable(),
                context.Enums.ToImmutable(),
                context.Classes.ToImmutable(),
                bodies.ToImmutable(),
                requires,
                platforms.ToImmutable(),
                dotnetRefs.ToImmutable(),
                imports.ToImmutable(),
                codRefs.ToImmutable(),
                namespaces.ToImmutable());
        }

        /// <summary>读侧共享状态：按名字/键索引的符号表 + 程序集符号清单。</summary>
        private sealed class ReadContext
        {
            /// <summary>类/枚举全名 → 类型符号（内建类型不经此表，直接解析）。</summary>
            public Dictionary<string, TypeSymbol> TypesByName { get; } = new(StringComparer.Ordinal);

            /// <summary>函数键 → 函数符号。</summary>
            public Dictionary<string, FunctionSymbol> FunctionsByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>变量键 → 变量/参数符号。</summary>
            public Dictionary<string, VariableSymbol> VariablesByKey { get; } = new(StringComparer.Ordinal);

            public ImmutableArray<FunctionSymbol>.Builder Functions { get; } = ImmutableArray.CreateBuilder<FunctionSymbol>();

            public ImmutableArray<GlobalVariableSymbol>.Builder Globals { get; } = ImmutableArray.CreateBuilder<GlobalVariableSymbol>();

            public ImmutableArray<EnumTypeSymbol>.Builder Enums { get; } = ImmutableArray.CreateBuilder<EnumTypeSymbol>();

            public ImmutableArray<ClassTypeSymbol>.Builder Classes { get; } = ImmutableArray.CreateBuilder<ClassTypeSymbol>();

            public void AddNamedType(string fullName, TypeSymbol type)
            {
                TypesByName[fullName] = type;
            }
        }

        private static void ReadSymbols(Reader reader, ReadContext context)
        {
            while (reader.TryExpect(out var kind))
            {
                switch (kind)
                {
                    case "enum":
                        ReadEnum(reader, context);
                        break;
                    case "systype":
                        ReadSystemType(reader, context);
                        break;
                    case "cls":
                        ReadClass(reader, context);
                        break;
                    case "fn":
                        ReadFunction(reader, context);
                        break;
                    case "glb":
                        ReadVariable(reader, context, isGlobal: true);
                        break;
                    case "loc":
                        ReadVariable(reader, context, isGlobal: false);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown symbol kind '{kind}'");
                }
            }

            reader.End();
        }

        private static void ReadEnum(Reader reader, ReadContext context)
        {
            var fullName = reader.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var count = ReadCountField(reader, "members:");
            var members = new Dictionary<string, int>();
            for (var i = 0; i < count; i++)
            {
                var memberName = reader.ExpectKind();
                var value = reader.ExpectInt();
                members[Unescape(memberName)] = value;
                reader.End();
            }

            var enumType = new EnumTypeSymbol(name, members, ns);
            context.Enums.Add(enumType);
            context.AddNamedType(fullName, enumType);
            reader.End();
        }

        private static void ReadSystemType(Reader reader, ReadContext context)
        {
            // 6e-M19 M2-c：内建单例按全名映射（成员面已由 Ensure 内建注入）
            var fullName = reader.ExpectString();
            var singleton = fullName switch
            {
                "System.Object" => ClassTypeSymbol.SystemObject,
                "System.Type" => ClassTypeSymbol.SystemType,
                _ => throw new InvalidDataException($"Unknown builtin system class '{fullName}'"),
            };
            context.Classes.Add(singleton);
            context.AddNamedType(fullName, singleton);
            reader.End();
        }

        private static void ReadClass(Reader reader, ReadContext context)
        {
            var fullName = reader.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var visibilityText = reader.ExpectString();
            if (!Enum.TryParse<Visibility>(visibilityText, ignoreCase: true, out var visibility))
            {
                throw new InvalidDataException($"Unknown visibility '{visibilityText}' on class '{fullName}'");
            }

            var methodCount = ReadCountField(reader, "methods:");
            // 方法名仅供阅读，方法符号由各 fn 条目的 owner 字段回填
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectString();
            }

            var classType = new ClassTypeSymbol(name, ns, visibility, declaration: null);
            // 6e-M19 M2-c：.cod 类默认继承 System.Object（与源码绑定一致；.cod v1 不序列化接口声明）
            classType.BaseType = ClassTypeSymbol.SystemObject;
            context.Classes.Add(classType);
            context.AddNamedType(fullName, classType);
            reader.End();
        }

        private static void ReadFunction(Reader reader, ReadContext context)
        {
            var key = reader.ExpectString();
            var name = ReadLabeledField(reader, "name:");
            var returnType = ResolveTypeRef(ReadLabeledField(reader, "ret:"), context);
            var nsText = ReadLabeledField(reader, "ns:");
            var ownerText = ReadLabeledField(reader, "owner:");
            var isExtern = ParseBoolWord(ReadLabeledField(reader, "extern:"));
            var dllText = ReadLabeledField(reader, "dll:");
            var ccText = ReadLabeledField(reader, "cc:");
            var builtinText = ReadLabeledField(reader, "builtin:");
            var entryText = ReadLabeledField(reader, "entry:");
            var charSetText = ReadLabeledField(reader, "charset:");

            var ns = nsText == "-" ? "" : nsText;
            var dllName = dllText == "-" ? null : dllText;
            var entryPoint = entryText == "-" ? null : entryText;
            var builtinKind = builtinText == "-" ? (BuiltinKind?)null : BuiltinFunctions.GetByKindName(builtinText) ?? SystemObjectMembers.GetByKindName(builtinText);
            if (builtinKind == null && builtinText != "-")
            {
                throw new InvalidDataException($"Unknown builtin kind '{builtinText}' on function '{key}'");
            }

            CharSet? charSet;
            if (charSetText == "-")
            {
                charSet = null;
            }
            else if (Enum.TryParse<CharSet>(charSetText, ignoreCase: true, out var parsedCharSet))
            {
                charSet = parsedCharSet;
            }
            else
            {
                throw new InvalidDataException($"Unknown charset '{charSetText}' on function '{key}'");
            }

            CallingConvention callingConvention;
            if (Enum.TryParse<CallingConvention>(ccText, ignoreCase: true, out var parsedCc))
            {
                callingConvention = parsedCc;
            }
            else
            {
                throw new InvalidDataException($"Unknown calling convention '{ccText}' on function '{key}'");
            }

            var containingClass = ownerText == "-" ? null : ResolveOwnerClass(ownerText, context);

            var paramCount = ReadCountField(reader, "params:");
            var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
            for (var i = 0; i < paramCount; i++)
            {
                reader.Expect("par");
                var pKey = reader.ExpectString();
                var pName = Unescape(reader.ExpectString());
                var pType = ResolveTypeRef(reader.ExpectString(), context);
                var ordinal = reader.ExpectInt();
                var parameter = new ParameterSymbol(pName, pType, ordinal);
                parameters.Add(parameter);
                context.VariablesByKey[pKey] = parameter;
                reader.End();
            }

            // 6e-M19 M2-c：Object 内建方法复用单例（保持符号同一性，发射器按 BuiltinKind 分发）
            if (containingClass != null && builtinKind != null && SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                var singleton = SystemObjectMembers.GetByKind(builtinKind.Value);
                if (singleton != null)
                {
                    context.Functions.Add(singleton);
                    context.FunctionsByKey[key] = singleton;
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
                    callingConvention: callingConvention,
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
                    callingConvention: callingConvention,
                    @namespace: ns,
                    entryPoint: entryPoint,
                    charSet: charSet);
            }

            context.Functions.Add(function);
            context.FunctionsByKey[key] = function;

            // 类方法回填：含类归属的 fn 归入其类（6e-M18：容器类全静态——syscall/extern 及带体静态方法）。
            // 内建单例（System.Object/System.Type，M2-c）成员已由 Ensure 注入，跳过回填防重复/防误标 static
            if (containingClass != null && !SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                function.IsStatic = true;
                containingClass.AddMethod(function);
            }

            reader.End();
        }

        private static void ReadVariable(Reader reader, ReadContext context, bool isGlobal)
        {
            var key = reader.ExpectString();
            var isReadOnly = ParseBoolWord(reader.ExpectString());
            var type = ResolveTypeRef(reader.ExpectString(), context);
            BoundConstant? constant = null;

            if (reader.PeekRaw() == "(")
            {
                reader.Expect("const");
                var encoded = reader.ExpectString();
                var value = DecodeValue(encoded);
                constant = new BoundConstant(value);
                reader.End();
            }

            var name = KeyToName(key);
            VariableSymbol variable = isGlobal
                ? new GlobalVariableSymbol(name, isReadOnly, type, constant)
                : new LocalVariableSymbol(name, isReadOnly, type, constant);

            if (isGlobal)
            {
                context.Globals.Add((GlobalVariableSymbol)variable);
            }

            context.VariablesByKey[key] = variable;
            reader.End();
        }

        private static void ReadBodies(Reader reader, ReadContext context, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder bodies)
        {
            while (reader.TryExpect(out var kind) && kind == "body")
            {
                var fnKey = reader.ExpectString();
                if (!context.FunctionsByKey.TryGetValue(fnKey, out var function))
                {
                    throw new InvalidDataException($"Unknown function '{fnKey}' in bodies");
                }

                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                var body = (BoundBlockStatement)ReadStatement(reader, context, labels);

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

        // ---------------------------------------------------------------- read: resolution helpers

        private static TypeSymbol ResolveTypeRef(string reference, ReadContext context)
        {
            var baseName = reference;
            var dims = 0;
            while (baseName.EndsWith("[]", StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - 2);
                dims++;
            }

            var core = ResolveNamedType(baseName, context);
            for (var i = 0; i < dims; i++)
            {
                core = TypeSymbol.ArrayOf(core);
            }

            return core;
        }

        private static TypeSymbol ResolveNamedType(string name, ReadContext context)
        {
            if (context.TypesByName.TryGetValue(name, out var known))
            {
                return known;
            }

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
                "?" => TypeSymbol.Error,
                _ => throw new InvalidDataException($"Unknown type '{name}'"),
            };
        }

        private static ClassTypeSymbol ResolveOwnerClass(string fullName, ReadContext context)
        {
            if (!context.TypesByName.TryGetValue(fullName, out var type) || type is not ClassTypeSymbol classType)
            {
                throw new InvalidDataException($"Unknown owner class '{fullName}'");
            }

            return classType;
        }

        private static VariableSymbol ResolveVariable(string key, ReadContext context)
        {
            if (!context.VariablesByKey.TryGetValue(key, out var variable))
            {
                throw new InvalidDataException($"Unknown variable '{key}'");
            }

            return variable;
        }

        private static FunctionSymbol ResolveFunction(string key, ReadContext context)
        {
            if (!context.FunctionsByKey.TryGetValue(key, out var function))
            {
                throw new InvalidDataException($"Unknown function '{key}'");
            }

            return function;
        }

        private static bool ParseBoolWord(string text)
        {
            return text switch
            {
                "true" => true,
                "false" => false,
                _ => throw new InvalidDataException($"Expected 'true'/'false' but found '{text}'"),
            };
        }

        /// <summary>读取 label:value 形式的字段并校验标签。</summary>
        private static string ReadLabeledField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return Unescape(token.Substring(label.Length));
        }

        /// <summary>读取 count:N 形式的计数字段。</summary>
        private static int ReadCountField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return int.Parse(token.Substring(label.Length), CultureInfo.InvariantCulture);
        }

        /// <summary>全名拆分为（命名空间, 名）；无点号时命名空间为空。</summary>
        private static (string Namespace, string Name) SplitFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            return lastDot < 0 ? ("", fullName) : (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1));
        }

        /// <summary>变量键还原真实符号名：去掉 global:/函数键前缀与 #N 冲突后缀。</summary>
        private static string KeyToName(string key)
        {
            var name = key;
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            var hash = name.LastIndexOf('#');
            if (hash >= 0 && int.TryParse(name.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                name = name.Substring(0, hash);
            }

            return Unescape(name);
        }

        // ---------------------------------------------------------------- read: statements

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
                        return new BoundForStatement(null, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
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
                        var target = ReadExpression(reader, context, labels);
                        return new BoundMemberAccessExpression(null, type, target, identifier);
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
                        var type = (ClassTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
                        return new BoundStaticTypeExpression(null, type);
                    }
                case "this":
                    {
                        var type = (ClassTypeSymbol)ResolveTypeRef(reader.ExpectString(), context);
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
            var syntaxKind = ParseUnaryOpText(reader.ExpectString());
            var operandType = ResolveTypeRef(reader.ExpectString(), context);
            var op = BoundUnaryOperator.Bind(syntaxKind, operandType);
            reader.End();
            return op ?? throw new InvalidDataException($"Cannot bind unary operator {syntaxKind} on {operandType}");
        }

        private static BoundBinaryOperator ReadBinaryOperator(Reader reader, ReadContext context)
        {
            reader.Expect("bop");
            var syntaxKind = ParseBinaryOpText(reader.ExpectString());
            var leftType = ResolveTypeRef(reader.ExpectString(), context);
            var rightType = ResolveTypeRef(reader.ExpectString(), context);
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

            /// <summary>窥探当前原始 token（不跳过 `(`）——用于判断子节点是否出现。</summary>
            public string PeekRaw()
            {
                return _pos < _tokens.Length ? _tokens[_pos] : "";
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
