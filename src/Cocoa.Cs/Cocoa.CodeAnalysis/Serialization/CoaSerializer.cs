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
    ///   杩愮畻绗?     鏂囨湰璁板彿 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   鐢啫鐨?閺嬫矮濡囩拠? true false閿涙埠ublic internal protected private閿涙硤inapi cdecl stdcall閿涙硢nicode ansi auto
    /// </summary>
    internal static partial class CoaSerializer
    {
        public const string Magic = "COCOA";
        public const int Version = 1;

        /// <summary>鐎瑰本鏆ｉ幀褎鐗庢宀嬬窗閺傚洣娆㈤張顐ヮ攽 `(checksum sha256:&lt;hex&gt;)` 鐟曞棛娲婇崗璺哄閸忋劑鍎寸€涙濡敍鍦睺F-8閿涘绱辩拠璁虫櫠瀵搫鍩楅弽锟犵崣閵?/summary>
        private const string ChecksumTag = "sha256:";

        // ---------------------------------------------------------------- write

        /// <summary>.coa 反序列化不携带语法节点（设计如此，见类头注释）；nullable 单点豁免。</summary>
        private static SyntaxNode NoSyntax => null!;

        public static void Write(TextWriter writer, CoaProgram program)
        {
            var registry = new Registry(program.Name);
            var labelsByFunction = new Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>>(ReferenceEqualityComparer.Instance);

            // 收集符号——函数体挀Functions（声明序）遍历，保证确定性（ImmutableDictionary 迭代序不稳定＀
            foreach (var e in program.Enums)
            {
                registry.RegisterType(e);
            }
            // 閸忋劑鍎寸粭锕€褰块弨鍫曟肠鐎瑰本鐦崥搴″晙鐎规艾鎮曢敍鍫濆綁闁插繘鏁棁鈧憰浣稿毐閺佷即鏁敍灞肩瑬鐟曚浇娉曠粭锕€褰垮☉鍫ュ櫢閿?
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

            // 閸忋劑鍎寸粭锕€褰块弨鍫曟肠鐎瑰本鐦崥搴″晙鐎规艾鎮曢敍鍫濆綁闁插繘鏁棁鈧憰浣稿毐閺佷即鏁敍灞肩瑬鐟曚浇娉曠粭锕€褰垮☉鍫ュ櫢閿?
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

            // 6e-G7 S2：开放绑定体（泛型定义方法）——显式遍历，避免卷入 stdlib 注入佀
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                WriteBodyEntry(w, registry, labelsByFunction, pair.Key, pair.Value);
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
            buffer.WriteLine();

            // 瀹屾暣鎬ф牎楠岋細瀵规鏂囧叏閮ㄥ瓧鑺傦紙UTF-8锛夊彀SHA256锛岃拷鍔犱负鏂囦欢鏈锛堣渚у己鍒舵牎楠岋紝缂哄け/涓嶇鎷掕浇销
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
                        // M0-1c：开放体对象创建 `new Foo(args)`——构造器由类垀元数重解析，仅需类型 + 实参
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


    }
}
