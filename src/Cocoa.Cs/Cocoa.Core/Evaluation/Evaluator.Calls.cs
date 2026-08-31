using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Cocoa.CodeAnalysis.Evaluation
{
    // TODO: Get rid of evaluator in favor of IlEmitter
    /// <summary>
    /// 姹傚€煎櫒
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

            // 6e-M23 R5锛歜yref 瀹炲弬 copy-in/out鈥斺€旀爣璁版湰璋冪敤鐨勫洖鍐欏熀绾?+ 鍒悕鍘婚噸浣滅敤鍩燂紝閫€鍑烘椂缁熶竴鍥炲啓
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

            // 绫婚潤鎬佹柟娉曠洿鍛硷紙using static 绛夛級锛氶娆¤Е纰拌Е鍙?.cctor锛圡3-c锛?
            if (node.Function.ContainingClass != null && node.Function.IsStatic)
            {
                EnsureStaticInit(node.Function.ContainingClass);
            }

            _locals.Push(locals);

            // 6e-M22 C5锛氬涓诲嚱鏁扮洿鍛艰矾寰勫悓鏍烽渶瑕佺幆澧冨璞?
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
        /// byref 瀹炲弬妲芥眰鍊硷紙6e-M23 R5锛夛細copy-in 褰撳墠鍊煎叆 Box锛岀櫥璁板洖鍐欏姩浣滐紱
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

        /// <summary>鍥炲啓鏈皟鐢ㄧ櫥璁扮殑 byref 瀹炲弬锛圠IFO 鍩虹嚎涔嬩笂锛夛紝寮傚父璺緞鍚屾牱鎵ц銆?/summary>
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

        /// <summary>姹傚€煎櫒鏄剧ず褰㈡€侊細鐢ㄦ埛绫诲疄渚?鈫?绫诲悕锛堝榻?IL 榛樿 ToString锛夛紱绫诲瀷鍊?鈫?鍏ㄥ悕銆?/summary>
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
                    proc!.WaitForExit();
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

        /// <summary>6e-M19 M5-b锛歩s 杩愯鏃跺垽瀹氣€斺€旂敤鎴风被娌?Class 缁ф壙閾撅紝string/CLR 瀵硅薄璧板涓荤被鍨嬨€?/summary>
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

            // string / CLR 瀵硅薄锛堝閮ㄤ簰鎿嶄綔鍊硷級锛氱洰鏍?string 鈫?瀹夸富绫诲瀷鍒ゅ畾锛涚被鐩爣瀵归潪 Evaluator 瀵硅薄涓嶅彲鑳?
            if (node.TargetType == TypeSymbol.String)
            {
                return value is string;
            }

            return false;
        }

        /// <summary>6e-M19 M5-b锛歛s 杩愯鏃惰浆鎹⑩€斺€斿懡涓繑鍥炲師寮曠敤锛屽け璐ュ緱 null銆?/summary>
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

            // 6e-M19 M5-a锛歯ull 鈫?寮曠敤鍨嬬洿閫氾紙蹇呴』鍏堜簬 String 鍒嗘敮鈥斺€擟onvert.ToString(null) 浼氭姌鍙犳垚 ""锛?
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

                // 鏃犵鍙峰ぇ鍊兼寜浣嶆ā寮忔埅鏂紙涓?C# unchecked 绐勫寲涓€鑷达級
                return unchecked((int)Binding.NumericBox.ToSigned64(value));
            }
            else if (node.Type == TypeSymbol.Int64)
            {
                if (value is double || value is float)
                {
                    return (long)Convert.ToDouble(value);
                }

                if (value is int longInt)
                {
                    // 绗﹀彿鎵╁睍锛堜笌 C# (long)int 涓€鑷达級
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

                return Binding.NumericBox.ToSigned64(value);
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

                // 鏃犵鍙峰瓧鑺傛埅鏂紝涓?(byte)300 == 44 璇箟涓€鑷?
                return unchecked((byte)Binding.NumericBox.ToUnsigned64(value));
            }
            else if (node.Type == TypeSymbol.Int8)
            {
                if (value is double sbyteDouble)
                    return unchecked((sbyte)(int)sbyteDouble);
                return unchecked((sbyte)Binding.NumericBox.ToSigned64(value));
            }
            else if (node.Type == TypeSymbol.Int16)
            {
                if (value is double shortDouble)
                    return unchecked((short)(int)shortDouble);
                return unchecked((short)Binding.NumericBox.ToSigned64(value));
            }
            else if (node.Type == TypeSymbol.UInt16)
            {
                if (value is double ushortDouble)
                    return unchecked((ushort)(int)ushortDouble);
                return unchecked((ushort)Binding.NumericBox.ToUnsigned64(value));
            }
            else if (node.Type == TypeSymbol.UInt32)
            {
                if (value is double uintDouble)
                    return unchecked((uint)(long)uintDouble);
                return unchecked((uint)Binding.NumericBox.ToUnsigned64(value));
            }
            else if (node.Type == TypeSymbol.UInt64)
            {
                if (value is double ulongDouble)
                    return unchecked((ulong)(long)ulongDouble);
                return Binding.NumericBox.ToUnsigned64(value);
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
                // 鏋氫妇搴曞眰涓?int锛屾棤鎿嶄綔
                return Convert.ToInt32(value);
            }
            else if (node.Type is Symbols.NamedTypeSymbol)
            {
                // 6e-M19 M2-c锛氱被闂村紩鐢ㄨ浆鎹紙娲剧敓鈫掑熀绫婚殣寮?/ 鍩虹被鈫掓淳鐢熸樉寮忥級鈥斺€擟LR 瀵硅薄鐩撮€?
                return value;
            }
            else
            {
                throw new Exception($"Unexpected type {node.Type}");
            }
        }

    }
}
