using Cocoa.CodeAnalysis.Binding;
using Binding = Cocoa.CodeAnalysis.Binding;
using Symbols = Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Cocoa.CodeGen.Interpreter
{
    // TODO: Get rid of evaluator in favor of IlEmitter
    /// <summary>
    /// 求值器
    /// </summary>
    internal sealed partial class Evaluator
    {
        private object EvaluateFormatExpression(BoundFormatExpression node)
        {
            var value = EvaluateExpression(node.Value)!;
            var text = node.Format != null ? string.Format("{0:" + node.Format + "}", value) : Convert.ToString(value)!;
            if (node.Width != null)
            {
                text = node.Width.Value < 0 ? text.PadRight(-node.Width.Value) : text.PadLeft(node.Width.Value);
            }

            return text;
        }

        private object EvaluateArrayCreationExpression(BoundArrayCreationExpression node)
        {
            var length = Convert.ToInt32(EvaluateExpression(node.Length));
            var array = new object[length];

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                array[i] = EvaluateExpression(node.Initializers[i])!;
            }

            return array;
        }

        private object EvaluateElementAccessExpression(BoundElementAccessExpression node)
        {
            var target = EvaluateExpression(node.Target)!;
            var index = Convert.ToInt32(EvaluateExpression(node.Index));

            if (node.Target.Type == TypeSymbol.String)
            {
                var text = (string)target;
                return text[index];
            }

            var array = (object[])target;
            return array[index]!;
        }

        private object EvaluateElementAssignmentExpression(BoundElementAssignmentExpression node)
        {
            var array = (object[])EvaluateExpression(node.Target.Target)!;
            var index = Convert.ToInt32(EvaluateExpression(node.Target.Index));
            var value = EvaluateExpression(node.Expression)!;

            array[index] = value;

            return value;
        }

        private object EvaluateMemberAccessExpression(BoundMemberAccessExpression node)
        {
            // 6e-M19 M3-c：类字段读（实例沿扁平化布局取槽；静态走字段槽字典）
            if (node.Field != null)
            {
                if (node.Field.IsStatic)
                {
                    EnsureStaticInit(node.Field.ContainingClass);
                    return _staticFields.TryGetValue(node.Field, out var value) ? value : DefaultValueOf(node.Field.Type)!;
                }

                var instance = (EvaluatorObject)EvaluateExpression(node.Target)!;
                var fieldValue = instance.Fields[FieldOrdinal(node.Field, instance.Class)];
                return fieldValue ?? DefaultValueOf(node.Field.Type)!;
            }

            var target = EvaluateExpression(node.Target)!;

            if (node.Identifier == "Length")
            {
                if (node.Target.Type == TypeSymbol.String)
                {
                    return ((string)target).Length;
                }

                var array = (object[])target;
                return array.Length;
            }

            throw new Exception($"Unexpected member {node.Identifier}");
        }

        private object? EvaluateMemberCallExpression(BoundMemberCallExpression node)
        {
            var method = node.Method;

            // 实例方法：用户类虚链分派 / Object 内建面 / System.Type 属性 getter
            if (method != null && !method.IsStatic)
            {
                var receiver = EvaluateExpression(node.Expression);

                if (receiver is EvaluatorObject instance)
                {
                    return DispatchOnInstance(node, method, instance);
                }

                if (method.BuiltinKind != null)
                {
                    return EvaluateBuiltinInstanceFace(method.BuiltinKind.Value, receiver!, node);
                }

                throw new Exception($"Unexpected instance call '{method.Name}' on {receiver}");
            }

            if (method?.BuiltinKind != null)
            {
                return EvaluateBuiltinCall(method, node.Arguments);
            }

            // 静态容器类方法调用（6e-M18：System.Console.WriteLine / System.Math.Max ...）：按函数调用求值；
            // 首次触碰类静态成员时触发其 .cctor（M3-c）。
            if (method != null)
            {
                if (method.ContainingClass != null && method.IsStatic)
                {
                    EnsureStaticInit(method.ContainingClass);
                }

                return EvaluateCallExpression(new BoundCallExpression(node.Syntax, method, node.Arguments));
            }

            var target = (string)EvaluateExpression(node.Expression)!;
            var start = Convert.ToInt32(EvaluateExpression(node.Arguments[0]));
            var count = Convert.ToInt32(EvaluateExpression(node.Arguments[1]));

            return target.Substring(start, count);
        }

        /// <summary>
        /// 用户类实例上的调用分派：非 base 沿运行时类链找最近实现（override 生效）；
        /// 走到内建单例即默认实现（ToString→类名等）。
        /// </summary>
        private object? DispatchOnInstance(BoundMemberCallExpression node, FunctionSymbol declared, EvaluatorObject instance)
        {
            var target = node.IsBase ? declared : ResolveDispatch(instance.Class, declared) ?? declared;

            // 6e-M23 R5：实参物化可能登记 byref 回写，基线传递 InvokeFunction 在退出时回写
            var byRefMarker = _byRefWriteBacks.Count;
            var savedSlots = _byRefSlotScope;
            _byRefSlotScope = new Dictionary<object, ByRefBox>();
            try
            {
                var argumentValues = MaterializeArguments(node);

                if (target.BuiltinKind != null)
                {
                    RunByRefWriteBacks(byRefMarker);
                    return EvaluateBuiltinDefaultOnInstance(target.BuiltinKind.Value, instance, node);
                }

                return InvokeFunction(target, instance, argumentValues, byRefMarker: byRefMarker);
            }
            finally
            {
                RunByRefWriteBacks(byRefMarker);
                _byRefSlotScope = savedSlots;
            }
        }

        /// <summary>内建默认实现的求值器语义（对齐 C# System.Object 默认行为）。</summary>
        private object? EvaluateBuiltinDefaultOnInstance(BuiltinKind kind, EvaluatorObject instance, BoundMemberCallExpression node)
        {
            switch (kind)
            {
                case BuiltinKind.ObjectToString:
                    return instance.Class.Name;
                case BuiltinKind.ObjectGetHashCode:
                    return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
                case BuiltinKind.ObjectEquals:
                    var other = EvaluateExpression(node.Arguments[0]);
                    return ReferenceEquals(instance, other);
                case BuiltinKind.ObjectGetType:
                    return new EvaluatorTypeInfo(instance.Class.FullName);
                default:
                    throw new Exception($"Unexpected builtin kind {kind} on instance");
            }
        }

        /// <summary>非用户类接收者（基元/string/CLR Type/EvaluatorTypeInfo）的内建实例面通道。</summary>
        private object? EvaluateBuiltinInstanceFace(BuiltinKind kind, object receiver, BoundMemberCallExpression node)
        {
            switch (kind)
            {
                case BuiltinKind.ObjectToString:
                    return receiver!.ToString();
                case BuiltinKind.ObjectGetHashCode:
                    return receiver!.GetHashCode();
                case BuiltinKind.ObjectEquals:
                    return object.Equals(receiver, EvaluateExpression(node.Arguments[0]));
                case BuiltinKind.ObjectGetType:
                    return receiver.GetType();

                // 6e-M19 M3-b：System.Type 只读属性（Name 一IL 同构——FullName 末段；用户类一EvaluatorTypeInfo＀
                case BuiltinKind.TypeName:
                    var fullName = FullNameOfTypeValue(receiver);
                    var lastDot = fullName.LastIndexOf('.');
                    return lastDot < 0 ? fullName : fullName.Substring(lastDot + 1);
                case BuiltinKind.TypeFullName:
                    return FullNameOfTypeValue(receiver);
                default:
                    throw new Exception($"Unexpected builtin kind {kind}");
            }
        }

        private static string FullNameOfTypeValue(object receiver) => receiver switch
        {
            System.Type clrType => clrType.FullName ?? clrType.Name,
            EvaluatorTypeInfo info => info.FullName,
            _ => throw new Exception($"Unexpected type value {receiver}"),
        };

        // ------------------------------------------------------ 6e-M19 M3-c：OOP 运行时簿记

        /// <summary>类的拍平化实例字段布局（基类字段在前、声明序；跨继承链，按类缓存）。</summary>
        private ImmutableArray<FieldSymbol> InstanceFieldsOf(NamedTypeSymbol classType)
        {
            if (_instanceFields.TryGetValue(classType, out var cached))
            {
                return cached;
            }

            var fields = new List<FieldSymbol>();
            for (var current = (NamedTypeSymbol?)classType; current != null; current = current.BaseType)
            {
                foreach (var field in current.Fields)
                {
                    if (!field.IsStatic)
                    {
                        fields.Add(field);
                    }
                }
            }

            var result = fields.ToImmutableArray();
            _instanceFields[classType] = result;
            return result;
        }

        private int FieldOrdinal(FieldSymbol field, NamedTypeSymbol classType)
        {
            var layout = InstanceFieldsOf(classType);
            for (var i = 0; i < layout.Length; i++)
            {
                if (layout[i] == field)
                {
                    return i;
                }
            }

            throw new Exception($"Field '{field.Name}' not found on '{classType.Name}'");
        }

        /// <summary>字段零值默认（语言无 null 字面量，未赋值读取给类型零值；引用类型 null）。</summary>
        private static object? DefaultValueOf(TypeSymbol type)
        {
            if (type == TypeSymbol.Int32 || type == TypeSymbol.UInt8 || type == TypeSymbol.Int8 ||
                type == TypeSymbol.Int16 || type == TypeSymbol.UInt16 || type == TypeSymbol.UInt32 ||
                type == TypeSymbol.Char || type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return 0;
            }

            if (type == TypeSymbol.Int64 || type == TypeSymbol.UInt64)
            {
                return 0L;
            }

            if (type == TypeSymbol.Double || type == TypeSymbol.Float)
            {
                return 0.0;
            }

            if (type == TypeSymbol.Boolean)
            {
                return false;
            }

            return null;
        }

        /// <summary>
        /// 静态初始化（CLR 语义近似）：首次触碰类静态成员时执行其 .cctor（字段初始化器已由绑定前缀进体）。
        /// </summary>
        private void EnsureStaticInit(NamedTypeSymbol classType)
        {
            if (!_staticsInitialized.Add(classType))
            {
                return;
            }

            var cctor = classType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
            if (cctor != null && _functions.ContainsKey(cctor))
            {
                InvokeFunction(cctor, thisReceiver: null, Array.Empty<object?>());
            }
        }

        private object EvaluateObjectCreation(BoundObjectCreationExpression node)
        {
            var classType = (NamedTypeSymbol)node.Type;
            var argumentValues = new object?[node.Arguments.Length];
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                argumentValues[i] = EvaluateExpression(node.Arguments[i]);
            }

            var instance = new EvaluatorObject(classType, new object?[InstanceFieldsOf(classType).Length]);

            // 构造函数解析：与绑定期一致（名字=类名，参数个数与类型逐一匹配）；无显式构造时隐式默认构造已在 Functions 中。
            foreach (var candidate in classType.Methods)
            {
                if (!candidate.IsConstructor || candidate.IsStatic || candidate.Parameters.Length != argumentValues.Length)
                {
                    continue;
                }

                var match = true;
                for (var i = 0; i < argumentValues.Length; i++)
                {
                    if (candidate.Parameters[i].Type != node.Arguments[i].Type)
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    // 构造体已由绑定注入 base(...) 链 + 字段初始化器前缀（隐式链对 Object 时 .ctor 自动跳过）。
                    InvokeFunction(candidate, instance, argumentValues);
                    break;
                }
            }

            return instance;
        }

        private object? EvaluateConstructorChain(BoundConstructorChainExpression node)
        {
            // 链到内建 System.Object（Constructor=null）：no-op
            if (node.Constructor == null)
            {
                return null;
            }

            // 6e-M23 R5：byref 实参回写基线 + 别名作用域（构造形参同样支持 out/ref）
            var byRefMarker = _byRefWriteBacks.Count;
            var savedSlots = _byRefSlotScope;
            _byRefSlotScope = new Dictionary<object, ByRefBox>();
            var argumentValues = new object?[node.Arguments.Length];
            try
            {
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    argumentValues[i] = EvaluateExpression(node.Arguments[i]);
                }

                InvokeFunction(node.Constructor, _thisStack.Peek(), argumentValues, byRefMarker: byRefMarker);
            }
            finally
            {
                RunByRefWriteBacks(byRefMarker);
                _byRefSlotScope = savedSlots;
            }
            return null;
        }

        private object? EvaluateMemberAssignment(BoundMemberAssignmentExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            if (node.Field.IsStatic)
            {
                EnsureStaticInit(node.Field.ContainingClass);
                _staticFields[node.Field] = value!;
                return value;
            }

            var target = (EvaluatorObject)EvaluateExpression(node.Target)!;
            target.Fields[FieldOrdinal(node.Field, target.Class)] = value;
            return value;
        }

        /// <summary>
        /// 实例函数调用环境：参数入局部帧 + this 压接收者栈（BoundThisExpression 求值返回栈顶），退出对称弹栈。
        /// </summary>
        private object? InvokeFunction(FunctionSymbol function, object? thisReceiver, object?[] argumentValues, ClosureEnvironment? existingEnvironment = null, int byRefMarker = -1)
        {
            var locals = new Dictionary<VariableSymbol, object>();
            for (var i = 0; i < function.Parameters.Length; i++)
            {
                locals[function.Parameters[i]] = argumentValues[i]!;
            }

            _locals.Push(locals);

            // 6e-M22 C5：环境对象入栈——lambda 用调用方传递的实例；宿主函数新建（捕获参数随入参播种）
            var usesEnvironment = existingEnvironment != null || function.CapturedVariables is { Count: > 0 };
            if (usesEnvironment)
            {
                _closureEnvironments.Push(existingEnvironment ?? CreateEnvironment(function, argumentValues));
            }

            if (thisReceiver != null)
            {
                _thisStack.Push(thisReceiver);
            }

            try
            {
                return EvaluateStatement(_functions[function]);
            }
            finally
            {
                if (thisReceiver != null)
                {
                    _thisStack.Pop();
                }

                if (usesEnvironment)
                {
                    _closureEnvironments.Pop();
                }

                // byref 写回须在弹出本函数帧之后执行，否则 Assign 落进将丢弃的帧（6e-M23 R5 隐性缺陷修复）
                _locals.Pop();

                if (byRefMarker >= 0)
                {
                    RunByRefWriteBacks(byRefMarker);
                }
            }
        }

        /// <summary>
        /// 虚分派（镜像 CLR 槽复用语义）：沿运行时类继承链找最近同名同签名实现— 
        /// 内建单例位于链根自然最后命中（即 C# 默认实现）。IsBase 直调绑定期解析的基类实现，不经此重派发。
        /// </summary>
        private FunctionSymbol? ResolveDispatch(NamedTypeSymbol runtimeClass, FunctionSymbol declared)
        {
            for (var current = (NamedTypeSymbol?)runtimeClass; current != null; current = current.BaseType)
            {
                foreach (var method in current.Methods)
                {
                    if (method.IsAbstract || method.IsStatic || method.IsConstructor)
                    {
                        continue;
                    }

                    if (method.Name != declared.Name || method.ReturnType != declared.ReturnType ||
                        method.Parameters.Length != declared.Parameters.Length)
                    {
                        continue;
                    }

                    var match = true;
                    for (var i = 0; i < method.Parameters.Length; i++)
                    {
                        if (method.Parameters[i].Type != declared.Parameters[i].Type)
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        private object?[] MaterializeArguments(BoundMemberCallExpression node)
        {
            var values = new object?[node.Arguments.Length];
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                values[i] = EvaluateExpression(node.Arguments[i]);
            }

            return values;
        }

        private void Assign(VariableSymbol variable, object? value)
        {
            // 6e-M23 R5：形参槽持有 ByRefBox 时写入穿透到调用方存储
            if (variable.Kind == SymbolKind.GlobalVariable)
            {
                if (_globals.TryGetValue(variable, out var existingGlobal) && existingGlobal is ByRefBox globalBox)
                {
                    globalBox.Value = value;
                    return;
                }

                _globals[variable] = value!;
            }
            else if (variable.IsCaptured)
            {
                // 6e-M22 C5：捕获变量写环境对象字段
                var slots = PeekClosureEnvironment().Slots;
                if (slots.TryGetValue(variable, out var existingCaptured) && existingCaptured is ByRefBox capturedBox)
                {
                    capturedBox.Value = value;
                    return;
                }

                slots[variable] = value!;
            }
            else
            {
                var locals = _locals.Peek();
                if (locals.TryGetValue(variable, out var existingLocal) && existingLocal is ByRefBox localBox)
                {
                    localBox.Value = value;
                    return;
                }

                locals[variable] = value!;
            }
        }

    }
}
