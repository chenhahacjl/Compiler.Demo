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
        private object? EvaluateCallExpression(BoundCallExpression node)
        {
            if (node.Function.BuiltinKind != null)
            {
                return EvaluateBuiltinCall(node.Function, node.Arguments);
            }

            var locals = new Dictionary<VariableSymbol, object>();
            var argumentValues = new object?[node.Arguments.Length];

            // 6e-M23 R5：byref 实参 copy-in/out——标记本调用的回写基纀+ 别名去重作用域，退出时统一回写
            var byRefMarker = _byRefWriteBacks.Count;
            var savedSlots = _byRefSlotScope;
            _byRefSlotScope = new Dictionary<object, ByRefBox>();

            for (var i = 0; i < node.Arguments.Length; i++)
            {
                var parameter = node.Function.Parameters[i];
                if (node.Arguments[i] is BoundByRefArgument byRefArgument)
                {
                    var box = EvaluateByRefSlot(byRefArgument);
                    locals.Add(parameter, box);
                    argumentValues[i] = box;
                    continue;
                }

                var value = EvaluateExpression(node.Arguments[i]);

                Debug.Assert(value != null);

                locals.Add(parameter, value);
                argumentValues[i] = value;
            }

            // 类静态方法直呼（using static 等）：首次触碰触发 .cctor（M3-c）
            if (node.Function.ContainingClass != null && node.Function.IsStatic)
            {
                EnsureStaticInit(node.Function.ContainingClass);
            }

            _locals.Push(locals);

            // 6e-M22 C5：宿主函数直呼路径同样需要环境对象。
            ClosureEnvironment? pushedEnvironment = null;
            if (node.Function.CapturedVariables is { Count: > 0 })
            {
                pushedEnvironment = CreateEnvironment(node.Function, argumentValues);
                _closureEnvironments.Push(pushedEnvironment);
            }

            var statement = _functions[node.Function];

            object? result;
            try
            {
                result = EvaluateStatement(statement);
            }
            finally
            {
                // byref 写回须在弹出被调者帧之后执行，否则 Assign 落进将丢弃的帧（6e-M23 R5 隐性缺陷修复）
                _locals.Pop();

                if (pushedEnvironment != null)
                {
                    _closureEnvironments.Pop();
                }

                _byRefSlotScope = savedSlots;
                RunByRefWriteBacks(byRefMarker);
            }

            return result;
        }

        /// <summary>
        /// byref 实参槽求值（6e-M23 R5）：copy-in 当前值入 Box，登记回写动作；
        /// 鍚屼竴娆¤皟鐢ㄧ殑鐩稿悓瀛樺偍锛堝埆鍚嶅幓閲嶉敭锛夊叡浜悓涓€ Box锛屼繚璇佷笁鍚庣鍒悕璇箟涓€鑷淬€?
        /// </summary>
        private ByRefBox EvaluateByRefSlot(BoundByRefArgument node)
        {
            var dedupe = _byRefSlotScope;

            switch (node.Expression)
            {
                case BoundVariableExpression variable:
                {
                    if (dedupe.TryGetValue(variable.Variable, out var sharedVariableBox))
                    {
                        return sharedVariableBox;
                    }

                    var current = EvaluateVariableExpression(variable);
                    var box = new ByRefBox(current);
                    dedupe[variable.Variable] = box;
                    _byRefWriteBacks.Add(() => Assign(variable.Variable, box.Value));
                    return box;
                }

                case BoundMemberAccessExpression member when member.Field is { IsStatic: true } staticField:
                {
                    if (dedupe.TryGetValue(staticField, out var sharedStaticBox))
                    {
                        return sharedStaticBox;
                    }

                    EnsureStaticInit(staticField.ContainingClass);
                    var current = _staticFields.TryGetValue(staticField, out var staticValue)
                        ? staticValue
                        : DefaultValueOf(staticField.Type);
                    var staticSlotBox = new ByRefBox(current);
                    dedupe[staticField] = staticSlotBox;
                    _byRefWriteBacks.Add(() =>
                    {
                        EnsureStaticInit(staticField.ContainingClass);
                        _staticFields[staticField] = staticSlotBox.Value!;
                    });
                    return staticSlotBox;
                }

                case BoundMemberAccessExpression member when member.Field != null:
                {
                    var field = member.Field;
                    var target = (EvaluatorObject)EvaluateExpression(member.Target)!;
                    var ordinal = FieldOrdinal(field, target.Class);

                    var slotKey = (target, ordinal);
                    if (dedupe.TryGetValue(slotKey, out var sharedFieldBox))
                    {
                        return sharedFieldBox;
                    }

                    var current = target.Fields[ordinal] ?? DefaultValueOf(field.Type);
                    var fieldBox = new ByRefBox(current);
                    dedupe[slotKey] = fieldBox;
                    _byRefWriteBacks.Add(() => target.Fields[ordinal] = fieldBox.Value);
                    return fieldBox;
                }

                case BoundElementAccessExpression element:
                {
                    var array = (object[])EvaluateExpression(element.Target)!;
                    var index = Convert.ToInt32(EvaluateExpression(element.Index));

                    var slotKey = (array, index);
                    if (dedupe.TryGetValue(slotKey, out var sharedElementBox))
                    {
                        return sharedElementBox;
                    }

                    var current = array[index];
                    var elementBox = new ByRefBox(current);
                    dedupe[slotKey] = elementBox;
                    _byRefWriteBacks.Add(() => array[index] = elementBox.Value!);
                    return elementBox;
                }

                default:
                    throw new Exception($"Unexpected by-ref argument target {node.Expression.Kind}");
            }
        }

        /// <summary>回写本调用登记的 byref 实参（LIFO 基线之上），异常路径同样执行。</summary>
        private void RunByRefWriteBacks(int marker)
        {
            for (var i = _byRefWriteBacks.Count - 1; i >= marker; i--)
            {
                _byRefWriteBacks[i]();
            }

            if (_byRefWriteBacks.Count > marker)
            {
                _byRefWriteBacks.RemoveRange(marker, _byRefWriteBacks.Count - marker);
            }
        }

        /// <summary>求值器显示形态：用户类实例 → 类名（对齐 IL 默认 ToString）；类型值 → 全名。</summary>
        private static string DisplayValue(object? value) => value switch
        {
            EvaluatorObject o => o.Class.Name,
            EvaluatorTypeInfo t => t.FullName,
            _ => value?.ToString() ?? "",
        };

        private object? EvaluateBuiltinCall(FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            switch (function.BuiltinKind)
            {
                case BuiltinKind.ReadLine:
                    return Console.ReadLine();
                case BuiltinKind.WriteLine:
                    var writeLineValue = EvaluateExpression(arguments[0]);
                    Console.WriteLine(DisplayValue(writeLineValue));
                    return null;
                case BuiltinKind.Write:
                    var writeValue = EvaluateExpression(arguments[0]);
                    Console.Write(DisplayValue(writeValue));
                    return null;
                case BuiltinKind.ReadKey:
                    var intercept = (bool)EvaluateExpression(arguments[0])!;
                    return Console.ReadKey(intercept).KeyChar;
                case BuiltinKind.Random:
                    var max = (int)EvaluateExpression(arguments[0])!;
                    return Random.Shared.Next(max);
                case BuiltinKind.Sleep:
                    var ms = (int)EvaluateExpression(arguments[0])!;
                    System.Threading.Thread.Sleep(ms);
                    return null;
                case BuiltinKind.TickCount:
                    return Environment.TickCount;
                case BuiltinKind.Exit:
                    var code = (int)EvaluateExpression(arguments[0])!;
                    Environment.Exit(code);
                    return null;
                case BuiltinKind.Sqrt:
                    return System.Math.Sqrt((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.Beep:
                    var frequency = (int)EvaluateExpression(arguments[0])!;
                    var duration = (int)EvaluateExpression(arguments[1])!;
                    Console.Beep(frequency, duration);
                    return null;
                case BuiltinKind.DoubleToString:
                    return Convert.ToString((double)EvaluateExpression(arguments[0])!);
                case BuiltinKind.StringFromChars:
                {
                    // 6e-G7 ③a：char[] 在 Evaluator 中为 .NET char[] 或 object[]（装箱元素）
                    var arr = EvaluateExpression(arguments[0]);
                    if (arr is char[] typedChars)
                    {
                        return new string(typedChars);
                    }

                    if (arr is object[] boxedChars)
                    {
                        var chars = new char[boxedChars.Length];
                        for (var ci = 0; ci < boxedChars.Length; ci++)
                        {
                            chars[ci] = (char)boxedChars[ci]!;
                        }

                        return new string(chars);
                    }

                    throw new InvalidOperationException($"StringFromChars: unexpected array type {arr?.GetType().Name}");
                }

                // ---- 文件 IO / 环境（6e-G7 ④）----
                case BuiltinKind.FileReadAllText:
                    return System.IO.File.ReadAllText((string)EvaluateExpression(arguments[0])!);
                case BuiltinKind.FileWriteAllText:
                    System.IO.File.WriteAllText((string)EvaluateExpression(arguments[0])!, (string)EvaluateExpression(arguments[1])!);
                    return null;
                case BuiltinKind.FileExists:
                    return System.IO.File.Exists((string)EvaluateExpression(arguments[0])!);
                case BuiltinKind.GetEnvironmentVariable:
                    return Environment.GetEnvironmentVariable((string)EvaluateExpression(arguments[0])!) ?? "";
                case BuiltinKind.GetCurrentDirectory:
                    return Directory.GetCurrentDirectory();
                case BuiltinKind.GetExecutablePath:
                    return AppContext.BaseDirectory;
                case BuiltinKind.FileDelete:
                    System.IO.File.Delete((string)EvaluateExpression(arguments[0])!);
                    return null;
                case BuiltinKind.FileCopy:
                    System.IO.File.Copy(
                        (string)EvaluateExpression(arguments[0])!,
                        (string)EvaluateExpression(arguments[1])!, true);
                    return null;
                case BuiltinKind.DirectoryExists:
                    return Directory.Exists((string)EvaluateExpression(arguments[0])!);
                case BuiltinKind.SetCurrentDirectory:
                    Directory.SetCurrentDirectory((string)EvaluateExpression(arguments[0])!);
                    return null;
                case BuiltinKind.Sha256Hash:
                {
                    var data = EvaluateExpression(arguments[0]);
                    byte[] raw;
                    if (data is byte[] bytes)
                    {
                        raw = bytes;
                    }
                    else if (data is object[] boxed)
                    {
                        raw = new byte[boxed.Length];
                        for (var bi = 0; bi < boxed.Length; bi++)
                        {
                            raw[bi] = (byte)boxed[bi]!;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Sha256Hash: unexpected input type {data?.GetType().Name}");
                    }

                    var result = System.Security.Cryptography.SHA256.HashData(raw);
                    var boxedResult = new object[result.Length];
                    for (var ri = 0; ri < result.Length; ri++)
                    {
                        boxedResult[ri] = result[ri];
                    }

                    return boxedResult;
                }
                case BuiltinKind.LaunchProcess:
                {
                    var path = (string)EvaluateExpression(arguments[0])!;
                    var args = (string)EvaluateExpression(arguments[1])!;
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc == null)
                    {
                        throw new InvalidOperationException($"LaunchProcess: failed to start '{path}'");
                    }

                    // drain both pipes (1a/A4): unread redirected output fills the 4KB pipe
                    // buffer and deadlocks parent/child forever
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit();
                    _ = stdoutTask.Result;
                    _ = stderrTask.Result;
                    return proc.ExitCode;
                }

                // 6e-M19 M2-c锛歋ystem.Object 闈闈欐€佹柟娉曪紙CLR 鐩撮€氾級
                case BuiltinKind.ObjectStaticEquals:
                    var equalsLeft = EvaluateExpression(arguments[0]);
                    var equalsRight = EvaluateExpression(arguments[1]);
                    return object.Equals(equalsLeft, equalsRight);
                case BuiltinKind.ObjectReferenceEquals:
                    var refLeft = EvaluateExpression(arguments[0]);
                    var refRight = EvaluateExpression(arguments[1]);
                    return object.ReferenceEquals(refLeft, refRight);
                default:
                    throw new InvalidOperationException($"Evaluator 后端未实现内建原语 {function.BuiltinKind}；覆盖登记见 BuiltinCoverage");
            }
        }

        /// <summary>6e-M19 M5-b：is 运行时判定——用户类沿 Class 继承链，string/CLR 对象走宿主类型。</summary>
        private object EvaluateIsExpression(BoundIsExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            if (value == null)
            {
                return false;
            }

            if (value is EvaluatorObject evalObject)
            {
                var targetClass = (Symbols.NamedTypeSymbol)node.TargetType;
                for (var current = evalObject.Class; current != null; current = current.BaseType)
                {
                    if (current == targetClass)
                    {
                        return true;
                    }
                }

                return false;
            }

            // string / CLR 对象（外部互操作值）：目标 string → 宿主类型判定；类目标对非 Evaluator 对象不可能。
            if (node.TargetType == TypeSymbol.String)
            {
                return value is string;
            }

            return false;
        }

        /// <summary>6e-M19 M5-b：as 运行时转换——命中返回原引用，失败得 null。</summary>
        private object? EvaluateAsExpression(BoundAsExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            if (value == null)
            {
                return null;
            }

            if (value is EvaluatorObject evalObject)
            {
                var targetClass = (Symbols.NamedTypeSymbol)node.TargetType;
                for (var current = evalObject.Class; current != null; current = current.BaseType)
                {
                    if (current == targetClass)
                    {
                        return value;
                    }
                }

                return null;
            }

            if (node.TargetType == TypeSymbol.String)
            {
                return value is string ? value : null;
            }

            return null;
        }

        private object? EvaluateConversionExpression(BoundConversionExpression node)
        {
            var value = EvaluateExpression(node.Expression);

            // 6e-M19 M5-a：null ↀ引用型直通（必须先于 String 分支——Convert.ToString(null) 会折叠成 ""＀
            if (node.Expression.Type == TypeSymbol.Null)
            {
                return value;
            }

            if (node.Type == TypeSymbol.Any)
            {
                return value;
            }            else if (node.Type == TypeSymbol.Boolean)
            {
                return Convert.ToBoolean(value);
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                if (value is double || value is float)
                {
                    return (int)Convert.ToDouble(value);
                }

                // 无符号大值按位模式截断（与 C# unchecked 窄化一致）
                return unchecked((int)Binding.NumericBox.ToSigned64(value!));
            }
            else if (node.Type == TypeSymbol.Int64)
            {
                if (value is double || value is float)
                {
                    return (long)Convert.ToDouble(value);
                }

                if (value is int longInt)
                {
                    // 符号扩展（与 C# (long)int 一致）
                    return (long)longInt;
                }

                if (value is uint longUint)
                {
                    return (long)longUint;
                }

                if (value is ulong longUlong)
                {
                    return unchecked((long)longUlong);
                }

                return Binding.NumericBox.ToSigned64(value!);
            }
            else if (node.Type == TypeSymbol.Char)
            {
                return Convert.ToChar(value);
            }
            else if (node.Type == TypeSymbol.UInt8)
            {
                if (value is double byteDouble)
                {
                    return unchecked((byte)(int)byteDouble);
                }

                // 无符号字节截断，与 (byte)300 == 44 语义一致
                return unchecked((byte)Binding.NumericBox.ToUnsigned64(value!));
            }
            else if (node.Type == TypeSymbol.Int8)
            {
                if (value is double sbyteDouble)
                    return unchecked((sbyte)(int)sbyteDouble);
                return unchecked((sbyte)Binding.NumericBox.ToSigned64(value!));
            }
            else if (node.Type == TypeSymbol.Int16)
            {
                if (value is double shortDouble)
                    return unchecked((short)(int)shortDouble);
                return unchecked((short)Binding.NumericBox.ToSigned64(value!));
            }
            else if (node.Type == TypeSymbol.UInt16)
            {
                if (value is double ushortDouble)
                    return unchecked((ushort)(int)ushortDouble);
                return unchecked((ushort)Binding.NumericBox.ToUnsigned64(value!));
            }
            else if (node.Type == TypeSymbol.UInt32)
            {
                if (value is double uintDouble)
                    return unchecked((uint)(long)uintDouble);
                return unchecked((uint)Binding.NumericBox.ToUnsigned64(value!));
            }
            else if (node.Type == TypeSymbol.UInt64)
            {
                if (value is double ulongDouble)
                    return unchecked((ulong)(long)ulongDouble);
                return Binding.NumericBox.ToUnsigned64(value!);
            }
            else if (node.Type == TypeSymbol.Float)
            {
                return Convert.ToSingle(value);
            }
            else if (node.Type == TypeSymbol.Double)
            {
                return Convert.ToDouble(value);
            }
            else if (node.Type == TypeSymbol.String)
            {
                return Convert.ToString(value);
            }
            else if (node.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                // 枚举底层为 int，无操作
                return Convert.ToInt32(value);
            }
            else if (node.Type is Symbols.NamedTypeSymbol)
            {
                // 6e-M19 M2-c：类间引用转换（派生→基类隐开/ 基类→派生显式）——CLR 对象直退
                return value;
            }
            else
            {
                throw new Exception($"Unexpected type {node.Type}");
            }
        }

    }
}
