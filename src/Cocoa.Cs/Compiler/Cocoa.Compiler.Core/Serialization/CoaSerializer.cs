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
    /// `.coa` 语义层序列化器：符号表 + 降级 BoundProgram（函数体）文本 round-trip。
    /// 双后端共用（native → MirToLir，IL → IlEmitter）；语法节点（Syntax）不序列化（置 null）。
    ///
    /// 文本格式（可读优先，类型/函数/变量一律按名字引用，不用数字 id）：
    ///   (type)     内建/数组类型内联为名字引用：int / int[] / int[][]；类/枚举用全名 System.Console
    ///   (enum)     (enum MyLib.Color members:3 (Red 0) (Green 1) (Blue 2))
    ///   (systype)  (systype System.Object)——内建单例按全名映射
    ///   (cls)      (cls System.Console public methods:2 WriteLine[string] ReadKey)——方法列 Name[参数类型] 签名
    ///   (fn)       (fn MyLib.Add(i32,i32) name:Add ret:i32 ns:MyLib owner:- extern:false ...
    ///               params:2 (par MyLib.Add/a a i32 0) ...)
    ///              函数键 = [命名空间或宿主类.]函数名(参数类型列表)，重载靠参数类型区分
    ///   (glb/loc)  (glb global:version true i32 (const i:1)) / (loc MyLib.Factorial/result false i32)
    ///              变量键：全局 global:名字；局部（参数）函数键/名字（同名冲突加 #2、#3 后缀）。
    ///   运算符     文本记号 + - * / % << >> &amp; | ^ == != &lt; &lt;= &gt; &gt;= &amp;&amp; || ! ~
    ///   布尔/枚举词： true false；public internal protected private；winapi cdecl stdcall；unicode ansi auto
    /// </summary>
    public static partial class CoaSerializer
    {
        public const string Magic = "COCOA";
        public const int Version = 1;

        /// <summary>完整性校验：文件末行 `(checksum sha256:&lt;hex&gt;)` 覆盖其前全部字节（UTF-8）；读侧强制校验。</summary>
        private const string ChecksumTag = "sha256:";

        // ---------------------------------------------------------------- write

        /// <summary>.coa 反序列化不携带语法节点（设计如此，见类头注释）；nullable 单点豁免。</summary>
        private static SyntaxNode NoSyntax => null!;

        public static void Write(TextWriter writer, CoaProgram program)
        {
            var registry = new Registry(program.Name);
            var labelsByFunction = new Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>>(ReferenceEqualityComparer.Instance);

            // 收集符号——函数体按 Functions（声明序）遍历，保证确定性（ImmutableDictionary 迭代序不稳定）。
            foreach (var e in program.Enums)
            {
                registry.RegisterType(e);
            }
            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）。
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

            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）。
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                CollectBody(registry, pair.Key, pair.Value, labels);
                labelsByFunction[pair.Key] = labels;
            }

            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）。
            registry.Seal();

            var buffer = new StringWriter();
            var w = new Writer(buffer);
            w.Open("cod");
            w.Field(Magic);
            w.Field(Version);

            // 符号表（按注册序）。
            w.Open("symbols");
            foreach (var emitter in registry.Emitters)
            {
                emitter(w, registry);
            }
            w.End();

            // 函数体。
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
            // 6e-G7 S2：开放绑定体（泛型定义方法）——显式遍历，避免卷入 stdlib 注入序列。
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

            // 完整性校验：对正文全部字节（UTF-8）取SHA256，追加为文件末行（读侧强制校验，缺失/不符拒载）。
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
                        // M0-1c：开放体对象创建 `new Foo(args)`——构造器由类型+元数重解析，仅需类型 + 实参
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
