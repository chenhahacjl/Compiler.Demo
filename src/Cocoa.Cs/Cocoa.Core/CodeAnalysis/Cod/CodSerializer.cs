using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Cod
{
    /// <summary>
    /// `.cod` 璇箟灞傚簭鍒楀寲鍣細绗﹀彿琛?+ 闄嶇骇 BoundProgram锛堝嚱鏁颁綋锛夋枃鏈?round-trip銆?
    /// 鍙屽悗绔叡鐢紙native 鈫?BoundTreeToIr锛孖L 鈫?IlEmitter锛夛紱璇硶鑺傜偣锛圫yntax锛変笉搴忓垪鍖栵紙缃?null锛夈€?
    ///
    /// 鏂囨湰鏍煎紡锛堝彲璇讳紭鍏堬紝绫诲瀷/鍑芥暟/鍙橀噺涓€寰嬫寜鍚嶅瓧寮曠敤锛屼笉鐢ㄦ暟瀛?id锛夛細
    ///   (type)     鍐呭缓/鏁扮粍绫诲瀷鍐呰仈涓哄悕瀛楀紩鐢細int / int[] / int[][]锛涚被/鏋氫妇鐢ㄥ叏鍚?System.Console
    ///   (enum)     (enum MyLib.Color members:3 (Red 0) (Green 1) (Blue 2))
    ///   (systype)  (systype System.Object)鈥斺€斿唴寤哄崟渚嬫寜鍏ㄥ悕鏄犲皠
    ///   (cls)      (cls System.Console public methods:2 WriteLine[string] ReadKey)鈥斺€旀柟娉曞垪 Name[鍙傛暟绫诲瀷] 绛惧悕
    ///   (fn)       (fn MyLib.Add(i32,i32) name:Add ret:i32 ns:MyLib owner:- extern:false ...
    ///               params:2 (par MyLib.Add/a a i32 0) ...)
    ///              鍑芥暟閿?= [鍛藉悕绌洪棿鎴栧涓荤被.]鍑芥暟鍚?鍙傛暟绫诲瀷鍒楄〃)锛岄噸杞介潬鍙傛暟绫诲瀷鍖哄垎
    ///   (glb/loc)  (glb global:version true i32 (const i:1)) / (loc MyLib.Factorial/result false i32)
    ///              鍙橀噺閿細鍏ㄥ眬 global:鍚嶅瓧锛涘眬閮?鍙傛暟 鍑芥暟閿?鍚嶅瓧锛堝悓鍚嶅啿绐佸姞 #2銆?3 鍚庣紑锛?
    ///   杩愮畻绗?     鏂囨湰璁板彿 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   甯冨皵/鏋氫妇璇? true false锛沺ublic internal protected private锛泈inapi cdecl stdcall锛泆nicode ansi auto
    /// </summary>
    internal static class CodSerializer
    {
        public const string Magic = "COCOD";
        public const int Version = 1;

        /// <summary>瀹屾暣鎬ф牎楠岋細鏂囦欢鏈 `(checksum sha256:&lt;hex&gt;)` 瑕嗙洊鍏跺墠鍏ㄩ儴瀛楄妭锛圲TF-8锛夛紱璇讳晶寮哄埗鏍￠獙銆?/summary>
        private const string ChecksumTag = "sha256:";

        // ---------------------------------------------------------------- write

        public static void Write(TextWriter writer, CodProgram program)
        {
            var registry = new Registry();
            var labelsByFunction = new Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>>(ReferenceEqualityComparer.Instance);

            // 鏀堕泦绗﹀彿鈥斺€斿嚱鏁颁綋鎸?Functions锛堝０鏄庡簭锛夐亶鍘嗭紝淇濊瘉纭畾鎬э紙ImmutableDictionary 杩唬搴忎笉绋冲畾锛?
            foreach (var e in program.Enums)
            {
                registry.RegisterType(e);
            }
            // 6e-G7 S1：泛型定义在枚举后、类/函数前注册——gcls 条目须先于引用 !开放参数 的 fn 条目落盘
            foreach (var g in program.GenericDefinitions)
            {
                registry.RegisterType(g);
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

            // 6e-G7 S2：泛型定义方法的开放绑定体同样收集（显式清单，不触碰 stdlib 注入体）
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                CollectBody(registry, pair.Key, pair.Value, labels);
                labelsByFunction[pair.Key] = labels;
            }

            // 鍏ㄩ儴绗﹀彿鏀堕泦瀹屾瘯鍚庡啀瀹氬悕锛堝彉閲忛敭闇€瑕佸嚱鏁伴敭锛屼笖瑕佽法绗﹀彿娑堥噸锛?
            registry.Seal();

            var buffer = new StringWriter();
            var w = new Writer(buffer);
            w.Open("cod");
            w.Field(Magic);
            w.Field(Version);

            // 绗﹀彿琛紙鎸夋敞鍐屽簭锛?
            w.Open("symbols");
            foreach (var emitter in registry.Emitters)
            {
                emitter(w, registry);
            }
            w.End();

            // 鍑芥暟浣?
            w.Open("bodies");
            foreach (var fn in program.Functions)
            {
                // 6e-G7 S2：泛型定义属主的方法体（开放绑定体）随库携带；其余实例方法不在容器序列化范围，跳过
                if (fn.ContainingClass != null && !fn.IsStatic && !fn.ContainingClass.IsGenericDefinition)
                {
                    continue;
                }

                if (!program.Bodies.TryGetValue(fn, out var body))
                {
                    continue;
                }

                WriteBodyEntry(w, registry, labelsByFunction, fn, body);
            }

            // 6e-G7 S2：开放绑定体（泛型定义方法）——显式遍历，避免卷入 stdlib 注入体
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                WriteBodyEntry(w, registry, labelsByFunction, pair.Key, pair.Value);
            }
            w.End();

            // 渚濊禆娓呭崟
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
            buffer.WriteLine();

            // 瀹屾暣鎬ф牎楠岋細瀵规鏂囧叏閮ㄥ瓧鑺傦紙UTF-8锛夊彇 SHA256锛岃拷鍔犱负鏂囦欢鏈锛堣渚у己鍒舵牎楠岋紝缂哄け/涓嶇鎷掕浇锛?
            var payload = buffer.ToString();
            writer.Write(payload);
            writer.WriteLine("(checksum " + ChecksumTag + ComputeChecksum(payload) + ")");
        }

        private static string ComputeChecksum(string payload)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
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
                case BoundNodeKind.ByRefArgument:
                    {
                        var n = (BoundByRefArgument)expression;
                        CollectExpression(registry, owner, n.Expression, labels);
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
                    // 璋冭瘯淇℃伅闄嶇骇锛氫粎搴忓垪鍖栧唴灞傝澶?
                    WriteStatement(w, registry, labels, ((BoundSequencePointStatement)statement).Statement);
                    break;
                default:
                    // 6e-G7 S2：杜绝静默产出损坏流——未覆盖节点显式失败
                    throw new NotSupportedException($"[cod] Unserializable statement kind '{statement.Kind}'");
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
                case BoundNodeKind.ByRefArgument:
                    {
                        var n = (BoundByRefArgument)expression;
                        w.Open("byrefarg");
                        w.Field(n.IsRef ? "ref" : "out");
                        WriteExpression(w, registry, labels, n.Expression);
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
                        // 6e-G7：字段访问随 gcls/fld 携带（Field 经 FnKey 式名字回填）；仅数组/字符串 `.Length` 时 Field == null
                        var n = (BoundMemberAccessExpression)expression;
                        w.Open("memberacc");
                        w.Field(TypeRef(n.Type));
                        w.Field(Str(n.Identifier));
                        if (n.Field != null)
                        {
                            w.Field("owner:" + n.Field.ContainingClass.FullName);
                        }

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
                case BoundNodeKind.MemberAssignmentExpression:
                    {
                        // 6e-G7 S2：字段赋值（开放体携带）：target 表达式 + 字段名/类型/静态位 + 值
                        var n = (BoundMemberAssignmentExpression)expression;
                        w.Open("memberassign");
                        WriteExpression(w, registry, labels, n.Target);
                        w.Field("name:" + Str(n.Field.Name));
                        w.Field(TypeRef(n.Field.Type));
                        w.Field(BoolWord(n.Field.IsStatic));
                        WriteExpression(w, registry, labels, n.Expression);
                        w.End();
                        break;
                    }
                default:
                    throw new NotSupportedException($"[cod] Unserializable expression kind '{expression.Kind}'（开放体节点覆盖缺口）");
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

        /// <summary>6e-M19 M2-c锛氬唴寤哄崟渚嬶紙System.Object/System.Type锛夋寜鍏ㄥ悕搴忓垪鍖栵紝璇讳晶鏄犲皠鍥炲崟渚嬨€?/summary>
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
            // 搴忓垪鍖栧叏閮ㄩ潤鎬佹柟娉曠鍚嶏紙6e-M18锛氬鍣ㄧ被鍏佽甯︿綋闈欐€佹柟娉曪紝濡?Console.WriteLine/Math.Max锛泂yscall/extern 浜︿负闈欐€侊級銆?
            // 鏂规硶鏈綋鐢卞悇鑷?fn 鏉＄洰鎼哄甫锛坥wner 瀛楁鍥炲～绫诲綊灞烇級锛岃繖閲屽垪 Name[鍙傛暟绫诲瀷] 渚涢槄璇伙紙鏃犲弬鐪佺暐鏂规嫭鍙凤級銆?
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(MethodSignature(method));
            }
            w.End();
        }

        /// <summary>鏂规硶绛惧悕鐭敭锛歂ame 鎴?Name[鍙傛暟绫诲瀷鍒楄〃]锛堥噸杞介潬鍙傛暟绫诲瀷鍖哄垎锛夈€?/summary>
        private static string MethodSignature(FunctionSymbol method)
        {
            // 6e-M23 R8锛氫粎宸?out/ref 鐨勯噸杞介敭椤讳笉鍚岋紙淇グ绗﹀叆绛惧悕锛?
            return method.Parameters.Length == 0
                ? method.Name
                : method.Name + "[" + string.Join(",", method.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type))) + "]";
        }

        /// <summary>
        /// 泛型定义类节点（6e-G7 S1）：类型参数（含约束）+ 字段 + 静态方法签名。
        /// 成员类型经 TypeRef 携带开放参数（!属主.名）与实例化 mangle；开放绑定体由 bodies 区按 FnKey 携带（S2）。
        /// </summary>
        private static void EmitGenericClassSymbol(Writer w, Registry registry, ClassTypeSymbol classType)
        {
            System.Console.Error.WriteLine("[G7] gcls " + classType.FullName + " methods=[" +
                string.Join(",", classType.Methods.Select(m => m.Name + (m.IsStatic ? "(s)" : m.IsConstructor ? "(ctor)" : "(i)"))) + "]" +
                " fns=" + string.Join(",", ((IEnumerable<object>)classType.Methods).Count()));
            w.Open("gcls");
            w.Field(classType.FullName);
            w.Field(classType.Visibility.ToString().ToLowerInvariant());

            var typeParameters = classType.TypeParameters;
            w.Field("tparams:" + typeParameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var typeParameter in typeParameters)
            {
                WriteTypeParameter(w, typeParameter);
            }

            var fields = classType.Fields.ToArray();
            w.Field("fields:" + fields.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var field in fields)
            {
                w.Open("fld");
                w.Field(Str(field.Name));
                w.Field(TypeRef(field.Type));
                w.Field(field.Visibility.ToString().ToLowerInvariant());
                w.Field(BoolWord(field.IsStatic));
                w.Field(BoolWord(field.IsReadonly));
                w.End();
            }

            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(MethodSignature(method));
            }

            w.End();
        }

        /// <summary>tpar/ftp 子节点共用写出（6e-G7 S1）：名 / 序号 / 约束标志 / 显式约束类型列表。</summary>
        private static void WriteTypeParameter(Writer w, TypeParameterSymbol typeParameter)
        {
            w.Open("tpar");
            w.Field(typeParameter.Name);
            w.Field(typeParameter.Ordinal);
            var flags = new List<string>();
            if (typeParameter.HasNewConstraint)
            {
                flags.Add("new");
            }

            if (typeParameter.HasReferenceTypeConstraint)
            {
                flags.Add("class");
            }

            if (typeParameter.HasValueTypeConstraint)
            {
                flags.Add("struct");
            }

            w.Field(flags.Count == 0 ? "-" : string.Join("+", flags));
            w.Field("c:" + typeParameter.ConstraintTypes.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                w.Field(TypeRef(constraint));
            }

            w.End();
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

        /// <summary>
        /// tpar/ftp 子节点读取（6e-G7 S1）：构造符号 + 应用标志 + 登记开放键（类级=限定键 !属主.名；
        /// 方法级=裸键 !名）+ 暂存约束数。返回 (参数, 约束数)，约束由第二趟解析。
        /// </summary>
        private static (TypeParameterSymbol Parameter, int ConstraintCount) ReadTypeParameter(Reader reader, ReadContext context, string? ownerFullName)
        {
            reader.Expect("tpar");
            var parameterName = Unescape(reader.ExpectString());
            var ordinal = reader.ExpectInt();
            var flagsText = reader.ExpectString();
            var constraintCount = ReadCountField(reader, "c:");

            var parameter = new TypeParameterSymbol(parameterName, ordinal, owningClass: null);
            ApplyTypeParameterFlags(parameter, flagsText);

            var openKey = ownerFullName == null
                ? "!" + parameterName
                : "!" + ownerFullName + "." + parameterName;
            context.OpenTypeParametersByKey[openKey] = parameter;

            return (parameter, constraintCount);
        }

        /// <summary>约束第二趟：兄弟参数已全部注册后解析显式约束类型。</summary>
        private static void ResolveDeferredConstraints(Reader reader, TypeParameterSymbol parameter, int constraintCount, ReadContext context)
        {
            if (constraintCount == 0)
            {
                reader.End();
                return;
            }

            var constraints = ImmutableArray.CreateBuilder<TypeSymbol>(constraintCount);
            for (var c = 0; c < constraintCount; c++)
            {
                constraints.Add(ResolveTypeRef(reader.ExpectString(), context));
            }

            parameter.ConstraintTypes = constraints.ToImmutable();
            reader.End();
        }

        private static void EmitFunctionSymbol(Writer w, Registry registry, FunctionSymbol fn)
        {
            w.Open("fn");
            w.Field(registry.FnKey(fn));
            w.Field("name:" + Str(fn.Name));

            // 6e-G7 S1：方法级类型参数（顶层泛型函数）——裸键 !名（无属主类）
            if (fn.TypeParameters.Length > 0)
            {
                w.Field("tps:" + fn.TypeParameters.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var typeParameter in fn.TypeParameters)
                {
                    WriteTypeParameter(w, typeParameter);
                }
            }

            w.Field("ret:" + TypeRef(fn.ReturnType));
            w.Field("ns:" + (fn.Namespace.Length > 0 ? Str(fn.Namespace) : "-"));
            w.Field("owner:" + (fn.ContainingClass != null ? fn.ContainingClass.FullName : "-"));
            w.Field("extern:" + BoolWord(fn.IsExtern));
            w.Field("dll:" + (fn.DllName != null ? Str(fn.DllName) : "-"));
            w.Field("cc:" + fn.CallingConvention.ToString().ToLowerInvariant());
            w.Field("builtin:" + (fn.BuiltinKind != null ? fn.BuiltinKind.Value.ToString() : "-"));
            w.Field("entry:" + (fn.EntryPoint != null ? Str(fn.EntryPoint) : "-"));
            w.Field("charset:" + (fn.CharSet != null ? fn.CharSet.Value.ToString().ToLowerInvariant() : "-"));

            // 6e-G7 S2：泛型定义属主的方法携带静态位（容器类全静态隐含 true；顶层函数恒 static；gcls 实例方法需显式 false）
            if (fn.ContainingClass is { IsGenericDefinition: true })
            {
                w.Field("static:" + BoolWord(fn.IsStatic));
                w.Field("ctor:" + BoolWord(fn.IsConstructor));
            }

            w.Field("params:" + fn.Parameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var p in fn.Parameters)
            {
                w.Open("par");
                w.Field(registry.VarKey(p));
                w.Field(Str(p.Name));
                w.Field(TypeRef(p.Type));
                w.Field(p.Ordinal);
                w.Field(p.IsOut ? "out" : p.IsRef ? "ref" : "-");
                w.Field(p.IsThisParameter ? "this" : "-");
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

        /// <summary>绫诲瀷鐨勬枃鏈紩鐢細鍐呭缓/鏁扮粍鐢ㄧ煭鍚嶏紙int / int[][]锛夛紝绫?鏋氫妇鐢ㄥ叏鍚嶃€?/summary>
        private static string TypeRef(TypeSymbol type)
        {
            // 6e-G7 S1：开放类型参数 → 限定权威键 `!属主全名.参数名`（方法级无属主回落裸名）；
            // 实例化类型 → Encode v3 完整 mangle（backtick 元数 + # + $ 分隔递归实参）
            if (type is TypeParameterSymbol openParameter)
            {
                return openParameter.OwningClass != null
                    ? "!" + openParameter.OwningClass.FullName + "." + openParameter.Name
                    : "!" + openParameter.Name;
            }

            if (type is InstantiatedTypeSymbol instantiated)
            {
                return EncodeInstantiatedTypeRef(instantiated);
            }

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

        /// <summary>
        /// 实例化类型的 .cod 编码（6e-G7 S1）：定义全名 + backtick 元数 + # + $ 分隔实参。
        /// 实参递归走 <see cref="TypeRef"/>——开放参数为限定键 !属主.名（区别于 mangle 缓存键的裸 !T），
        /// 保证跨定义无歧义且读侧可独立解析；基元/类用平名（不含 $、`、#，分隔安全）；嵌套实例化递归。
        /// </summary>
        private static string EncodeInstantiatedTypeRef(InstantiatedTypeSymbol instantiated)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(instantiated.GenericDefinition.FullName);
            builder.Append('`');
            builder.Append(instantiated.TypeArguments.Length);
            builder.Append('#');

            for (var i = 0; i < instantiated.TypeArguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('$');
                }

                builder.Append(TypeRef(instantiated.TypeArguments[i]));
            }

            return builder.ToString();
        }

        /// <summary>6e-G7 S2：单条 body 条目（FnKey + 语句块）。</summary>
        /// <summary>6e-M26：泛型开放绑定体确定性排序键（GenericOpenBodies 为 ImmutableDictionary，枚举不稳定）。</summary>
        private static string GenericOpenSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }

        private static void WriteBodyEntry(Writer w, Registry registry, Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>> labelsByFunction, FunctionSymbol fn, BoundBlockStatement body)
        {
            w.Open("body");
            w.Field(registry.FnKey(fn));
            WriteStatement(w, registry, labelsByFunction[fn], body);
            w.End();
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
                case null: return "n:"; // 6e-M19 M5-a锛歯ull 甯搁噺
                case int i: return "i:" + i.ToString(CultureInfo.InvariantCulture);
                case long l: return "l:" + l.ToString(CultureInfo.InvariantCulture); // 6e-M23 R8锛歩64 甯搁噺
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
                case 'n': return null!; // 6e-M19 M5-a锛歯ull 甯搁噺
                case 'i': return int.Parse(rest, CultureInfo.InvariantCulture);
                case 'l': return long.Parse(rest, CultureInfo.InvariantCulture); // 6e-M23 R8锛歩64 甯搁噺
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
            private readonly List<bool> _hasChild = new();
            private int _depth;
            private bool _lineStart = true;

            public Writer(TextWriter writer)
            {
                _w = writer;
            }

            public void Open(string kind)
            {
                if (_hasChild.Count > 0)
                {
                    // 鏍囪鐖惰妭鐐瑰惈瀛愯妭鐐癸細鍏堕棴鎷彿鎹㈣缂╄繘锛岃€岄潪琛屽唴闂悎
                    _hasChild[_hasChild.Count - 1] = true;
                }

                Indent();
                _w.Write('(');
                _w.Write(kind);
                _lineStart = false;
                _hasChild.Add(false);
                _depth++;
            }

            public void Field(object value)
            {
                _w.Write(' ');
                _w.Write(value);
                _lineStart = false;
            }

            public void End()
            {
                var hasChild = _hasChild[_hasChild.Count - 1];
                _hasChild.RemoveAt(_hasChild.Count - 1);
                _depth--;

                if (hasChild && !_lineStart)
                {
                    // 澶氳鑺傜偣锛氬厛鍥炲埌琛岄锛岄棴鎷彿涓庡紑鎷彿鍚屽垪
                    _w.WriteLine();
                    _w.Write(new string(' ', _depth * 2));
                }

                // 琛屽唴闂悎锛堟棤瀛愯妭鐐癸級鎴栧畾浣嶅悗闂悎鍧囦笉涓诲姩鎹㈣鈥斺€旂敱涓嬩竴娆?Open/Field/End 鎸夐渶瀹氫綅
                _w.Write(')');
                _lineStart = false;
            }

            /// <summary>瀛愯妭鐐瑰紑鎷彿鍓嶅畾浣嶅埌涓嬩竴琛岀缉杩涘垪锛堝凡鍦ㄨ棣栧垯涓嶅啀鎹㈣锛夈€?/summary>
            private void Indent()
            {
                if (_depth == 0)
                {
                    return;
                }

                if (!_lineStart)
                {
                    _w.WriteLine();
                }

                _w.Write(new string(' ', _depth * 2));
                _lineStart = true;
            }
        }

        /// <summary>鍐欎晶绗﹀彿娉ㄥ唽琛細鍘婚噸 + 鍙戝皠椤哄簭锛坕d 浠呯敤浜庢帓搴忥紝涓嶅啓鍏ユ枃浠讹級銆?/summary>
        private sealed class Registry
        {
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            private readonly List<FunctionSymbol> _functions = new();
            private readonly List<(VariableSymbol Symbol, FunctionSymbol? Owner)> _variables = new();
            private readonly Dictionary<FunctionSymbol, string> _fnKeys = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, string> _varKeys = new(ReferenceEqualityComparer.Instance);

            public List<Action<Writer, Registry>> Emitters { get; } = new();

            public string FnKey(FunctionSymbol fn)
        {
            // 6e-G7：开放体携带后，部分符号（如 cod 注入链上的实例化副本）不经 RegisterFunction——
            // 缺键时回退动态计算（公式与 Seal 一致），读写两侧对称即自洽
            return _fnKeys.TryGetValue(fn, out var key) ? key : ComputeFnKey(fn);
        }

            public string VarKey(VariableSymbol v) => _varKeys[v];

            public void RegisterType(TypeSymbol type)
            {
                if (_ids.ContainsKey(type))
                {
                    return;
                }

                // 6e-G7 S1：开放类型参数自描述（gcls 内 tpar 声明 + !属主.名 引用），无独立条目
                if (type is TypeParameterSymbol)
                {
                    return;
                }

                // 6e-G7 S1：实例化类型 → 注册泛型定义与全部实参（依赖先行）；本体无独立条目（引用处 mangle 自描述）
                if (type is InstantiatedTypeSymbol instantiated)
                {
                    _ids[type] = _ids.Count;
                    RegisterType(instantiated.GenericDefinition);
                    foreach (var argument in instantiated.TypeArguments)
                    {
                        RegisterType(argument);
                    }

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
                // 鍏朵綑锛堝唴寤?鏁扮粍锛夎嚜鎻忚堪锛屾棤闇€鐙珛鏉＄洰
            }

            private void RegisterClassCore(ClassTypeSymbol classType)
            {
                // 6e-M19 M2-c锛氬唴寤哄崟渚嬶紙System.Object/System.Type锛変笉鍙?cls鈥斺€旇渚т細閫犲嚭鏂扮被鐮村潖鍗曚緥鍚屼竴鎬э紱
                // 鍙?systype 鎸夊叏鍚嶆槧灏勫洖鍗曚緥锛堟垚鍛橀潰鐢?Ensure 鍐呭缓娉ㄥ叆锛屼笉搴忓垪鍖栵級
                if (SystemObjectMembers.IsBuiltinSystemClass(classType))
                {
                    Emitters.Add((w, r) => EmitBuiltinSystemClass(w, r, classType));
                    return;
                }

                // 6e-G7 S1：泛型定义走 gcls 专属节点；gcls 必须先于其静态方法 fn 落盘
                // （fn 的 ret/par 引用 !开放参数，读侧需先经 gcls 注册限定键）；连带注册非开放类型依赖
                if (classType.IsGenericDefinition)
                {
                    Emitters.Add((w, r) => EmitGenericClassSymbol(w, r, classType));

                    foreach (var typeParameter in classType.TypeParameters)
                    {
                        foreach (var constraint in typeParameter.ConstraintTypes)
                        {
                            RegisterType(constraint);
                        }
                    }

                    foreach (var field in classType.Fields)
                    {
                        RegisterType(field.Type);
                    }

                    foreach (var method in classType.Methods.Where(m => m.IsStatic))
                    {
                        RegisterFunction(method);
                    }

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

                // 绫绘柟娉曪細瀹瑰櫒绫诲叏闈欐€侊紙syscall/extern 鍙婂甫浣撻潤鎬佹柟娉曪紝6e-M18锛変綔涓虹嫭绔?fn 搴忓垪鍖栵紱瀹炰緥鏂规硶/鏋勯€犵敱绫诲３杩囨护銆?
                // 渚嬪锛歄bject 鍐呭缓鏂规硶锛圡2-c锛夊甫 BuiltinKind锛岃渚х粡鍗曚緥澶嶇敤閲嶅缓锛岄』闅忓紩鐢ㄥ簭鍒楀寲
                // 6e-G7 S1/S2：泛型定义的实例方法/构造也随库携带（消费方单态化素材）；其余实例方法仍由类壳过滤
                if (fn.ContainingClass != null && !fn.IsStatic &&
                    !SystemObjectMembers.IsBuiltinSystemClass(fn.ContainingClass) &&
                    !fn.ContainingClass.IsGenericDefinition)
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

            /// <summary>鏀堕泦瀹屾垚鍚庣粺涓€鍛藉悕锛氬嚱鏁伴敭涓庡彉閲忛敭锛堝叏灞€ global:鍚嶅瓧锛涘眬閮?鍙傛暟 鍑芥暟閿?鍚嶅瓧锛涘啿绐佸姞 #2/#3锛夈€?/summary>
            /// <summary>FnKey 计算（6e-G7 抽取）：owner/ns 前缀 + 名 + [参数类型]；仅差 out/ref 的重载键不同。</summary>
            private static string ComputeFnKey(FunctionSymbol fn)
            {
                var paramTypes = string.Join(",", fn.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type)));
                var head = fn.ContainingClass != null
                    ? fn.ContainingClass.FullName + "." + fn.Name
                    : fn.Namespace.Length > 0 ? fn.Namespace + "." + fn.Name : fn.Name;
                return head + "[" + paramTypes + "]";
            }

            public void Seal()
            {
                foreach (var fn in _functions)
                {
                    _fnKeys[fn] = ComputeFnKey(fn);
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

        /// <summary>浠?`.cod` 鏂囦欢鍔犺浇绋嬪簭闆嗐€?/summary>
        public static CodProgram Load(string path)
        {
            return Read(File.ReadAllText(path));
        }

        public static CodProgram Read(string text)
        {
            // 瀹屾暣鎬ф牎楠屽墠缃細缂哄け鎴栦笉鍖归厤鍗虫嫆杞斤紙闃茶鏀?鎹熷潖锛涜搫鎰忎吉閫犻渶绛惧悕鏈哄埗锛屼笉鍦?v1 鑼冨洿锛?
            var marker = "(checksum " + ChecksumTag;
            var markerIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                throw new InvalidDataException(".cod checksum missing (expected '(checksum sha256:<hex>)' as the last line); rebuild the library");
            }

            var payload = text.Substring(0, markerIndex);
            var provided = text.Substring(markerIndex + marker.Length).TrimEnd();
            if (!provided.EndsWith(")"))
            {
                throw new InvalidDataException(".cod checksum malformed (expected '(checksum sha256:<hex>)' as the last line)");
            }

            provided = provided.Substring(0, provided.Length - 1);
            var actual = ComputeChecksum(payload);
            if (!string.Equals(provided, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($".cod checksum mismatch: library corrupted or modified (expected {actual}, got {provided})");
            }

            var tokens = Tokenize(payload).ToArray();
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
                namespaces.ToImmutable(),
                context.GenericDefinitions.ToImmutable());
        }

        /// <summary>璇讳晶鍏变韩鐘舵€侊細鎸夊悕瀛?閿储寮曠殑绗﹀彿琛?+ 绋嬪簭闆嗙鍙锋竻鍗曘€?/summary>
        private sealed class ReadContext
        {
            /// <summary>绫?鏋氫妇鍏ㄥ悕 鈫?绫诲瀷绗﹀彿锛堝唴寤虹被鍨嬩笉缁忔琛紝鐩存帴瑙ｆ瀽锛夈€?/summary>
            public Dictionary<string, TypeSymbol> TypesByName { get; } = new(StringComparer.Ordinal);

            /// <summary>6e-G7 S1：开放类型参数限定键（!属主全名.参数名）→ 符号。文件级平铺——限定键天然无碰撞。</summary>
            public Dictionary<string, TypeParameterSymbol> OpenTypeParametersByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>鍑芥暟閿?鈫?鍑芥暟绗﹀彿銆?/summary>
            public Dictionary<string, FunctionSymbol> FunctionsByKey { get; } = new(StringComparer.Ordinal);

            /// <summary>鍙橀噺閿?鈫?鍙橀噺/鍙傛暟绗﹀彿銆?/summary>
            public Dictionary<string, VariableSymbol> VariablesByKey { get; } = new(StringComparer.Ordinal);

            public ImmutableArray<FunctionSymbol>.Builder Functions { get; } = ImmutableArray.CreateBuilder<FunctionSymbol>();

            public ImmutableArray<GlobalVariableSymbol>.Builder Globals { get; } = ImmutableArray.CreateBuilder<GlobalVariableSymbol>();

            public ImmutableArray<EnumTypeSymbol>.Builder Enums { get; } = ImmutableArray.CreateBuilder<EnumTypeSymbol>();

            public ImmutableArray<ClassTypeSymbol>.Builder Classes { get; } = ImmutableArray.CreateBuilder<ClassTypeSymbol>();

            /// <summary>6e-G7 S1：泛型定义类（gcls 读入）。</summary>
            public ImmutableArray<ClassTypeSymbol>.Builder GenericDefinitions { get; } = ImmutableArray.CreateBuilder<ClassTypeSymbol>();

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
                    case "gcls":
                        ReadGenericClass(reader, context);
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
            // 6e-M19 M2-c锛氬唴寤哄崟渚嬫寜鍏ㄥ悕鏄犲皠锛堟垚鍛橀潰宸茬敱 Ensure 鍐呭缓娉ㄥ叆锛?
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
            // 鏂规硶鍚嶄粎渚涢槄璇伙紝鏂规硶绗﹀彿鐢卞悇 fn 鏉＄洰鐨?owner 瀛楁鍥炲～
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectString();
            }

            var classType = new ClassTypeSymbol(name, ns, visibility, declaration: null);
            // 6e-M19 M2-c锛?cod 绫婚粯璁ょ户鎵?System.Object锛堜笌婧愮爜缁戝畾涓€鑷达紱.cod v1 涓嶅簭鍒楀寲鎺ュ彛澹版槑锛?
            classType.BaseType = ClassTypeSymbol.SystemObject;
            context.Classes.Add(classType);
            context.GenericDefinitions.Add(classType);
            context.AddNamedType(fullName, classType);
            reader.End();
        }

        /// <summary>
        /// 泛型定义类读取（6e-G7 S1）：重建 IsGenericDefinition 壳 + 类型参数（含约束，两趟——约束可引用兄弟参数）+
        /// 字段；静态方法签名仅作清单，符号由各自 fn 条目 owner 回填。
        /// 开放类型参数按限定键 `!属主全名.名` 注册进文件级表，后续 fn/bodies 的类型引用据此解析。
        /// </summary>
        private static void ReadGenericClass(Reader reader, ReadContext context)
        {
            var fullName = reader.ExpectString();
            var (ns, name) = SplitFullName(fullName);
            var visibilityText = reader.ExpectString();
            if (!Enum.TryParse<Visibility>(visibilityText, ignoreCase: true, out var visibility))
            {
                throw new InvalidDataException($"Unknown visibility '{visibilityText}' on generic class '{fullName}'");
            }

            var typeParameterCount = ReadCountField(reader, "tparams:");
            var classType = new ClassTypeSymbol(name, ns, visibility, declaration: null);
            classType.BaseType = ClassTypeSymbol.SystemObject;

            var pendingConstraints = new (TypeParameterSymbol Parameter, int Count)[typeParameterCount];
            for (var i = 0; i < typeParameterCount; i++)
            {
                reader.Expect("tpar");
                var parameterName = Unescape(reader.ExpectString());
                var ordinal = reader.ExpectInt();
                var flagsText = reader.ExpectString();
                var constraintCount = ReadCountField(reader, "c:");

                var parameter = new TypeParameterSymbol(parameterName, ordinal, classType);
                if (flagsText != "-")
                {
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
                                throw new InvalidDataException($"Unknown type parameter constraint flag '{flag}' on '{fullName}.{parameterName}'");
                        }
                    }
                }

                classType.TypeParameters = classType.TypeParameters.Add(parameter);
                context.OpenTypeParametersByKey["!" + fullName + "." + parameterName] = parameter;
                pendingConstraints[i] = (parameter, constraintCount);
            }

            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
            for (var i = 0; i < typeParameterCount; i++)
            {
                var (parameter, constraintCount) = pendingConstraints[i];
                if (constraintCount == 0)
                {
                    reader.End();
                    continue;
                }

                var constraints = ImmutableArray.CreateBuilder<TypeSymbol>(constraintCount);
                for (var c = 0; c < constraintCount; c++)
                {
                    constraints.Add(ResolveTypeRef(reader.ExpectString(), context));
                }

                parameter.ConstraintTypes = constraints.ToImmutable();
                reader.End();
            }

            var fieldCount = ReadCountField(reader, "fields:");
            for (var i = 0; i < fieldCount; i++)
            {
                reader.Expect("fld");
                var fieldName = Unescape(reader.ExpectString());
                var fieldType = ResolveTypeRef(reader.ExpectString(), context);
                var fieldVisibilityText = reader.ExpectString();
                if (!Enum.TryParse<Visibility>(fieldVisibilityText, ignoreCase: true, out var fieldVisibility))
                {
                    throw new InvalidDataException($"Unknown visibility '{fieldVisibilityText}' on field '{fullName}.{fieldName}'");
                }

                var isStatic = ParseBoolWord(reader.ExpectString());
                var isReadonly = ParseBoolWord(reader.ExpectString());
                classType.AddField(new FieldSymbol(fieldName, fieldType, fieldVisibility, classType, isReadonly, isStatic));
                reader.End();
            }

            var methodCount = ReadCountField(reader, "methods:");
            // 方法名仅供阅读，方法符号由各自 fn 条目的 owner 字段回填
            for (var i = 0; i < methodCount; i++)
            {
                reader.ExpectString();
            }

            context.Classes.Add(classType);
            context.GenericDefinitions.Add(classType);
            context.AddNamedType(fullName, classType);
            reader.End();
        }

        private static void ReadFunction(Reader reader, ReadContext context)
        {
            var key = reader.ExpectString();
            var name = ReadLabeledField(reader, "name:");

            // 6e-G7 S1：方法级类型参数（顶层泛型函数，裸键 !名）——先注册再解析 ret/par 的类型引用
            var typeParameters = ImmutableArray<TypeParameterSymbol>.Empty;
            if (reader.PeekRaw().StartsWith("tps:", StringComparison.Ordinal))
            {
                var tpsHeader = reader.ExpectString();
                if (!int.TryParse(tpsHeader.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tpsCount))
                {
                    throw new InvalidDataException($"Malformed 'tps:' count '{tpsHeader}' on function '{name}'");
                }

                var builder = ImmutableArray.CreateBuilder<TypeParameterSymbol>(tpsCount);
                var deferred = new List<(TypeParameterSymbol Parameter, int ConstraintCount)>(tpsCount);
                for (var i = 0; i < tpsCount; i++)
                {
                    var (parameter, constraintCount) = ReadTypeParameter(reader, context, ownerFullName: null);
                    builder.Add(parameter);
                    deferred.Add((parameter, constraintCount));
                }

                foreach (var (parameter, constraintCount) in deferred)
                {
                    ResolveDeferredConstraints(reader, parameter, constraintCount, context);
                }

                typeParameters = builder.ToImmutable();
            }

            var returnType = ResolveTypeRef(ReadLabeledField(reader, "ret:"), context);
            var nsText = ReadLabeledField(reader, "ns:");
            var ownerText = ReadLabeledField(reader, "owner:");
            var isExtern = ParseBoolWord(ReadLabeledField(reader, "extern:"));
            var dllText = ReadLabeledField(reader, "dll:");
            var ccText = ReadLabeledField(reader, "cc:");
            var builtinText = ReadLabeledField(reader, "builtin:");
            var entryText = ReadLabeledField(reader, "entry:");
            var charSetText = ReadLabeledField(reader, "charset:");

            // 6e-G7 S2：泛型属主方法的显式静态/构造位（旧文件无此字段，按默认推断）
            bool? explicitIsStatic = null;
            var explicitIsConstructor = false;
            if (reader.PeekRaw().StartsWith("static:", StringComparison.Ordinal))
            {
                explicitIsStatic = ParseBoolWord(ReadLabeledField(reader, "static:"));
                explicitIsConstructor = ParseBoolWord(ReadLabeledField(reader, "ctor:"));
            }


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

                // 6e-M23 R8锛氱 5 涓?token = out/ref/-锛堟棫鏂囦欢鏃犳 token锛屾寜 "-" 鍏煎锛?
                var isOut = false;
                var isRef = false;
                var modifierText = reader.PeekRaw();
                if (modifierText is "out" or "ref" or "-")
                {
                    reader.ExpectString();
                    isOut = modifierText == "out";
                    isRef = modifierText == "ref";
                }

                var isThis = false;
                var thisText = reader.PeekRaw();
                if (thisText is "this" or "-")
                {
                    reader.ExpectString();
                    isThis = thisText == "this";
                }

                var parameter = new ParameterSymbol(pName, pType, ordinal, isOut, isRef, isThis);
                parameters.Add(parameter);
                context.VariablesByKey[pKey] = parameter;
                reader.End();
            }

            // 6e-M19 M2-c锛歄bject 鍐呭缓鏂规硶澶嶇敤鍗曚緥锛堜繚鎸佺鍙峰悓涓€鎬э紝鍙戝皠鍣ㄦ寜 BuiltinKind 鍒嗗彂锛?
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

            // 鍚被褰掑睘鎴栧唴缃绫伙細涓嶅鐢ㄥ叏灞€鍗曚緥锛堝唴缃崟渚嬫棤绫诲綊灞烇級锛岄噸寤哄甫涓婁笅鏂囩鍙?
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

            // 6e-G7 S1：方法级类型参数回填（顶层泛型函数）
            if (typeParameters.Length > 0)
            {
                function.TypeParameters = typeParameters;
            }

            // 绫绘柟娉曞洖濉細鍚被褰掑睘鐨?fn 褰掑叆鍏剁被锛?e-M18锛氬鍣ㄧ被鍏ㄩ潤鎬佲€斺€攕yscall/extern 鍙婂甫浣撻潤鎬佹柟娉曪級銆?
            // 鍐呭缓鍗曚緥锛圫ystem.Object/System.Type锛孧2-c锛夋垚鍛樺凡鐢?Ensure 娉ㄥ叆锛岃烦杩囧洖濉槻閲嶅/闃茶鏍?static
            if (containingClass != null && !SystemObjectMembers.IsBuiltinSystemClass(containingClass))
            {
                // 6e-G7 S2：泛型定义属主按显式位还原（实例方法 false / .ctor true）；容器类隐含全静态
                function.IsStatic = explicitIsStatic ?? true;
                if (explicitIsStatic.HasValue)
                {
                    function.IsConstructor = explicitIsConstructor;
                }

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

                // extern 鍑芥暟鏃犲疄鐜帮細绌?body锛堜笌 Binder.BindProgram 涓€鑷达級
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

            // 6e-G7 S1：开放类型参数限定键（!属主.名）或基元权威编码（!System.Int32 等，实例化实参位置出现）
            if (name.StartsWith("!", StringComparison.Ordinal))
            {
                if (context.OpenTypeParametersByKey.TryGetValue(name, out var openParameter))
                {
                    return openParameter;
                }

                if (GenericTypeInstantiator.TryDecodePrimitive(name, out var primitive))
                {
                    return primitive;
                }

                throw new InvalidDataException($"Unknown open type parameter '{name}'");
            }

            // 6e-G7 S1：实例化类型 mangle（backtick 元数 + # + $ 分隔递归实参）
            if (name.Contains('`') && name.Contains('#'))
            {
                return ParseInstantiatedTypeRef(name, context);
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

        /// <summary>
        /// 实例化类型 mangle 递归解析（6e-G7 S1）：`定义全名\`N#实参1$...$实参N`，
        /// 按 arity 递归消费（嵌套实例化的内层 $ 归属内层分组）；叶子经
        /// !开放参数/!基元反解或既有名字解析；`[]` 后缀按数组还原。
        /// </summary>
        private static TypeSymbol ParseInstantiatedTypeRef(string text, ReadContext context)
        {
            var position = 0;
            var type = ParseEncodedType(text, ref position, context);
            if (position != text.Length)
            {
                throw new InvalidDataException($"Trailing characters in instantiated type '{text}'");
            }

            return type;
        }

        private static TypeSymbol ParseEncodedType(string text, ref int position, ReadContext context)
        {
            // ! 前缀：开放类型参数限定键 / 基元权威编码
            if (position < text.Length && text[position] == '!')
            {
                var start = position;
                position++;
                while (position < text.Length && IsEncodedNameChar(text[position]))
                {
                    position++;
                }

                var key = text.Substring(start, position - start);
                if (context.OpenTypeParametersByKey.TryGetValue(key, out var openParameter))
                {
                    return ConsumeArraySuffixes(key, openParameter, text, ref position);
                }

                if (GenericTypeInstantiator.TryDecodePrimitive(key, out var primitive))
                {
                    return ConsumeArraySuffixes(key, primitive, text, ref position);
                }

                throw new InvalidDataException($"Unknown encoded type '{key}' in '{text}'");
            }

            // 名字段：字母数字._ （实例化头在此处截断于 backtick）
            var nameStart = position;
            while (position < text.Length && IsEncodedNameChar(text[position]))
            {
                position++;
            }

            var fullName = text.Substring(nameStart, position - nameStart);

            // 实例化：backtick 元数 + # + N 个递归实参（$ 分隔）
            if (position < text.Length && text[position] == '`')
            {
                position++;
                var arityStart = position;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                {
                    position++;
                }

                if (!int.TryParse(text.Substring(arityStart, position - arityStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var arity) ||
                    posAt(text, position) != '#')
                {
                    throw new InvalidDataException($"Malformed instantiation arity in '{text}'");
                }

                position++; // skip '#'
                if (!context.TypesByName.TryGetValue(fullName, out var definitionObject) ||
                    definitionObject is not ClassTypeSymbol definition ||
                    !definition.IsGenericDefinition ||
                    definition.TypeParameters.Length != arity)
                {
                    throw new InvalidDataException($"Unknown generic definition or arity mismatch '{fullName}`{arity}' in '{text}'");
                }

                var arguments = ImmutableArray.CreateBuilder<TypeSymbol>(arity);
                for (var i = 0; i < arity; i++)
                {
                    if (i > 0)
                    {
                        if (posAt(text, position) != '$')
                        {
                            throw new InvalidDataException($"Expected '$' separator in '{text}'");
                        }

                        position++;
                    }

                    arguments.Add(ParseEncodedType(text, ref position, context));
                }

                var instantiated = GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
                return ConsumeArraySuffixes(fullName + "`" + arity, instantiated, text, ref position);
            }

            // 平名：类/枚举全名或别名，走既有解析
            var resolved = ResolveNamedType(fullName, context);
            return ConsumeArraySuffixes(fullName, resolved, text, ref position);
        }

        private static TypeSymbol ConsumeArraySuffixes(string debugName, TypeSymbol type, string text, ref int position)
        {
            while (position + 1 < text.Length && text[position] == '[' && text[position + 1] == ']')
            {
                position += 2;
                type = TypeSymbol.ArrayOf(type);
            }

            return type;
        }

        private static char posAt(string text, int index) => index < text.Length ? text[index] : '\0';

        private static bool IsEncodedNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '.' || c == '_';
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
            if (context.FunctionsByKey.TryGetValue(key, out var function))
            {
                return function;
            }

            // 6e-G7 回退：开放体内对同类实例成员的调用，键可能携带实例化副本前缀
            // （如 `MyLib.MyLib.Box`1#!T.Get[]`——inst.FullName 双缀 + 实参 mangle）。
            // 按「方法名 + 元数」在已注册函数中归一到定义符号（消费方替换期再映射回实例化副本）。
            var bracketIndex = key.LastIndexOf('[');
            if (bracketIndex > 0)
            {
                var head = key.Substring(0, bracketIndex);
                var dotIndex = head.LastIndexOf('.');
                if (dotIndex > 0)
                {
                    var methodName = head.Substring(dotIndex + 1);
                    var parameterCountText = key.Substring(bracketIndex + 1, key.Length - bracketIndex - 2);
                    var parameterCount = parameterCountText.Length == 0
                        ? 0
                        : parameterCountText.Split(',').Length;

                    var candidates = context.Functions.Where(f =>
                        f.Name == methodName &&
                        f.Parameters.Length == parameterCount).ToList();

                    if (candidates.Count == 1)
                    {
                        return candidates[0];
                    }
                }
            }

            throw new InvalidDataException($"Unknown function '{key}'");
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

        /// <summary>璇诲彇 label:value 褰㈠紡鐨勫瓧娈靛苟鏍￠獙鏍囩銆?/summary>
        private static string ReadLabeledField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return Unescape(token.Substring(label.Length));
        }

        /// <summary>璇诲彇 count:N 褰㈠紡鐨勮鏁板瓧娈点€?/summary>
        private static int ReadCountField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return int.Parse(token.Substring(label.Length), CultureInfo.InvariantCulture);
        }

        /// <summary>鍏ㄥ悕鎷嗗垎涓猴紙鍛藉悕绌洪棿, 鍚嶏級锛涙棤鐐瑰彿鏃跺懡鍚嶇┖闂翠负绌恒€?/summary>
        private static (string Namespace, string Name) SplitFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            return lastDot < 0 ? ("", fullName) : (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1));
        }

        /// <summary>鍙橀噺閿繕鍘熺湡瀹炵鍙峰悕锛氬幓鎺?global:/鍑芥暟閿墠缂€涓?#N 鍐茬獊鍚庣紑銆?/summary>
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
                            if (ResolveNamedType(ownerFullName, context) is ClassTypeSymbol ownerClass)
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
                            BoundThisExpression thisExpression => ((ClassTypeSymbol)thisExpression.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
                            BoundMemberAccessExpression access => access.Field,
                            BoundStaticTypeExpression staticType => ((ClassTypeSymbol)staticType.Type).Fields.FirstOrDefault(f => f.Name == fieldName),
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
                // 璺宠繃鑺傜偣寮€鎷彿 `(`锛涜繑鍥炲師瀛愭垨 `)`锛堝垪琛ㄧ粓姝級
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
