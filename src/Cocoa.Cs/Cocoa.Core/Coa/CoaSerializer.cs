using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Coa
{
    /// <summary>
    /// `.coa` 鐠囶厺绠熺仦鍌氱碍閸掓瀵查崳顭掔窗缁楋箑褰跨悰?+ 闂勫秶楠?BoundProgram閿涘牆鍤遍弫棰佺秼閿涘鏋冮張?round-trip閵?
    /// 閸欏苯鎮楃粩顖氬彙閻㈩煉绱檔ative 閳?MirToLir閿涘瓥L 閳?IlEmitter閿涘绱辩拠顓熺《閼哄倻鍋ｉ敍鍦珁ntax閿涘绗夋惔蹇撳灙閸栨牭绱欑純?null閿涘鈧?
    ///
    /// 閺傚洦婀伴弽鐓庣础閿涘牆褰茬拠璁崇喘閸忓牞绱濈猾璇茬€?閸戣姤鏆?閸欐﹢鍣烘稉鈧瀣瘻閸氬秴鐡у鏇犳暏閿涘奔绗夐悽銊︽殶鐎?id閿涘绱?
    ///   (type)     閸愬懎缂?閺佹壆绮嶇猾璇茬€烽崘鍛颁粓娑撳搫鎮曠€涙绱╅悽顭掔窗int / int[] / int[][]閿涙稓琚?閺嬫矮濡囬悽銊ュ弿閸?System.Console
    ///   (enum)     (enum MyLib.Color members:3 (Red 0) (Green 1) (Blue 2))
    ///   (systype)  (systype System.Object)閳ユ柡鈧柨鍞村鍝勫礋娓氬瀵滈崗銊ユ倳閺勭姴鐨?
    ///   (cls)      (cls System.Console public methods:2 WriteLine[string] ReadKey)閳ユ柡鈧梹鏌熷▔鏇炲灙 Name[閸欏倹鏆熺猾璇茬€穄 缁涙儳鎮?
    ///   (fn)       (fn MyLib.Add(i32,i32) name:Add ret:i32 ns:MyLib owner:- extern:false ...
    ///               params:2 (par MyLib.Add/a a i32 0) ...)
    ///              閸戣姤鏆熼柨?= [閸涜棄鎮曠粚娲？閹存牕顔栨稉鑽よ.]閸戣姤鏆熼崥?閸欏倹鏆熺猾璇茬€烽崚妤勩€?閿涘矂鍣告潪浠嬫浆閸欏倹鏆熺猾璇茬€烽崠鍝勫瀻
    ///   (glb/loc)  (glb global:version true i32 (const i:1)) / (loc MyLib.Factorial/result false i32)
    ///              閸欐﹢鍣洪柨顕嗙窗閸忋劌鐪?global:閸氬秴鐡ч敍娑樼湰闁?閸欏倹鏆?閸戣姤鏆熼柨?閸氬秴鐡ч敍鍫濇倱閸氬秴鍟跨粣浣稿 #2閵?3 閸氬海绱戦敍?
    ///   鏉╂劗鐣荤粭?     閺傚洦婀扮拋鏉垮娇 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   鐢啫鐨?閺嬫矮濡囩拠? true false閿涙埠ublic internal protected private閿涙硤inapi cdecl stdcall閿涙硢nicode ansi auto
    /// </summary>
    internal static partial class CoaSerializer
    {
        public const string Magic = "COCOA";
        public const int Version = 1;

        /// <summary>鐎瑰本鏆ｉ幀褎鐗庢宀嬬窗閺傚洣娆㈤張顐ヮ攽 `(checksum sha256:&lt;hex&gt;)` 鐟曞棛娲婇崗璺哄閸忋劑鍎寸€涙濡敍鍦睺F-8閿涘绱辩拠璁虫櫠瀵搫鍩楅弽锟犵崣閵?/summary>
        private const string ChecksumTag = "sha256:";

        // ---------------------------------------------------------------- write

        public static void Write(TextWriter writer, CoaProgram program)
        {
            var registry = new Registry(program.Name);
            var labelsByFunction = new Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>>(ReferenceEqualityComparer.Instance);

            // 閺€鍫曟肠缁楋箑褰块垾鏂衡偓鏂垮毐閺侀缍嬮幐?Functions閿涘牆锛愰弰搴＄碍閿涘浜堕崢鍡礉娣囨繆鐦夌涵顔肩暰閹嶇礄ImmutableDictionary 鏉╊厺鍞惔蹇庣瑝缁嬪啿鐣鹃敍?
            foreach (var e in program.Enums)
            {
                registry.RegisterType(e);
            }
            // 6e-G7 S1锛氭硾鍨嬪畾涔夊湪鏋氫妇鍚庛€佺被/鍑芥暟鍓嶆敞鍐屸€斺€攇cls 鏉＄洰椤诲厛浜庡紩鐢?!寮€鏀惧弬鏁?鐨?fn 鏉＄洰钀界洏
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

            // 6e-G7 S2锛氭硾鍨嬪畾涔夋柟娉曠殑寮€鏀剧粦瀹氫綋鍚屾牱鏀堕泦锛堟樉寮忔竻鍗曪紝涓嶈Е纰?stdlib 娉ㄥ叆浣擄級
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                CollectBody(registry, pair.Key, pair.Value, labels);
                labelsByFunction[pair.Key] = labels;
            }

            // 閸忋劑鍎寸粭锕€褰块弨鍫曟肠鐎瑰本鐦崥搴″晙鐎规艾鎮曢敍鍫濆綁闁插繘鏁棁鈧憰浣稿毐閺佷即鏁敍灞肩瑬鐟曚浇娉曠粭锕€褰垮☉鍫ュ櫢閿?
            registry.Seal();

            var buffer = new StringWriter();
            var w = new Writer(buffer);
            w.Open("cod");
            w.Field(Magic);
            w.Field(Version);

            // 缁楋箑褰跨悰顭掔礄閹稿鏁為崘灞界碍閿?
            w.Open("symbols");
            foreach (var emitter in registry.Emitters)
            {
                emitter(w, registry);
            }
            w.End();

            // 閸戣姤鏆熸担?
            w.Open("bodies");
            foreach (var fn in program.Functions)
            {
                // 6e-G7 S2锛氭硾鍨嬪畾涔夊睘涓荤殑鏂规硶浣擄紙寮€鏀剧粦瀹氫綋锛夐殢搴撴惡甯︼紱鍏朵綑瀹炰緥鏂规硶涓嶅湪瀹瑰櫒搴忓垪鍖栬寖鍥达紝璺宠繃
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

            // 6e-G7 S2锛氬紑鏀剧粦瀹氫綋锛堟硾鍨嬪畾涔夋柟娉曪級鈥斺€旀樉寮忛亶鍘嗭紝閬垮厤鍗峰叆 stdlib 娉ㄥ叆浣?
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                WriteBodyEntry(w, registry, labelsByFunction, pair.Key, pair.Value);
            }
            w.End();

            // 娓氭繆绂嗗〒鍛礋
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

            // 鐎瑰本鏆ｉ幀褎鐗庢宀嬬窗鐎佃顒滈弬鍥у弿闁劌鐡ч懞鍌︾礄UTF-8閿涘褰?SHA256閿涘矁鎷烽崝鐘辫礋閺傚洣娆㈤張顐ヮ攽閿涘牐顕版笟褍宸遍崚鑸电墡妤犲矉绱濈紓鍝勩亼/娑撳秶顑侀幏鎺曟祰閿?
            var payload = buffer.ToString();
            writer.Write(payload);
            writer.WriteLine("(checksum " + ChecksumTag + ComputeChecksum(payload) + ")");
        }

        private static string ComputeChecksum(string payload)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        private static string RequirementName(CoaRequirement r)
        {
            return r switch
            {
                CoaRequirement.Any => "any",
                CoaRequirement.DotNet => "dotnet",
                _ => "any",
            };
        }

        private static CoaRequirement ParseRequirement(string name)
        {
            return name switch
            {
                "dotnet" => CoaRequirement.DotNet,
                _ => CoaRequirement.Any,
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
                case BoundNodeKind.ForRangeStatement:
                    {
                        var n = (BoundForRangeStatement)statement;
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
                case BoundNodeKind.ObjectCreationExpression:
                    {
                        // M0-1c锛氬紑鏀句綋瀵硅薄鍒涘缓 `new Foo(args)`鈥斺€旀瀯閫犲櫒鐢辩被鍨?鍏冩暟閲嶈В鏋愶紝浠呴渶绫诲瀷 + 瀹炲弬
                        var n = (BoundObjectCreationExpression)expression;
                        registry.RegisterType(n.Type);
                        foreach (var arg in n.Arguments)
                        {
                            CollectExpression(registry, owner, arg, labels);
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
                case BoundNodeKind.ForRangeStatement:
                    {
                        var n = (BoundForRangeStatement)statement;
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
                    // 鐠嬪啳鐦穱鈩冧紖闂勫秶楠囬敍姘矌鎼村繐鍨崠鏍у敶鐏炲倽婢?
                    WriteStatement(w, registry, labels, ((BoundSequencePointStatement)statement).Statement);
                    break;
                default:
                    // 6e-G7 S2锛氭潨缁濋潤榛樹骇鍑烘崯鍧忔祦鈥斺€旀湭瑕嗙洊鑺傜偣鏄惧紡澶辫触
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
                case BoundNodeKind.ObjectCreationExpression:
                    {
                        // M0-1c锛氬璞″垱寤?`new Foo(args)`鈥斺€旀瀯閫犲櫒鐢辩被鍨?鍏冩暟閲嶈В鏋愶紝浠呴渶绫诲瀷 + 瀹炲弬
                        var n = (BoundObjectCreationExpression)expression;
                        w.Open("objnew");
                        w.Field(TypeRef(n.Type));
                        w.Field(n.Arguments.Length);
                        foreach (var arg in n.Arguments)
                        {
                            WriteExpression(w, registry, labels, arg);
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
                        // 6e-G7锛氬瓧娈佃闂殢 gcls/fld 鎼哄甫锛團ield 缁?FnKey 寮忓悕瀛楀洖濉級锛涗粎鏁扮粍/瀛楃涓?`.Length` 鏃?Field == null
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
                        // 6e-G7 S2锛氬瓧娈佃祴鍊硷紙寮€鏀句綋鎼哄甫锛夛細target 琛ㄨ揪寮?+ 瀛楁鍚?绫诲瀷/闈欐€佷綅 + 鍊?
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
                    throw new NotSupportedException($"[cod] Unserializable expression kind '{expression.Kind}' in fn '{registry.CurrentFunctionName}' at {expression.Syntax}");
            }
        }

        private static void WriteUnaryOperator(Writer w, Registry registry, BoundUnaryOperator op)
        {
            w.Open("uop");
            w.Field(UnaryOpText(op.Kind));
            w.Field(TypeRef(op.OperandType));
            w.End();
        }

        private static void WriteBinaryOperator(Writer w, Registry registry, BoundBinaryOperator op)
        {
            w.Open("bop");
            w.Field(BinaryOpText(op.Kind));
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

        /// <summary>6e-M19 M2-c閿涙艾鍞村鍝勫礋娓氬绱橲ystem.Object/System.Type閿涘瀵滈崗銊ユ倳鎼村繐鍨崠鏍电礉鐠囪鏅堕弰鐘茬殸閸ョ偛宕熸笟瀣ㄢ偓?/summary>
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
            // 6e-G7/M0-1a锛氭帴鍙ｄ綅 + 瀹炵幇鎺ュ彛鍒楄〃锛堜緵娑堣垂鏂?IsInterface 鍒ゅ畾涓庢帴鍙ｆ垚鍛樻部 Interfaces 閾捐В鏋愶級
            w.Field("iface:" + BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            w.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                w.Field(TypeRef(iface));
            }
            // 鎼村繐鍨崠鏍у弿闁劑娼ら幀浣规煙濞夋洜顒烽崥宥忕礄6e-M18閿涙艾顔愰崳銊ц閸忎浇顔忕敮锔跨秼闂堟瑦鈧焦鏌熷▔鏇礉婵?Console.WriteLine/Math.Max閿涙硞yscall/extern 娴滐缚璐熼棃娆愨偓渚婄礆閵?
            // 閺傝纭堕張顑跨秼閻㈠崬鎮囬懛?fn 閺夛紕娲伴幖鍝勭敨閿涘潵wner 鐎涙顔岄崶鐐诧綖缁缍婄仦鐑囩礆閿涘矁绻栭柌灞藉灙 Name[閸欏倹鏆熺猾璇茬€穄 娓氭盯妲勭拠浼欑礄閺冪姴寮惇浣烘殣閺傝瀚崣鍑ょ礆閵?
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            w.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                w.Field(MethodSignature(method));
            }
            // 6b锛歠acade 瀹炰緥绫诲睘鎬у０鏄庯紙getter/setter 璁块棶鍣ㄤ负鐙珛 fn `get_X`/`set_X`锛岃渚?fns 鍥炲～鍚庢寕鎺ワ級
            var properties = classType.Properties;
            if (properties.Length > 0)
            {
                w.Field("props:" + properties.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var property in properties)
                {
                    w.Open("prop");
                    w.Field(Str(property.Name));
                    w.Field(TypeRef(property.Type));
                    w.Field(BoolWord(property.Getter != null));
                    w.Field(BoolWord(property.Setter != null));
                    w.Field(property.Visibility.ToString().ToLowerInvariant());
                    w.Field(BoolWord(property.IsStatic));
                    w.End();
                }
            }
            w.End();
        }

        /// <summary>閺傝纭剁粵鎯ф倳閻參鏁敍姝俛me 閹?Name[閸欏倹鏆熺猾璇茬€烽崚妤勩€僝閿涘牓鍣告潪浠嬫浆閸欏倹鏆熺猾璇茬€烽崠鍝勫瀻閿涘鈧?/summary>
        private static string MethodSignature(FunctionSymbol method)
        {
            // 6e-M23 R8閿涙矮绮庡?out/ref 閻ㄥ嫰鍣告潪浠嬫暛妞よ绗夐崥宀嬬礄娣囶噣銈扮粭锕€鍙嗙粵鎯ф倳閿?
            return method.Parameters.Length == 0
                ? method.Name
                : method.Name + "[" + string.Join(",", method.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type))) + "]";
        }

        /// <summary>
        /// 娉涘瀷瀹氫箟绫昏妭鐐癸紙6e-G7 S1锛夛細绫诲瀷鍙傛暟锛堝惈绾︽潫锛? 瀛楁 + 闈欐€佹柟娉曠鍚嶃€?
        /// 鎴愬憳绫诲瀷缁?TypeRef 鎼哄甫寮€鏀惧弬鏁帮紙!灞炰富.鍚嶏級涓庡疄渚嬪寲 mangle锛涘紑鏀剧粦瀹氫綋鐢?bodies 鍖烘寜 FnKey 鎼哄甫锛圫2锛夈€?
        /// </summary>
        private static void EmitGenericClassSymbol(Writer w, Registry registry, NamedTypeSymbol classType)
        {
            System.Console.Error.WriteLine("[G7] gcls " + classType.FullName + " methods=[" +
                string.Join(",", classType.Methods.Select(m => m.Name + (m.IsStatic ? "(s)" : m.IsConstructor ? "(ctor)" : "(i)"))) + "]" +
                " fns=" + string.Join(",", ((IEnumerable<object>)classType.Methods).Count()));
            w.Open("gcls");
            w.Field(classType.FullName);
            w.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 6e-G7/M0-1a锛氭帴鍙ｄ綅 + 瀹炵幇鎺ュ彛鍒楄〃锛堝紑鏀惧弬鏁扮粡 TypeRef `!灞炰富.鍚峘 缂栫爜锛屽 `List<T>: IEnumerable<!List.T>`锛?
            w.Field("iface:" + BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            w.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                w.Field(TypeRef(iface));
            }

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

            // 6e 跨库里程碑：泛型定义类属性声明（访问器 get_X/set_X 为独立 fn，读侧 fns 回填后挂接）。
            var properties = classType.Properties;
            if (properties.Length > 0)
            {
                w.Field("props:" + properties.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var property in properties)
                {
                    w.Open("prop");
                    w.Field(Str(property.Name));
                    w.Field(TypeRef(property.Type));
                    w.Field(BoolWord(property.Getter != null));
                    w.Field(BoolWord(property.Setter != null));
                    w.Field(property.Visibility.ToString().ToLowerInvariant());
                    w.Field(BoolWord(property.IsStatic));
                    w.End();
                }
            }

            w.End();
        }

        /// <summary>tpar/ftp 瀛愯妭鐐瑰叡鐢ㄥ啓鍑猴紙6e-G7 S1锛夛細鍚?/ 搴忓彿 / 绾︽潫鏍囧織 / 鏄惧紡绾︽潫绫诲瀷鍒楄〃銆?/summary>
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

        /// <summary>绾︽潫鏍囧織瑙ｆ瀽锛坓cls.tpar 涓?fn.tps 鍏辩敤锛?e-G7 S1锛夈€?/summary>
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
        /// tpar/ftp 瀛愯妭鐐硅鍙栵紙6e-G7 S1锛夛細鏋勯€犵鍙?+ 搴旂敤鏍囧織 + 鐧昏寮€鏀鹃敭锛堢被绾?闄愬畾閿?!灞炰富.鍚嶏紱
        /// 鏂规硶绾?瑁搁敭 !鍚嶏級+ 鏆傚瓨绾︽潫鏁般€傝繑鍥?(鍙傛暟, 绾︽潫鏁?锛岀害鏉熺敱绗簩瓒熻В鏋愩€?
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

        /// <summary>绾︽潫绗簩瓒燂細鍏勫紵鍙傛暟宸插叏閮ㄦ敞鍐屽悗瑙ｆ瀽鏄惧紡绾︽潫绫诲瀷銆?/summary>
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

            // 6e-G7 S1锛氭柟娉曠骇绫诲瀷鍙傛暟锛堥《灞傛硾鍨嬪嚱鏁帮級鈥斺€旇８閿?!鍚嶏紙鏃犲睘涓荤被锛?
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

            // 6e-G7 S2锛氬睘涓绘柟娉曟惡甯﹂潤鎬?鏋勯€?璁块棶鍣ㄤ綅锛堟硾鍨嬪畾涔変笌 6b facade 瀹炰緥绫绘樉寮忓尯鍒嗭紱瀹瑰櫒绫诲叏闈欐€?鏄惧紡 true锛?
            if (fn.ContainingClass != null)
            {
                w.Field("static:" + BoolWord(fn.IsStatic));
                w.Field("ctor:" + BoolWord(fn.IsConstructor));
                w.Field("acc:" + BoolWord(fn.IsPropertyAccessor));
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

        /// <summary>缁鐎烽惃鍕瀮閺堫剙绱╅悽顭掔窗閸愬懎缂?閺佹壆绮嶉悽銊х叚閸氬稄绱檌nt / int[][]閿涘绱濈猾?閺嬫矮濡囬悽銊ュ弿閸氬秲鈧?/summary>
        private static string TypeRef(TypeSymbol type)
        {
            // 6e 跨库里程碑：基元内建 → `@` 权威记法（@i32/@string/@bool…，Rust/LLVM 式位宽名）。
            // 引用相等键（单例稳定），先于 NamedTypeSymbol 分支命中，避免输出 C# 短名 int/string。
            if (GenericTypeInstantiator.TryGetPrimitiveName(type, out var primitiveName))
            {
                return primitiveName;
            }
            // 6e-G7 S1锛氬紑鏀剧被鍨嬪弬鏁?鈫?闄愬畾鏉冨▉閿?`!灞炰富鍏ㄥ悕.鍙傛暟鍚峘锛堟柟娉曠骇鏃犲睘涓诲洖钀借８鍚嶏級锛?
            // 瀹炰緥鍖栫被鍨?鈫?Encode v3 瀹屾暣 mangle锛坆acktick 鍏冩暟 + # + $ 鍒嗛殧閫掑綊瀹炲弬锛?
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

            // 6e-M22/M0-1b锛氬嚱鏁扮被鍨?`fnty{鍙傛暟,;杩斿洖}`锛堥€掑綊 TypeRef锛涘弬鏁伴€楀彿鍒嗛殧銆佸垎鍙锋帴杩斿洖銆亄} 宓屽鈥斺€?
            // .coa 璇嶆硶浠呬互绌虹櫧涓?() 鍒囧垎锛屾晠宓屽鐢?{} 閬垮紑缁撴瀯鎷彿锛?
            if (type is FunctionTypeSymbol functionType)
            {
                var builder = new System.Text.StringBuilder();
                builder.Append("fnty{");
                for (var i = 0; i < functionType.ParameterTypes.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(TypeRef(functionType.ParameterTypes[i]));
                }

                builder.Append(';');
                builder.Append(TypeRef(functionType.ReturnType));
                builder.Append('}');
                return builder.ToString();
            }

            if (type.ElementType != null)
            {
                // 数组：递归元素 TypeRef（元素为开放类型参数时限定为 !属主.名，如 K[] → !System.Collections.Generic.Dictionary.K[]）
                return TypeRef(type.ElementType) + "[]";
            }

            return type.Name;
        }

        /// <summary>
        /// 瀹炰緥鍖栫被鍨嬬殑 .coa 缂栫爜锛?e-G7 S1锛夛細瀹氫箟鍏ㄥ悕 + backtick 鍏冩暟 + # + $ 鍒嗛殧瀹炲弬銆?
        /// 瀹炲弬閫掑綊璧?<see cref="TypeRef"/>鈥斺€斿紑鏀惧弬鏁颁负闄愬畾閿?!灞炰富.鍚嶏紙鍖哄埆浜?mangle 缂撳瓨閿殑瑁?!T锛夛紝
        /// 淇濊瘉璺ㄥ畾涔夋棤姝т箟涓旇渚у彲鐙珛瑙ｆ瀽锛涘熀鍏?绫荤敤骞冲悕锛堜笉鍚?$銆乣銆?锛屽垎闅斿畨鍏級锛涘祵濂楀疄渚嬪寲閫掑綊銆?
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

        /// <summary>6e-G7 S2锛氬崟鏉?body 鏉＄洰锛團nKey + 璇彞鍧楋級銆?/summary>
        /// <summary>6e-M26锛氭硾鍨嬪紑鏀剧粦瀹氫綋纭畾鎬ф帓搴忛敭锛圙enericOpenBodies 涓?ImmutableDictionary锛屾灇涓句笉绋冲畾锛夈€?/summary>
        private static string GenericOpenSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }

        private static void WriteBodyEntry(Writer w, Registry registry, Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>> labelsByFunction, FunctionSymbol fn, BoundBlockStatement body)
        {
            registry.CurrentFunctionName = fn.Name + (fn.ContainingClass != null ? " (" + fn.ContainingClass.FullName + ")" : "");
            w.Open("body");
            w.Field(registry.FnKey(fn));
            WriteStatement(w, registry, labelsByFunction[fn], body);
            w.End();
            registry.CurrentFunctionName = null;
        }

        private static string BoolWord(bool value)
        {
            return value ? "true" : "false";
        }

        private static string UnaryOpText(BoundUnaryOperatorKind kind)
        {
            return kind switch
            {
                BoundUnaryOperatorKind.Identity => "+",
                BoundUnaryOperatorKind.Negation => "-",
                BoundUnaryOperatorKind.LogicalNegation => "!",
                BoundUnaryOperatorKind.OnesComplement => "~",
                _ => throw new NotSupportedException($"Unsupported unary operator '{kind}'"),
            };
        }

        private static string BinaryOpText(BoundBinaryOperatorKind kind)
        {
            return kind switch
            {
                BoundBinaryOperatorKind.Addition => "+",
                BoundBinaryOperatorKind.Subtraction => "-",
                BoundBinaryOperatorKind.Multiplication => "*",
                BoundBinaryOperatorKind.Division => "/",
                BoundBinaryOperatorKind.Modulo => "%",
                BoundBinaryOperatorKind.ShiftLeft => "<<",
                BoundBinaryOperatorKind.ShiftRight => ">>",
                BoundBinaryOperatorKind.BitwiseAnd => "&",
                BoundBinaryOperatorKind.BitwiseOr => "|",
                BoundBinaryOperatorKind.BitwiseXor => "^",
                BoundBinaryOperatorKind.Equals => "==",
                BoundBinaryOperatorKind.NotEquals => "!=",
                BoundBinaryOperatorKind.ReferenceEquals => "==",
                BoundBinaryOperatorKind.ReferenceNotEquals => "!=",
                BoundBinaryOperatorKind.Less => "<",
                BoundBinaryOperatorKind.LessOrEquals => "<=",
                BoundBinaryOperatorKind.Greater => ">",
                BoundBinaryOperatorKind.GreaterOrEquals => ">=",
                BoundBinaryOperatorKind.LogicalAnd => "&&",
                BoundBinaryOperatorKind.LogicalOr => "||",
                _ => throw new NotSupportedException($"Unsupported binary operator '{kind}'"),
            };
        }

        private static BoundUnaryOperatorKind ParseUnaryOpText(string text)
        {
            return text switch
            {
                "+" => BoundUnaryOperatorKind.Identity,
                "-" => BoundUnaryOperatorKind.Negation,
                "!" => BoundUnaryOperatorKind.LogicalNegation,
                "~" => BoundUnaryOperatorKind.OnesComplement,
                _ => throw new InvalidDataException($"Unknown unary operator '{text}'"),
            };
        }

        private static BoundBinaryOperatorKind ParseBinaryOpText(string text)
        {
            return text switch
            {
                "+" => BoundBinaryOperatorKind.Addition,
                "-" => BoundBinaryOperatorKind.Subtraction,
                "*" => BoundBinaryOperatorKind.Multiplication,
                "/" => BoundBinaryOperatorKind.Division,
                "%" => BoundBinaryOperatorKind.Modulo,
                "<<" => BoundBinaryOperatorKind.ShiftLeft,
                ">>" => BoundBinaryOperatorKind.ShiftRight,
                "&" => BoundBinaryOperatorKind.BitwiseAnd,
                "|" => BoundBinaryOperatorKind.BitwiseOr,
                "^" => BoundBinaryOperatorKind.BitwiseXor,
                "==" => BoundBinaryOperatorKind.Equals,
                "!=" => BoundBinaryOperatorKind.NotEquals,
                "<" => BoundBinaryOperatorKind.Less,
                "<=" => BoundBinaryOperatorKind.LessOrEquals,
                ">" => BoundBinaryOperatorKind.Greater,
                ">=" => BoundBinaryOperatorKind.GreaterOrEquals,
                "&&" => BoundBinaryOperatorKind.LogicalAnd,
                "||" => BoundBinaryOperatorKind.LogicalOr,
                _ => throw new InvalidDataException($"Unknown binary operator '{text}'"),
            };
        }

        // ---------------------------------------------------------------- write: value encoding

        private static string EncodeValue(object value)
        {
            switch (value)
            {
                case null: return "n:"; // 6e-M19 M5-a閿涙ull 鐢悂鍣?
case int i: return "i:" + i.ToString(CultureInfo.InvariantCulture);
                case long l: return "l:" + l.ToString(CultureInfo.InvariantCulture); // 6e-M23 R8锛歩64 甯搁噺
                case ulong ul: return "U:" + ul.ToString(CultureInfo.InvariantCulture); // 6b锛歶64 甯搁噺锛圡0-4 鎵? TryParse 寮曞叆锛?
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
                case 'n': return null!; // 6e-M19 M5-a閿涙ull 鐢悂鍣?
                case 'i': return int.Parse(rest, CultureInfo.InvariantCulture);
                case 'l': return long.Parse(rest, CultureInfo.InvariantCulture); // 6e-M23 R8閿涙64 鐢悂鍣?
                case 'b': return rest == "1";
                case 'c': return (char)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'u': return (byte)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'U': return ulong.Parse(rest, CultureInfo.InvariantCulture); // 6b锛歶64 甯搁噺
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
                    // 閺嶅洩顔囬悥鎯板Ν閻愮懓鎯堢€涙劘濡悙鐧哥窗閸忓爼妫撮幏顒€褰块幑銏ｎ攽缂傗晞绻橀敍宀冣偓宀勬姜鐞涘苯鍞撮梻顓炴値
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
                    // 婢舵俺顢戦懞鍌滃仯閿涙艾鍘涢崶鐐插煂鐞涘矂顩婚敍宀勬４閹奉剙褰挎稉搴＄磻閹奉剙褰块崥灞藉灙
                    _w.WriteLine();
                    _w.Write(new string(' ', _depth * 2));
                }

                // 鐞涘苯鍞撮梻顓炴値閿涘牊妫ょ€涙劘濡悙鐧哥礆閹存牕鐣炬担宥呮倵闂傤厼鎮庨崸鍥︾瑝娑撹濮╅幑銏ｎ攽閳ユ柡鈧梻鏁辨稉瀣╃濞?Open/Field/End 閹稿娓剁€规矮缍?
                _w.Write(')');
                _lineStart = false;
            }

            /// <summary>鐎涙劘濡悙鐟扮磻閹奉剙褰块崜宥呯暰娴ｅ秴鍩屾稉瀣╃鐞涘瞼缂夋潻娑樺灙閿涘牆鍑￠崷銊攽妫ｆ牕鍨稉宥呭晙閹广垼顢戦敍澶堚偓?/summary>
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

        /// <summary>閸愭瑤鏅剁粭锕€褰垮▔銊ュ斀鐞涱煉绱伴崢濠氬櫢 + 閸欐垵鐨犳い鍝勭碍閿涘潟d 娴犲懐鏁ゆ禍搴㈠笓鎼村骏绱濇稉宥呭晸閸忋儲鏋冩禒璁圭礆閵?/summary>
        private sealed class Registry
        {
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            private readonly List<FunctionSymbol> _functions = new();
            private readonly List<(VariableSymbol Symbol, FunctionSymbol? Owner)> _variables = new();
            private readonly Dictionary<FunctionSymbol, string> _fnKeys = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, string> _varKeys = new(ReferenceEqualityComparer.Instance);

            /// <summary>当前模块名（`.coa` 库名）：FnKey 库维度前缀的回退归属（符号未带 ContainingLibrary 时）。</summary>
            private readonly string _moduleName;

            public Registry(string moduleName)
            {
                _moduleName = moduleName;
            }

            /// <summary>调试：当前序列化函数名（WriteBodyEntry 设置，供 Unserializable 错误定位）。</summary>
            public string? CurrentFunctionName { get; set; }

            public List<Action<Writer, Registry>> Emitters { get; } = new();

            public string FnKey(FunctionSymbol fn)
        {
            // 6e-G7锛氬紑鏀句綋鎼哄甫鍚庯紝閮ㄥ垎绗﹀彿锛堝 cod 娉ㄥ叆閾句笂鐨勫疄渚嬪寲鍓湰锛変笉缁?RegisterFunction鈥斺€?
            // 缂洪敭鏃跺洖閫€鍔ㄦ€佽绠楋紙鍏紡涓?Seal 涓€鑷达級锛岃鍐欎袱渚у绉板嵆鑷唇
            return _fnKeys.TryGetValue(fn, out var key) ? key : ComputeFnKey(fn);
        }

            public string VarKey(VariableSymbol v) => _varKeys[v];

            public void RegisterType(TypeSymbol type)
            {
                if (_ids.ContainsKey(type))
                {
                    return;
                }

                // 6e-G7 S1锛氬紑鏀剧被鍨嬪弬鏁拌嚜鎻忚堪锛坓cls 鍐?tpar 澹版槑 + !灞炰富.鍚?寮曠敤锛夛紝鏃犵嫭绔嬫潯鐩?
                if (type is TypeParameterSymbol)
                {
                    return;
                }

                // 6e-G7 S1锛氬疄渚嬪寲绫诲瀷 鈫?娉ㄥ唽娉涘瀷瀹氫箟涓庡叏閮ㄥ疄鍙傦紙渚濊禆鍏堣锛夛紱鏈綋鏃犵嫭绔嬫潯鐩紙寮曠敤澶?mangle 鑷弿杩帮級
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

                if (type is NamedTypeSymbol { TypeKind: not TypeKind.Enum } classType && type.SpecialType == SpecialType.None)
                {
                    RegisterClassCore(classType);
                }
                else if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                {
                    Emitters.Add((w, r) => EmitEnumSymbol(w, r, enumType));
                }
                // 閸忔湹缍戦敍鍫濆敶瀵?閺佹壆绮嶉敍澶庡殰閹诲繗鍫敍灞炬￥闂団偓閻欘剛鐝涢弶锛勬窗
            }

            private void RegisterClassCore(NamedTypeSymbol classType)
            {
                // 6e-M19 M2-c閿涙艾鍞村鍝勫礋娓氬绱橲ystem.Object/System.Type閿涘绗夐崣?cls閳ユ柡鈧棁顕版笟褌绱伴柅鐘插毉閺傛壆琚惍鏉戞綎閸楁洑绶ラ崥灞肩閹嶇幢
                // 閸?systype 閹稿鍙忛崥宥嗘Ё鐏忓嫬娲栭崡鏇氱伐閿涘牊鍨氶崨姗€娼伴悽?Ensure 閸愬懎缂撳▔銊ュ弳閿涘奔绗夋惔蹇撳灙閸栨牭绱?
                if (SystemObjectMembers.IsBuiltinSystemClass(classType))
                {
                    Emitters.Add((w, r) => EmitBuiltinSystemClass(w, r, classType));
                    return;
                }

                // 6e-G7 S1锛氭硾鍨嬪畾涔夎蛋 gcls 涓撳睘鑺傜偣锛沢cls 蹇呴』鍏堜簬鍏堕潤鎬佹柟娉?fn 钀界洏
                // 锛坒n 鐨?ret/par 寮曠敤 !寮€鏀惧弬鏁帮紝璇讳晶闇€鍏堢粡 gcls 娉ㄥ唽闄愬畾閿級锛涜繛甯︽敞鍐岄潪寮€鏀剧被鍨嬩緷璧?
                if (classType.IsGenericDefinition)
                {
                    foreach (var iface in classType.Interfaces)
                    {
                        RegisterType(iface);
                    }

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

                foreach (var iface in classType.Interfaces)
                {
                    RegisterType(iface);
                }

                Emitters.Add((w, r) => EmitClassSymbol(w, r, classType));
            }

            public void RegisterFunction(FunctionSymbol fn)
            {
                if (_ids.ContainsKey(fn))
                {
                    return;
                }

                // 6e 跨库里程碑：非本库符号不入本库 fn 条目——跨库 callee 由依赖库（external）提供符号，
                // 本库只引用其键；避免重复声明致符号身份分裂（Binder 按引用相等合并函数体）。
                if (fn.ContainingLibrary != null &&
                    !string.Equals(fn.ContainingLibrary, _moduleName, StringComparison.Ordinal))
                {
                    return;
                }

                // 缁粯鏌熷▔鏇窗鐎圭懓娅掔猾璇插弿闂堟瑦鈧緤绱檚yscall/extern 閸欏﹤鐢担鎾绘饯閹焦鏌熷▔鏇礉6e-M18閿涘缍旀稉铏瑰缁?fn 鎼村繐鍨崠鏍电幢鐎圭偘绶ラ弬瑙勭《/閺嬪嫰鈧姷鏁辩猾璇诧紦鏉╁洦鎶ら妴?
                // 娓氬顦婚敍姝刡ject 閸愬懎缂撻弬瑙勭《閿涘湣2-c閿涘鐢?BuiltinKind閿涘矁顕版笟褏绮￠崡鏇氱伐婢跺秶鏁ら柌宥呯紦閿涘矂銆忛梾蹇撶穿閻劌绨崚妤€瀵?
                // 6e-G7 S1/S2锛氭硾鍨嬪畾涔夌殑瀹炰緥鏂规硶/鏋勯€犱篃闅忓簱鎼哄甫锛堟秷璐规柟鍗曟€佸寲绱犳潗锛夛紱鍏朵綑瀹炰緥鏂规硶浠嶇敱绫诲３杩囨护
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

            /// <summary>閺€鍫曟肠鐎瑰本鍨氶崥搴ｇ埠娑撯偓閸涜棄鎮曢敍姘毐閺佷即鏁稉搴″綁闁插繘鏁敍鍫濆弿鐏炩偓 global:閸氬秴鐡ч敍娑樼湰闁?閸欏倹鏆?閸戣姤鏆熼柨?閸氬秴鐡ч敍娑樺暱缁愪礁濮?#2/#3閿涘鈧?/summary>
            /// <summary>FnKey 璁＄畻锛?e-G7 鎶藉彇锛夛細owner/ns 鍓嶇紑 + 鍚?+ [鍙傛暟绫诲瀷]锛涗粎宸?out/ref 鐨勯噸杞介敭涓嶅悓銆?/summary>
            private string ComputeFnKey(FunctionSymbol fn)
            {
                var paramTypes = string.Join(",", fn.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type)));
                var head = fn.ContainingClass != null
                    ? fn.ContainingClass.FullName + "." + fn.Name
                    : fn.Namespace.Length > 0 ? fn.Namespace + "." + fn.Name : fn.Name;
                // 6e 跨库里程碑：FnKey 带库维度前缀（`库名!head[...]`）。归属 = 符号带库名则用其库名
                // （跨库 callee：从其库加载的符号），否则回退当前模块名（本库声明的函数/编译期单例）。
                var library = fn.ContainingLibrary ?? _moduleName;
                return (library.Length > 0 ? library + "!" : "") + head + "[" + paramTypes + "]";
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

        /// <summary>娴?`.coa` 閺傚洣娆㈤崝鐘烘祰缁嬪绨梿鍡愨偓?/summary>
        /// <summary>Load `.coa` 文件。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
        public static CoaProgram Load(string path, ImmutableArray<CoaProgram>? external = null)
        {
            var moduleName = Path.GetFileNameWithoutExtension(path);
            return Read(File.ReadAllText(path), moduleName, external ?? ImmutableArray<CoaProgram>.Empty);
        }

    }
}
