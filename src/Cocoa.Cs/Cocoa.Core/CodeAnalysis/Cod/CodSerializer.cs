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
    internal static partial class CodSerializer
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

        private static void EmitEnumSymbol(Writer w, Registry registry, NamedTypeSymbol e)
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
        private static void EmitBuiltinSystemClass(Writer w, Registry registry, NamedTypeSymbol classType)
        {
            w.Open("systype");
            w.Field(classType.FullName);
            w.End();
        }

        private static void EmitClassSymbol(Writer w, Registry registry, NamedTypeSymbol classType)
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
        private static void EmitGenericClassSymbol(Writer w, Registry registry, NamedTypeSymbol classType)
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

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return enumType.FullName;
            }

            if (type is NamedTypeSymbol classType)
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

                if (type is NamedTypeSymbol { TypeKind: not TypeKind.Enum } classType && !type.IsPrimitiveValueType)
                {
                    RegisterClassCore(classType);
                }
                else if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                {
                    Emitters.Add((w, r) => EmitEnumSymbol(w, r, enumType));
                }
                // 鍏朵綑锛堝唴寤?鏁扮粍锛夎嚜鎻忚堪锛屾棤闇€鐙珛鏉＄洰
            }

            private void RegisterClassCore(NamedTypeSymbol classType)
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

    }
}
