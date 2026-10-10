using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis.Bound;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>
    /// .coa 写端协调器（源 <c>CoaSerializer</c> 的写侧收敛为实例类）：
    /// 持有输出写入器 + 符号注册表（<see cref="CoaTextWriter"/>/<see cref="CoaRegistry"/>），
    /// 所有 Emit*/Write* 为实例方法，递归不再显式传上下文。
    /// </summary>
    internal sealed class CoaFormatWriter
    {
        internal const string Magic = "COCOA";
        internal const int Version = 1;

        private readonly CoaRegistry _registry;
        private CoaTextWriter _output = null!;

        private CoaFormatWriter(string moduleName)
        {
            _registry = new CoaRegistry(moduleName);
        }

        public static void Write(TextWriter writer, CoaProgram program)
        {
            var serializer = new CoaFormatWriter(program.Name);
            serializer.Run(writer, program);
        }

        private void Run(TextWriter writer, CoaProgram program)
        {
            var labelsByFunction = new Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>>(ReferenceEqualityComparer.Instance);

            // 收集符号——函数体按 Functions（声明序）遍历，保证确定性（ImmutableDictionary 迭代序不稳定）。
            foreach (var e in program.Enums)
            {
                _registry.RegisterType(e);
            }
            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）。
            foreach (var g in program.GenericDefinitions)
            {
                _registry.RegisterType(g);
            }
            foreach (var c in program.Classes)
            {
                _registry.RegisterType(c);
            }
            foreach (var f in program.Functions)
            {
                _registry.RegisterFunction(f);
            }
            foreach (var g in program.Globals)
            {
                _registry.RegisterVariable(g);
            }
            foreach (var fn in program.Functions)
            {
                if (!program.Bodies.TryGetValue(fn, out var body))
                {
                    continue;
                }

                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                CollectBody(fn, body, labels);
                labelsByFunction[fn] = labels;
            }

            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）。
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => CoaTypeText.GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                var labels = new Dictionary<string, BoundLabel>(StringComparer.Ordinal);
                CollectBody(pair.Key, pair.Value, labels);
                labelsByFunction[pair.Key] = labels;
            }

            // 全部符号收集完毕后再定名（变量键需要函数键，且要跨符号消重）。
            _registry.Seal();

            var buffer = new StringWriter();
            _output = new CoaTextWriter(buffer);
            _output.Open("cod");
            _output.Field(Magic);
            _output.Field(Version);

            // 符号表（按注册序）。
            _output.Open("symbols");
            foreach (var emitter in _registry.Emitters)
            {
                emitter(this);
            }
            _output.End();

            // 函数体。
            _output.Open("bodies");
            foreach (var fn in program.Functions)
            {
                // 6e-G7 S2：泛型定义属主的方法体（开放绑定体）随库携带；6f-2 其余实例方法（事件类等）
                // 方法体同样随库携带，供动态链接库（A1/CoaLibraryCompiler）重建可运行体。
                if (fn.ContainingClass != null && fn.ContainingClass.IsGenericDefinition)
                {
                    continue;
                }

                if (fn.IsExtern || fn.BuiltinKind != null)
                {
                    continue;
                }

                // 6f-2：仅写出已注册（带 fn 记录、读侧可解析）的函数体——实例方法若未过 RegisterFunction
                // 门（如非 plain-instance 容器类），体无符号可挂接，写出即成孤儿损失。
                if (!_registry.IsFunctionRegistered(fn))
                {
                    continue;
                }

                if (!program.Bodies.TryGetValue(fn, out var body))
                {
                    continue;
                }

                WriteBodyEntry(labelsByFunction, fn, body);
            }
            // 6e-G7 S2：开放绑定体（泛型定义方法）——显式遍历，避免卷入 stdlib 注入序列。
            foreach (var pair in program.GenericOpenBodies.OrderBy(kv => CoaTypeText.GenericOpenSortKey(kv.Key), StringComparer.Ordinal))
            {
                WriteBodyEntry(labelsByFunction, pair.Key, pair.Value);
            }
            _output.End();

            // 依赖清单
            _output.Open("manifest");
            _output.Open("requires");
            _output.Field(CoaText.RequirementName(program.Requires));
            _output.End();
            foreach (var p in program.Platforms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.Open("platform");
                _output.Field(CoaText.Str(p));
                _output.End();
            }
            foreach (var d in program.DotnetReferences.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.Open("refdll");
                _output.Field(CoaText.Str(d));
                _output.End();
            }
            foreach (var c in program.CodReferences.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.Open("refcod");
                _output.Field(CoaText.Str(c));
                _output.End();
            }
            foreach (var i in program.NativeImports.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.Open("import");
                _output.Field(CoaText.Str(i));
                _output.End();
            }
            foreach (var ns in program.Namespaces.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.Open("ns");
                _output.Field(CoaText.Str(ns));
                _output.End();
            }
            _output.End(); // manifest

            // 6e-M24：文档注释段（DocID → 规范化原文）
            if (program.Docs.Count > 0)
            {
                _output.Open("docs");
                foreach (var pair in program.Docs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    _output.Open("doc");
                    _output.Field(CoaText.Str(pair.Key));
                    _output.Field(CoaText.Str(pair.Value));
                    _output.End();
                }
                _output.End();
            }

            _output.End(); // cod
            buffer.WriteLine();

            // 完整性校验：对正文全部字节（UTF-8）取SHA256，追加为文件末行（读侧强制校验，缺失/不符拒载）。
            var payload = buffer.ToString();
            writer.Write(payload);
            writer.WriteLine("(checksum " + CoaText.ChecksumTag + CoaText.ComputeChecksum(payload) + ")");
        }

        // ---------------------------------------------------------------- write: body collection

        private void CollectBody(FunctionSymbol owner, BoundStatement statement, Dictionary<string, BoundLabel> labels)
        {
            switch (statement.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    foreach (var s in ((BoundBlockStatement)statement).Statements)
                    {
                        CollectBody(owner, s, labels);
                    }
                    break;
                case BoundNodeKind.VariableDeclaration:
                    {
                        var d = (BoundVariableDeclaration)statement;
                        _registry.RegisterVariable(d.Variable, owner);
                        CollectExpression(owner, d.Initializer, labels);
                        break;
                    }
                case BoundNodeKind.IfStatement:
                    {
                        var n = (BoundIfStatement)statement;
                        CollectExpression(owner, n.Condition, labels);
                        CollectBody(owner, n.ThenStatement, labels);
                        if (n.ElseStatement != null)
                        {
                            CollectBody(owner, n.ElseStatement, labels);
                        }
                        break;
                    }
                case BoundNodeKind.WhileStatement:
                    {
                        var n = (BoundWhileStatement)statement;
                        CollectExpression(owner, n.Condition, labels);
                        CollectBody(owner, n.Body, labels);
                        break;
                    }
                case BoundNodeKind.DoWhileStatement:
                    {
                        var n = (BoundDoWhileStatement)statement;
                        CollectBody(owner, n.Body, labels);
                        CollectExpression(owner, n.Condition, labels);
                        break;
                    }
                case BoundNodeKind.ForRangeStatement:
                    {
                        var n = (BoundForRangeStatement)statement;
                        _registry.RegisterVariable(n.Variable, owner);
                        CollectExpression(owner, n.LowerBound, labels);
                        CollectExpression(owner, n.UpperBound, labels);
                        if (n.Step != null)
                        {
                            CollectExpression(owner, n.Step, labels);
                        }

                        CollectBody(owner, n.Body, labels);
                        break;
                    }
                case BoundNodeKind.LabelStatement:
                    {
                        var n = (BoundLabelStatement)statement;
                        labels[n.Label.Name] = n.Label;
                        break;
                    }
                case BoundNodeKind.ConditionalGotoStatement:
                    CollectExpression(owner, ((BoundConditionalGotoStatement)statement).Condition, labels);
                    break;
                case BoundNodeKind.ReturnStatement:
                    {
                        var n = (BoundReturnStatement)statement;
                        if (n.Expression != null)
                        {
                            CollectExpression(owner, n.Expression, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ExpressionStatement:
                    CollectExpression(owner, ((BoundExpressionStatement)statement).Expression, labels);
                    break;
                case BoundNodeKind.SequencePointStatement:
                    CollectBody(owner, ((BoundSequencePointStatement)statement).Statement, labels);
                    break;
            }
        }

        private void CollectExpression(FunctionSymbol owner, BoundExpression expression, Dictionary<string, BoundLabel> labels)
        {
            switch (expression.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    _registry.RegisterType(expression.Type);
                    break;
                case BoundNodeKind.VariableExpression:
                    _registry.RegisterVariable(((BoundVariableExpression)expression).Variable, owner);
                    break;
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)expression;
                        _registry.RegisterVariable(n.Variable, owner);
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)expression;
                        _registry.RegisterVariable(n.Variable, owner);
                        _registry.RegisterType(n.Op.LeftType);
                        _registry.RegisterType(n.Op.RightType);
                        _registry.RegisterType(n.Op.ResultType);
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.UnaryExpression:
                    {
                        var n = (BoundUnaryExpression)expression;
                        _registry.RegisterType(n.Op.OperandType);
                        _registry.RegisterType(n.Op.ResultType);
                        CollectExpression(owner, n.Operand, labels);
                        break;
                    }
                case BoundNodeKind.BinaryExpression:
                    {
                        var n = (BoundBinaryExpression)expression;
                        _registry.RegisterType(n.Op.LeftType);
                        _registry.RegisterType(n.Op.RightType);
                        _registry.RegisterType(n.Op.ResultType);
                        CollectExpression(owner, n.Left, labels);
                        CollectExpression(owner, n.Right, labels);
                        break;
                    }
                case BoundNodeKind.ConditionalExpression:
                    {
                        var n = (BoundConditionalExpression)expression;
                        CollectExpression(owner, n.Condition, labels);
                        CollectExpression(owner, n.WhenTrue, labels);
                        CollectExpression(owner, n.WhenFalse, labels);
                        break;
                    }
                case BoundNodeKind.CallExpression:
                    {
                        var n = (BoundCallExpression)expression;
                        _registry.RegisterFunction(n.Function);
                        foreach (var a in n.Arguments)
                        {
                            CollectExpression(owner, a, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ByRefArgument:
                    {
                        var n = (BoundByRefArgument)expression;
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.ConversionExpression:
                    {
                        var n = (BoundConversionExpression)expression;
                        _registry.RegisterType(n.Type);
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)expression;
                        _registry.RegisterType(n.Type);
                        CollectExpression(owner, n.Length, labels);
                        foreach (var i in n.Initializers)
                        {
                            CollectExpression(owner, i, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ObjectCreationExpression:
                    {
                        // M0-1c：开放体对象创建 `new Foo(args)`——构造器由类型+元数重解析，仅需类型 + 实参
                        var n = (BoundObjectCreationExpression)expression;
                        _registry.RegisterType(n.Type);
                        foreach (var arg in n.Arguments)
                        {
                            CollectExpression(owner, arg, labels);
                        }
                        break;
                    }
                case BoundNodeKind.ElementAccessExpression:
                    {
                        var n = (BoundElementAccessExpression)expression;
                        _registry.RegisterType(n.Type);
                        CollectExpression(owner, n.Target, labels);
                        CollectExpression(owner, n.Index, labels);
                        break;
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)expression;
                        _registry.RegisterType(n.Type);
                        CollectExpression(owner, n.Target, labels);
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.MemberAccessExpression:
                    {
                        var n = (BoundMemberAccessExpression)expression;
                        _registry.RegisterType(n.Type);
                        CollectExpression(owner, n.Target, labels);
                        break;
                    }
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)expression;
                        _registry.RegisterType(n.Type);
                        if (n.Method != null)
                        {
                            _registry.RegisterFunction(n.Method);
                        }
                        CollectExpression(owner, n.Expression, labels);
                        foreach (var a in n.Arguments)
                        {
                            CollectExpression(owner, a, labels);
                        }
                        break;
                    }
                case BoundNodeKind.StaticTypeExpression:
                    {
                        var n = (BoundStaticTypeExpression)expression;
                        _registry.RegisterType(n.Type);
                        break;
                    }
                case BoundNodeKind.IsExpression:
                    {
                        var n = (BoundIsExpression)expression;
                        _registry.RegisterType(n.TargetType);
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
                case BoundNodeKind.AsExpression:
                    {
                        var n = (BoundAsExpression)expression;
                        _registry.RegisterType(n.TargetType);
                        CollectExpression(owner, n.Expression, labels);
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- write: symbols

        internal void EmitEnumSymbol(NamedTypeSymbol e)
        {
            _output.Open("enum");
            _output.Field(e.FullName);
            // facade 统一：`bclTarget:` 携带 BCL 目标全名（= 枚举全名），`-` 表非门面（对齐类 facade 标记）。
            _output.Field("bclTarget:" + (e.IsFacadeClass ? e.FullName : "-"));
            var members = e.MemberNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            _output.Field("members:" + members.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var name in members)
            {
                e.TryGetMember(name, out var value);
                _output.Open(name);
                _output.Field(value);
                _output.End();
            }
            _output.End();
        }

        /// <summary>6e-M19 M2-c：内建单例（System.Object/System.Type）按全名序列化，读侧映射回单例。</summary>
        internal void EmitBuiltinSystemClass(NamedTypeSymbol classType)
        {
            _output.Open("systype");
            _output.Field(classType.FullName);
            _output.End();
        }

        internal void EmitClassSymbol(NamedTypeSymbol classType)
        {
            _output.Open("cls");
            _output.Field(classType.FullName);
            _output.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 6e-Step D-c：delegate 标记（Invoke 按 fn owner 携带，读侧据此重建 TypeKind.Delegate）
            if (classType.TypeKind == TypeKind.Delegate)
            {
                _output.Field("tk:Delegate");
            }
            // 6e-G7/M0-1a：接口位 + 实现接口列表（供消费方 IsInterface 判定与接口成员沿 Interfaces 链解析）
            _output.Field("iface:" + CoaText.BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            _output.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                _output.Field(CoaTypeText.TypeRef(iface));
            }
            // N1：值类型位（struct 重建须还原 TypeKind.Struct——否则消费侧按引用类型签名发射，BCL 同名 struct 直联处 value-type mismatch）
            _output.Field("struct:" + CoaText.BoolWord(classType.TypeKind == TypeKind.Struct));
            // 6e-M32：`[Facade("X")]` 显式 BCL 目标（如 System.NativeInt32 → System.IntPtr）——跨库消费端恢复 IL 重定向必需
            _output.Field("bclTarget:" + (classType.FacadeBclTargetName != null ? classType.FacadeBclTargetName : "-"));
            // 6e-M33：显式基类（非 Object）——消费端恢复继承链（Handle 族 FileHandle extends Handle 硬前置）；无/Object → `-`
            _output.Field("base:" + (classType.BaseType != null && !classType.BaseType.IsSystemObjectRoot ? CoaTypeText.LibraryQualify(classType.BaseType) : "-"));
            // 序列化全部静态方法签名（6e-M18：容器类允许带体静态方法，如 Console.WriteLine/Math.Max；syscall/extern 亦为静态）。
            // 方法本体由各自 fn 条目携带（owner 字段回填类归属），这里列 Name[参数类型] 供阅读（无参省略方括号）。
            // N1：接口的实例（抽象）方法签名也须随库携带——否则库侧空壳接口（如 System.IDisposable methods:0）
            // 会遮蔽消费方/源码声明，成员解析全部失败。
            var methods = classType.Methods.Where(m => m.IsStatic || classType.IsInterface).ToArray();
            _output.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                // 接口方法无 fn 条目（无方法体），methods: 是唯一来源——须携带返回类型供读侧重建完整符号
                _output.Field(classType.IsInterface ? InterfaceMethodSignature(method) : MethodSignature(method));
            }
            // 6e-Step D-a：类字段（含闭包环境类 __Env_* 捕获实例成员）随 fld 携带——供闭包读侧重建
            var classFields = classType.Fields.ToArray();
            _output.Field("fields:" + classFields.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var field in classFields)
            {
                _output.Open("fld");
                _output.Field(CoaText.Str(field.Name));
                _output.Field(CoaTypeText.TypeRef(field.Type));
                _output.Field(field.Visibility.ToString().ToLowerInvariant());
                _output.Field(CoaText.BoolWord(field.IsStatic));
                _output.Field(CoaText.BoolWord(field.IsReadonly));
                _output.End();
            }
            // 6e-Step D-b：事件声明（符号多播 + 后备字段 `_<e>` 已在 fields: 携带）——读侧回填 EventSymbol
            var events = classType.Events;
            _output.Field("events:" + events.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var eventSymbol in events)
            {
                _output.Open("evt");
                _output.Field(CoaText.Str(eventSymbol.Name));
                _output.Field(CoaTypeText.TypeRef(eventSymbol.HandlerType));
                _output.Field(eventSymbol.Visibility.ToString().ToLowerInvariant());
                _output.End();
            }
            // 6b：facade 实例类属性声明（getter/setter 访问器为独立 fn `get_X`/`set_X`，读侧 fns 回填后挂接）
            var properties = classType.Properties;
            _output.Field("props:" + properties.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var property in properties)
            {
                _output.Open("prop");
                _output.Field(CoaText.Str(property.Name));
                _output.Field(CoaTypeText.TypeRef(property.Type));
                _output.Field(CoaText.BoolWord(property.Getter != null));
                _output.Field(CoaText.BoolWord(property.Setter != null));
                _output.Field(property.Visibility.ToString().ToLowerInvariant());
                _output.Field(CoaText.BoolWord(property.IsStatic));
                _output.End();
            }
            _output.End();
        }

        /// <summary>方法签名短键：Name 或 Name[参数类型列表]（重载靠参数类型区分）。</summary>
        private static string MethodSignature(FunctionSymbol method)
        {
            // 6e-M23 R8：仅有 out/ref 的重载键须不同（修饰符入签名）。
            return method.Parameters.Length == 0
                ? method.Name
                : method.Name + "[" + string.Join(",", method.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "-") + CoaTypeText.TypeRef(p.Type))) + "]";
        }

        /// <summary>接口方法完整签名：Name[params]:Return（返回类型必须携带——接口无 fn 条目承载方法体签名）。</summary>
        private static string InterfaceMethodSignature(FunctionSymbol method)
        {
            var parameters = method.Parameters.Length == 0
                ? ""
                : "[" + string.Join(",", method.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "-") + CoaTypeText.TypeRef(p.Type))) + "]";
            return method.Name + parameters + ":" + CoaTypeText.TypeRef(method.ReturnType);
        }

        /// <summary>
        /// 泛型定义类节点（6e-G7 S1）：类型参数（含约束）+ 字段 + 静态方法签名。
        /// 成员类型经 TypeRef 携带开放参数（!属主.名）与实例化 mangle；开放绑定体经 bodies 区按 FnKey 携带（S2）。
        /// </summary>
        internal void EmitGenericClassSymbol(NamedTypeSymbol classType)
        {
            _output.Open("gcls");
            _output.Field(classType.FullName);
            _output.Field(classType.Visibility.ToString().ToLowerInvariant());
            // 6e-G7/M0-1a：接口位 + 实现接口列表（开放参数经 TypeRef `!属主.名` 编码，如 `List<T>: IEnumerable<!List.T>`）。
            _output.Field("iface:" + CoaText.BoolWord(classType.IsInterface));
            var interfaces = classType.Interfaces;
            _output.Field("ifaces:" + interfaces.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var iface in interfaces)
            {
                _output.Field(CoaTypeText.TypeRef(iface));
            }
            // N1：值类型位（gcls 同样携带——ValueTuple 等泛型 struct 重建须还原 TypeKind.Struct）
            _output.Field("struct:" + CoaText.BoolWord(classType.TypeKind == TypeKind.Struct));

            var typeParameters = classType.TypeParameters;
            _output.Field("tparams:" + typeParameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var typeParameter in typeParameters)
            {
                WriteTypeParameter(typeParameter);
            }

            var fields = classType.Fields.ToArray();
            _output.Field("fields:" + fields.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var field in fields)
            {
                _output.Open("fld");
                _output.Field(CoaText.Str(field.Name));
                _output.Field(CoaTypeText.TypeRef(field.Type));
                _output.Field(field.Visibility.ToString().ToLowerInvariant());
                _output.Field(CoaText.BoolWord(field.IsStatic));
                _output.Field(CoaText.BoolWord(field.IsReadonly));
                _output.End();
            }

            // 序列化泛型定义类的静态方法签名（方法本体经 bodies 区按 FnKey 携带）。
            var methods = classType.Methods.Where(m => m.IsStatic).ToArray();
            _output.Field("methods:" + methods.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var method in methods)
            {
                _output.Field(classType.IsInterface ? InterfaceMethodSignature(method) : MethodSignature(method));
            }
            // 6e-Step D-b：泛型定义类事件声明（handler 类型可含开放参数）
            var genericEvents = classType.Events;
            _output.Field("events:" + genericEvents.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var eventSymbol in genericEvents)
            {
                _output.Open("evt");
                _output.Field(CoaText.Str(eventSymbol.Name));
                _output.Field(CoaTypeText.TypeRef(eventSymbol.HandlerType));
                _output.Field(eventSymbol.Visibility.ToString().ToLowerInvariant());
                _output.End();
            }
            // 6e 跨库里程碑：泛型定义类属性声明（访问器 get_X/set_X 为独立 fn，读侧 fns 回填后挂接）；常写 props:0。
            var properties = classType.Properties;
            _output.Field("props:" + properties.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var property in properties)
            {
                _output.Open("prop");
                _output.Field(CoaText.Str(property.Name));
                _output.Field(CoaTypeText.TypeRef(property.Type));
                _output.Field(CoaText.BoolWord(property.Getter != null));
                _output.Field(CoaText.BoolWord(property.Setter != null));
                _output.Field(property.Visibility.ToString().ToLowerInvariant());
                _output.Field(CoaText.BoolWord(property.IsStatic));
                _output.End();
            }

            _output.End();
        }

        /// <summary>tpar/ftp 子节点共用写出（6e-G7 S1）：名 / 序号 / 约束标志 / 显式约束类型列表。</summary>
        private void WriteTypeParameter(TypeParameterSymbol typeParameter)
        {
            _output.Open("tpar");
            _output.Field(typeParameter.Name);
            _output.Field(typeParameter.Ordinal);
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

            _output.Field(flags.Count == 0 ? "-" : string.Join("+", flags));
            // 6e-M22 委托真实类型化：型变注解位（in/out；Invariant 不写）
            _output.Field("v:" + VarianceText(typeParameter.Variance));

            _output.Field("c:" + typeParameter.ConstraintTypes.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                _output.Field(CoaTypeText.TypeRef(constraint));
            }

            _output.End();
        }

        private static string VarianceText(VarianceKind variance)
        {
            return variance switch
            {
                VarianceKind.In => "in",
                VarianceKind.Out => "out",
                _ => "-",
            };
        }

        internal void EmitFunctionSymbol(FunctionSymbol fn)
        {
            _output.Open("fn");
            _output.Field(_registry.FnKey(fn));
            _output.Field("name:" + CoaText.Str(fn.Name));

            // 6e-G7 S1：方法级类型参数（顶层泛型函数）——裸键 !名（无属主类）；常写 tps:0 对齐计数惯例。
            _output.Field("tps:" + fn.TypeParameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var typeParameter in fn.TypeParameters)
            {
                WriteTypeParameter(typeParameter);
            }

            _output.Field("ret:" + CoaTypeText.TypeRef(fn.ReturnType));
            _output.Field("ns:" + (fn.Namespace.Length > 0 ? CoaText.Str(fn.Namespace) : "-"));
            _output.Field("owner:" + (fn.ContainingClass != null ? fn.ContainingClass.FullName : "-"));
            _output.Field("extern:" + CoaText.BoolWord(fn.IsExtern));
            _output.Field("dll:" + (fn.DllName != null ? CoaText.Str(fn.DllName) : "-"));
            _output.Field("cc:" + fn.CallingConvention.ToString().ToLowerInvariant());
            _output.Field("builtin:" + (fn.BuiltinKind != null ? fn.BuiltinKind.Value.ToString() : "-"));
            _output.Field("entry:" + (fn.EntryPoint != null ? CoaText.Str(fn.EntryPoint) : "-"));
            _output.Field("charset:" + (fn.CharSet != null ? fn.CharSet.Value.ToString().ToLowerInvariant() : "-"));

            // 6e-M32 Tier-2：函数级 attribute 通道（库跨边界携带 [Test] 等；逗号分隔属性类名，无 → `-`）。
            _output.Field("attrs:" + (fn.Attributes.IsDefaultOrEmpty ? "-" : string.Join(",", fn.Attributes.Select(a => a.Type.Name))));

            // 6e-G7 S2：属主方法携带静态/构造/访问器位（泛型定义与 6b facade 实例类显式区分；容器类全静态显式 true）。
            if (fn.ContainingClass != null)
            {
                _output.Field("static:" + CoaText.BoolWord(fn.IsStatic));
                _output.Field("ctor:" + CoaText.BoolWord(fn.IsConstructor));
                _output.Field("acc:" + CoaText.BoolWord(fn.IsPropertyAccessor));
                // 6e-M25 阶段 5：虚/抽象/重写/密封位（跨库派生 override 解析所需；旧文件无此字段 → 读侧默认 false）
                _output.Field("virt:" + CoaText.BoolWord(fn.IsVirtual));
                _output.Field("abs:" + CoaText.BoolWord(fn.IsAbstract));
                _output.Field("ovr:" + CoaText.BoolWord(fn.IsOverride));
                _output.Field("seal:" + CoaText.BoolWord(fn.IsSealed));
            }

            // 6f-4：捕获闭包元数据（宿主函数 EnvClass/Captures + lambda IsLambdaWithEnvironment）——
            // A1 库发射重建环境类与捕获播种必需（Step F 闭包经库；旧文件无此字段 → 读侧按缺省）。
            if (fn.IsLambdaWithEnvironment || fn.EnvironmentClass != null ||
                (fn.CapturedVariables != null && fn.CapturedVariables.Count > 0))
            {
                _output.Field("envn:" + CoaText.BoolWord(fn.IsLambdaWithEnvironment));
                _output.Field("envl:" + CoaText.BoolWord(fn.IsLambda));
                _output.Field("envc:" + (fn.EnvironmentClass != null ? fn.EnvironmentClass.FullName : "-"));
                var captures = fn.CapturedVariables;
                _output.Field("envcap:" + (captures != null ? captures.Count : 0).ToString(CultureInfo.InvariantCulture));
                if (captures != null)
                {
                    foreach (var captured in captures)
                    {
                        _output.Field(_registry.VarKey(captured));
                    }
                }
            }

            _output.Field("params:" + fn.Parameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var p in fn.Parameters)
            {
                _output.Open("par");
                _output.Field(_registry.VarKey(p));
                _output.Field(CoaText.Str(p.Name));
                _output.Field(CoaTypeText.TypeRef(p.Type));
                _output.Field(p.Ordinal);
                _output.Field(p.IsOut ? "out:" : p.IsRef ? "ref:" : "-");
                _output.Field(p.IsThisParameter ? "this" : "-");
                _output.End();
            }
            _output.End();
        }

        internal void EmitVariableSymbol(VariableSymbol v)
        {
            _output.Open(v is GlobalVariableSymbol ? "glb" : "loc");
            _output.Field(_registry.VarKey(v));
            _output.Field(CoaText.BoolWord(v.IsReadOnly));
            _output.Field(CoaTypeText.TypeRef(v.Type));
            if (v.Constant != null)
            {
                _output.Open("const");
                _output.Field(CoaText.EncodeValue(v.Constant.Value));
                _output.End();
            }

            _output.End();
        }

        /// <summary>6e-G7 S2：单个 body 条目（FnKey + 语句块）。</summary>
        private void WriteBodyEntry(Dictionary<FunctionSymbol, Dictionary<string, BoundLabel>> labelsByFunction, FunctionSymbol fn, BoundBlockStatement body)
        {
            _registry.CurrentFunctionName = fn.Name + (fn.ContainingClass != null ? " (" + fn.ContainingClass.FullName + ")" : "");
            _output.Open("body");
            _output.Field(_registry.FnKey(fn));
            WriteStatement(labelsByFunction[fn], body);
            _output.End();
            _registry.CurrentFunctionName = null;
        }

        // ---------------------------------------------------------------- write: statements

        private void WriteStatement(Dictionary<string, BoundLabel> labels, BoundStatement statement)
        {
            switch (statement.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    {
                        var n = (BoundBlockStatement)statement;
                        _output.Open("block");
                        _output.Field(n.Statements.Length);
                        foreach (var s in n.Statements)
                        {
                            WriteStatement(labels, s);
                        }
                        _output.End();
                        break;
                    }
                case BoundNodeKind.NopStatement:
                    _output.Open("nop");
                    _output.End();
                    break;
                case BoundNodeKind.VariableDeclaration:
                    {
                        var n = (BoundVariableDeclaration)statement;
                        _output.Open("vardecl");
                        _output.Field(_registry.VarKey(n.Variable));
                        WriteExpression(labels, n.Initializer);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.IfStatement:
                    {
                        var n = (BoundIfStatement)statement;
                        _output.Open("if");
                        WriteExpression(labels, n.Condition);
                        WriteStatement(labels, n.ThenStatement);
                        WriteNullableStatement(labels, n.ElseStatement);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.WhileStatement:
                    {
                        var n = (BoundWhileStatement)statement;
                        _output.Open("while");
                        WriteExpression(labels, n.Condition);
                        WriteStatement(labels, n.Body);
                        _output.Field(CoaText.Str(n.BreakLabel.Name));
                        _output.Field(CoaText.Str(n.ContinueLabel.Name));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.DoWhileStatement:
                    {
                        var n = (BoundDoWhileStatement)statement;
                        _output.Open("dowhile");
                        WriteStatement(labels, n.Body);
                        WriteExpression(labels, n.Condition);
                        _output.Field(CoaText.Str(n.BreakLabel.Name));
                        _output.Field(CoaText.Str(n.ContinueLabel.Name));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ForRangeStatement:
                    {
                        var n = (BoundForRangeStatement)statement;
                        _output.Open("for");
                        _output.Field(_registry.VarKey(n.Variable));
                        WriteExpression(labels, n.LowerBound);
                        WriteExpression(labels, n.UpperBound);
                        WriteNullableExpression(labels, n.Step);
                        WriteStatement(labels, n.Body);
                        _output.Field(CoaText.Str(n.BreakLabel.Name));
                        _output.Field(CoaText.Str(n.ContinueLabel.Name));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.LabelStatement:
                    {
                        var n = (BoundLabelStatement)statement;
                        _output.Open("label");
                        _output.Field(CoaText.Str(n.Label.Name));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.GotoStatement:
                    {
                        var n = (BoundGotoStatement)statement;
                        _output.Open("goto");
                        _output.Field(CoaText.Str(n.Label.Name));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ConditionalGotoStatement:
                    {
                        var n = (BoundConditionalGotoStatement)statement;
                        _output.Open("cgoto");
                        _output.Field(CoaText.Str(n.Label.Name));
                        WriteExpression(labels, n.Condition);
                        _output.Field(CoaText.BoolWord(n.JumpIfTrue));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ReturnStatement:
                    {
                        var n = (BoundReturnStatement)statement;
                        _output.Open("return");
                        WriteNullableExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ExpressionStatement:
                    {
                        var n = (BoundExpressionStatement)statement;
                        _output.Open("exprstmt");
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.SequencePointStatement:
                    // 调试信息降级：仅序列化内层语句。
                    WriteStatement(labels, ((BoundSequencePointStatement)statement).Statement);
                    break;
                default:
                    // 6e-G7 S2：杜绝静默产出损坏流——未覆盖节点显式失败
                    throw new NotSupportedException($"[cod] Unserializable statement kind '{statement.Kind}'");
            }
        }

        private void WriteNullableStatement(Dictionary<string, BoundLabel> labels, BoundStatement? statement)
        {
            if (statement == null)
            {
                _output.Field("-");
                return;
            }

            WriteStatement(labels, statement);
        }

        private void WriteNullableExpression(Dictionary<string, BoundLabel> labels, BoundExpression? expression)
        {
            if (expression == null)
            {
                _output.Field("-");
                return;
            }

            WriteExpression(labels, expression);
        }

        // ---------------------------------------------------------------- write: expressions

        private void WriteExpression(Dictionary<string, BoundLabel> labels, BoundExpression expression)
        {
            switch (expression.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    {
                        var n = (BoundLiteralExpression)expression;
                        _output.Open("lit");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        _output.Field(CoaText.EncodeValue(n.Value));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.VariableExpression:
                    {
                        var n = (BoundVariableExpression)expression;
                        _output.Open("var");
                        _output.Field(_registry.VarKey(n.Variable));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.AssignmentExpression:
                    {
                        var n = (BoundAssignmentExpression)expression;
                        _output.Open("assign");
                        _output.Field(_registry.VarKey(n.Variable));
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var n = (BoundCompoundAssignmentExpression)expression;
                        _output.Open("cassign");
                        _output.Field(_registry.VarKey(n.Variable));
                        WriteBinaryOperator(n.Op);
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.UnaryExpression:
                    {
                        var n = (BoundUnaryExpression)expression;
                        _output.Open("unary");
                        WriteUnaryOperator(n.Op);
                        WriteExpression(labels, n.Operand);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.BinaryExpression:
                    {
                        var n = (BoundBinaryExpression)expression;
                        _output.Open("binary");
                        WriteBinaryOperator(n.Op);
                        WriteExpression(labels, n.Left);
                        WriteExpression(labels, n.Right);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ConditionalExpression:
                    {
                        var n = (BoundConditionalExpression)expression;
                        _output.Open("cond");
                        WriteExpression(labels, n.Condition);
                        WriteExpression(labels, n.WhenTrue);
                        WriteExpression(labels, n.WhenFalse);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.CallExpression:
                    {
                        var n = (BoundCallExpression)expression;
                        _output.Open("call");
                        _output.Field(_registry.FnKey(n.Function));
                        _output.Field(n.Arguments.Length);
                        foreach (var a in n.Arguments)
                        {
                            WriteExpression(labels, a);
                        }
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ByRefArgument:
                    {
                        var n = (BoundByRefArgument)expression;
                        _output.Open("byrefarg");
                        _output.Field(n.IsRef ? "ref:" : "out:");
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ConversionExpression:
                    {
                        var n = (BoundConversionExpression)expression;
                        _output.Open("conv");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.IsExpression:
                    {
                        var n = (BoundIsExpression)expression;
                        _output.Open("istype");
                        _output.Field(CoaTypeText.TypeRef(n.TargetType));
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.AsExpression:
                    {
                        var n = (BoundAsExpression)expression;
                        _output.Open("astype");
                        _output.Field(CoaTypeText.TypeRef(n.TargetType));
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ArrayCreationExpression:
                    {
                        var n = (BoundArrayCreationExpression)expression;
                        _output.Open("arrnew");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        WriteExpression(labels, n.Length);
                        _output.Field(n.Initializers.Length);
                        foreach (var i in n.Initializers)
                        {
                            WriteExpression(labels, i);
                        }
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ObjectCreationExpression:
                    {
                        // M0-1c：对象创建 `new Foo(args)`——构造器由类型+元数重解析，仅需类型 + 实参
                        var n = (BoundObjectCreationExpression)expression;
                        _output.Open("objnew");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        _output.Field(n.Arguments.Length);
                        foreach (var arg in n.Arguments)
                        {
                            WriteExpression(labels, arg);
                        }
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ElementAccessExpression:
                    {
                        var n = (BoundElementAccessExpression)expression;
                        _output.Open("elem");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        WriteExpression(labels, n.Target);
                        WriteExpression(labels, n.Index);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var n = (BoundElementAssignmentExpression)expression;
                        _output.Open("elemassign");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        WriteExpression(labels, n.Target);
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.MemberAccessExpression:
                    {
                        // 6e-G7：字段访问随 gcls/fld 携带（Field 经 FnKey 式名字回填）；仅数组/字符串 `.Length` 时 Field == null
                        var n = (BoundMemberAccessExpression)expression;
                        _output.Open("memberacc");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        _output.Field(CoaText.Str(n.Identifier));
                        if (n.Field != null)
                        {
                            _output.Field("owner:" + n.Field.ContainingClass.FullName);
                        }

                        WriteExpression(labels, n.Target);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.MemberCallExpression:
                    {
                        var n = (BoundMemberCallExpression)expression;
                        _output.Open("membercall");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        _output.Field(CoaText.Str(n.Identifier));
                        _output.Field(n.Method != null ? _registry.FnKey(n.Method) : "-");
                        _output.Field(n.Arguments.Length);
                        WriteExpression(labels, n.Expression);
                        foreach (var a in n.Arguments)
                        {
                            WriteExpression(labels, a);
                        }
                        _output.End();
                        break;
                    }
                case BoundNodeKind.StaticTypeExpression:
                    {
                        var n = (BoundStaticTypeExpression)expression;
                        _output.Open("statictype");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ConstructorChainExpression:
                    {
                        // 6e-M33：构造链 `base(...)` / `this(...)`——kind + 目标构造 FnKey（null = 链 System.Object 0 参 no-op）+ 实参
                        var n = (BoundConstructorChainExpression)expression;
                        _output.Open("ctorchain");
                        _output.Field(n.InitializerKind == ConstructorInitializerKind.This ? "this" : "base");
                        _output.Field(n.Constructor != null ? _registry.FnKey(n.Constructor) : "-");
                        _output.Field(n.Arguments.Length);
                        foreach (var arg in n.Arguments)
                        {
                            WriteExpression(labels, arg);
                        }
                        _output.End();
                        break;
                    }
                case BoundNodeKind.ThisExpression:
                    {
                        var n = (BoundThisExpression)expression;
                        _output.Open("this");
                        _output.Field(CoaTypeText.TypeRef(n.Type));
                        _output.End();
                        break;
                    }
                case BoundNodeKind.MemberAssignmentExpression:
                    {
                    // 调试降级：memberassign 不携带调试信息（读侧不回填）。
                        var n = (BoundMemberAssignmentExpression)expression;
                        _output.Open("memberassign");
                        WriteExpression(labels, n.Target);
                        _output.Field("name:" + CoaText.Str(n.Field.Name));
                        _output.Field(CoaTypeText.TypeRef(n.Field.Type));
                        _output.Field(CoaText.BoolWord(n.Field.IsStatic));
                        WriteExpression(labels, n.Expression);
                        _output.End();
                        break;
                    }
                case BoundNodeKind.FunctionValueExpression:
                    {
                        // Step D-a：函数值/捕获闭包——FnKey（lambda 合成方法或方法组）+ 可选接收者 + 可选闭包环境类
                        var fd = (BoundFunctionValueExpression)expression;
                        _output.Open("fnval");
                        _output.Field(CoaTypeText.TypeRef(fd.Type));
                        _output.Field(_registry.FnKey(fd.Function));
                        WriteNullableExpression(labels, fd.Receiver);
                        _output.Field(fd.EnvironmentClass != null ? CoaTypeText.TypeRef(fd.EnvironmentClass) : "-");
                        _output.End();
                        break;
                    }
                case BoundNodeKind.InvocationExpression:
                    {
                        // Step D-a：函数值间接调用 `f(x)` / `obj.handler(a, b)`
                        var iv = (BoundInvocationExpression)expression;
                        _output.Open("invoc");
                        _output.Field(CoaTypeText.TypeRef(iv.Type));
                        _output.Field(iv.Arguments.Length);
                        WriteExpression(labels, iv.Callee);
                        foreach (var a in iv.Arguments)
                        {
                            WriteExpression(labels, a);
                        }
                        _output.End();
                        break;
                    }
                default:
                    throw new NotSupportedException($"[cod] Unserializable expression kind '{expression.Kind}' in fn '{_registry.CurrentFunctionName}' at {expression.Syntax}");
            }
        }

        private void WriteUnaryOperator(BoundUnaryOperator op)
        {
            _output.Open("uop");
            _output.Field(CoaText.UnaryOpText(op.Kind));
            _output.Field(CoaTypeText.TypeRef(op.OperandType));
            _output.End();
        }

        private void WriteBinaryOperator(BoundBinaryOperator op)
        {
            _output.Open("bop");
            _output.Field(CoaText.BinaryOpText(op.Kind));
            _output.Field(CoaTypeText.TypeRef(op.LeftType));
            _output.Field(CoaTypeText.TypeRef(op.RightType));
            _output.End();
        }
    }
}
