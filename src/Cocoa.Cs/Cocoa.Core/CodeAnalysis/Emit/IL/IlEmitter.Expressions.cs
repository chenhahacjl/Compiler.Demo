using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// IL 路径发射器：绑定树 → 自研 IL 组件（IlAssembler/MetadataBuilder/ManagedPEWriter）。
    /// 发射语义与原 Mono.Cecil 实现一致（表达式/语句 → IL 指令序列）。
    /// </summary>
    internal sealed partial class IlEmitter
    {
        private void EmitBuiltinCall(IlAssembler il, FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
        {
            if (function.BuiltinKind == BuiltinKind.Random)
            {
                // 6d-4：Random.get_Shared 是 .NET 6+ API，mscorlib 没有；改用 new Random() 双运行时兼容。
                il.Emit(IlOpCodeTable.Get("Newobj"), _framework.RandomCtor);
                foreach (var argument in arguments)
                {
                    EmitExpression(il, argument);
                }

                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.RandomNext);
                return;
            }

            foreach (var argument in arguments)
            {
                EmitExpression(il, argument);
            }

            switch (function.BuiltinKind)
            {
                case BuiltinKind.WriteLine:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleWriteLine);
                    break;
                case BuiltinKind.Write:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleWrite);
                    break;
                case BuiltinKind.ReadLine:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleReadLine);
                    break;
                case BuiltinKind.ReadKey:
                    // Console.ReadKey(intercept) → ConsoleKeyInfo（struct 栈值）→ box 后 callvirt get_KeyChar → char
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleReadKey);
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.ConsoleKeyInfoType);
                    il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.ConsoleKeyInfoKeyChar);
                    break;
                case BuiltinKind.Sleep:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ThreadSleep);
                    break;
                case BuiltinKind.TickCount:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.EnvironmentTickCount);
                    break;
                case BuiltinKind.Exit:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.EnvironmentExit);
                    break;
                case BuiltinKind.Sqrt:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathSqrt);
                    break;
                case BuiltinKind.Floor:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathFloor);
                    break;
                case BuiltinKind.Ceiling:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathCeiling);
                    break;
                case BuiltinKind.Truncate:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathTruncate);
                    break;
                case BuiltinKind.Round:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.MathRound);
                    break;
                case BuiltinKind.Beep:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConsoleBeep);
                    break;
                case BuiltinKind.Int32ToString:
                case BuiltinKind.UInt64ToString:
                    // box 值（框架 TypeRef）→ Convert.ToString(object)
                    il.Emit(
                        IlOpCodeTable.Get("Box"),
                        function.BuiltinKind == BuiltinKind.Int32ToString ? (object)_framework.Int32Type : _framework.UInt64Type);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    break;
                case BuiltinKind.Int64ToString:
                    il.Emit(
                        IlOpCodeTable.Get("Box"),
                        (object)_framework.Int64Type);
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    break;
                case BuiltinKind.DoubleToString:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToStringDouble);
                    break;
                case BuiltinKind.BooleanToString:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToStringBoolean);
                    break;
                case BuiltinKind.CharToString:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToStringChar);
                    break;
                case BuiltinKind.ParseInt64:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToInt64FromString);
                    break;
                case BuiltinKind.StringFromChars:
                    // 6e-G7 ③a：new string(char[])
                    il.Emit(IlOpCodeTable.Get("Newobj"), _framework.StringCtorCharArray);
                    break;
                case BuiltinKind.Sha256Hash:
                    // 6e-G7 ⑤a：native+IL 接入待 IlFramework 惰性引用基础设施就绪
                    throw new Exception("Sha256Hash IL emission requires lazy framework references (G7-⑤a follow-up)");
                case BuiltinKind.FileReadAllText:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "ReadAllText", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.File.ReadAllText not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileWriteAllText:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "WriteAllText", new[] { "System.String", "System.String" });
                    if (m == null) throw new Exception("System.IO.File.WriteAllText not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileExists:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "Exists", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.File.Exists not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.GetEnvironmentVariable:
                {
                    var m = _framework.ResolveMethod("System.Environment", "GetEnvironmentVariable", new[] { "System.String" });
                    if (m == null) throw new Exception("System.Environment.GetEnvironmentVariable not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.GetCurrentDirectory:
                {
                    var m = _framework.ResolveMethod("System.Environment", "get_CurrentDirectory", Array.Empty<string>());
                    if (m == null) throw new Exception("System.Environment.CurrentDirectory not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileDelete:
                {
                    var m = _framework.ResolveMethod("System.IO.File", "Delete", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.File.Delete not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.FileCopy:
                {
                    // 与 stdlib 2 参形态 Copy(src, dst) 匹配（无 overwrite；目标已存在则抛 IOException）。
                    // 此前误解析 3 参 Copy(string,string,bool) 而只压 2 实参 → 栈欠载 InvalidProgramException。
                    var m = _framework.ResolveMethod("System.IO.File", "Copy", new[] { "System.String", "System.String" });
                    if (m == null) throw new Exception("System.IO.File.Copy not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.DirectoryExists:
                {
                    var m = _framework.ResolveMethod("System.IO.Directory", "Exists", new[] { "System.String" });
                    if (m == null) throw new Exception("System.IO.Directory.Exists not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.SetCurrentDirectory:
                {
                    var m = _framework.ResolveMethod("System.Environment", "SetCurrentDirectory", new[] { "System.String" });
                    if (m == null) throw new Exception("System.Environment.SetCurrentDirectory not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }
                case BuiltinKind.GetExecutablePath:
                {
                    // AppContext.BaseDirectory 作为可执行文件路径的近似
                    var m = _framework.ResolveMethod("AppContext", "get_BaseDirectory", Array.Empty<string>());
                    if (m == null) throw new Exception("AppContext.BaseDirectory not found in framework references");
                    il.Emit(IlOpCodeTable.Get("Call"), m);
                    break;
                }

                // 6e-M19 M2-c：System.Object 静态方法（Object.Equals(a,b) / Object.ReferenceEquals(a,b)，参数 any→object）
                case BuiltinKind.ObjectStaticEquals:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectEquals);
                    break;
                case BuiltinKind.ObjectReferenceEquals:
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ObjectReferenceEquals);
                    break;
                default:
                    throw new Exception($"Unknown builtin kind {function.BuiltinKind}");
            }
        }

        private void EmitConversionExpression(IlAssembler il, BoundConversionExpression node)
        {
            EmitExpression(il, node.Expression);

            // 6e-M19 M5-a：null 字面量 → 引用型（类/接口/string/数组/any）——栈上已是 ldnull，直通
            if (node.Expression.Type == TypeSymbol.Null)
            {
                return;
            }

            // 6e-M21 Phase 4：数值↔数值系统化转换（含 char/enum 源），命中即返回
            if (TryEmitNumericConversion(il, node.Expression.Type, node.Type))
            {
                return;
            }

            if (node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.String)
            {
                var type = _framework.RequireType("System.Char");
                il.Emit(IlOpCodeTable.Get("Box"), type);
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.UInt8 && node.Type == TypeSymbol.Int32)
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.UInt8)
            {
                // 无符号字节截断，与 C# (byte)300 == 44 语义一致
                il.Emit(IlOpCodeTable.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Double ||
                node.Expression.Type == TypeSymbol.UInt8 && node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Conv_R8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Conv_R8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.Int64)
            {
                // 与 C# 一致：截断取整
                il.Emit(IlOpCodeTable.Get("Conv_I8"));
                return;
            }

            if (node.Expression.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum } && node.Type == TypeSymbol.Int64 ||
                node.Expression.Type == TypeSymbol.UInt8 && node.Type == TypeSymbol.Int64 ||
                node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Conv_I8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Int64)
            {
                // 符号扩展（C# int→long 隐式）
                il.Emit(IlOpCodeTable.Get("Conv_I8"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodeTable.Get("Conv_I4"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.Char)
            {
                il.Emit(IlOpCodeTable.Get("Conv_U2"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Int64 && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Int64"));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            // 6e-M21 Phase 7：其余数值类型 → string（装箱 + Convert.ToString）
            if ((node.Expression.Type.IsInteger && node.Expression.Type != TypeSymbol.Boolean) &&
                !node.Expression.Type.IsPlaceholder128 && node.Type == TypeSymbol.String)
            {
                var boxedName = node.Expression.Type == TypeSymbol.Int8 ? "System.SByte"
                    : node.Expression.Type == TypeSymbol.Int16 ? "System.Int16"
                    : node.Expression.Type == TypeSymbol.UInt16 ? "System.UInt16"
                    : node.Expression.Type == TypeSymbol.UInt32 ? "System.UInt32"
                    : "System.UInt64";
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType(boxedName));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Float && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Single"));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.String && node.Type == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToInt64);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodeTable.Get("Conv_I4"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Conv_U1"));
                return;
            }

            if (node.Expression.Type == TypeSymbol.Double && node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Double"));
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                return;
            }

            if (node.Expression.Type == TypeSymbol.Char && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type == TypeSymbol.Char ||
                node.Expression.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum } && node.Type == TypeSymbol.Int32 ||
                node.Expression.Type == TypeSymbol.Int32 && node.Type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                // 栈上同为 4 字节，无需指令
                return;
            }

            if (node.Expression.Type is NamedTypeSymbol fromClass && node.Type is NamedTypeSymbol toClass)
            {
                if (toClass.IsInterface &&
                    (fromClass == toClass || fromClass.IsBaseOf(toClass) || fromClass.GetAllInterfaces().Contains(toClass)))
                {
                    // 类/接口 → 其实现的接口（含继承链）：引用转换，栈上引用不变
                    return;
                }

                if (fromClass.IsInterface &&
                    (toClass.IsBaseOf(fromClass) || toClass.GetAllInterfaces().Contains(fromClass)))
                {
                    // 接口 → 类：显式向下引用转换（castclass）
                    il.Emit(IlOpCodeTable.Get("Castclass"), ToIlType(toClass));
                    return;
                }

                if (!toClass.IsInterface && toClass.IsBaseOf(fromClass))
                {
                    // 派生类 → 基类（6e-M19 M2-c 方向修正）：引用转换，栈上引用不变
                    return;
                }

                if (!fromClass.IsInterface && !toClass.IsInterface && fromClass.IsBaseOf(toClass))
                {
                    // 基类 → 派生类：显式向下引用转换（castclass）
                    il.Emit(IlOpCodeTable.Get("Castclass"), ToIlType(toClass));
                    return;
                }
            }

            EmitBoxIfValueType(il, node.Expression.Type);

            if (node.Type == TypeSymbol.Any)
            {
                // Done
            }
            else if (node.Type == TypeSymbol.Boolean)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToBoolean);
            }
            else if (node.Type == TypeSymbol.Int32)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToInt32);
            }
            else if (node.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
            }
            else
            {
                throw new System.Exception($"Unexpected conversion from {node.Expression.Type} to {node.Type}");
            }
        }

        /// <summary>值类型（bool/int/long/char/byte/double/短整型/浮点/枚举）→ 装箱为 System.Object 参数。</summary>
        private void EmitBoxIfValueType(IlAssembler il, TypeSymbol type)
        {
            if (type != TypeSymbol.Boolean && type != TypeSymbol.Int32 && type != TypeSymbol.Int64 && type != TypeSymbol.Char &&
                type != TypeSymbol.UInt8 && type != TypeSymbol.Double && type is not NamedTypeSymbol { TypeKind: TypeKind.Enum } &&
                type != TypeSymbol.Int8 && type != TypeSymbol.Int16 && type != TypeSymbol.UInt16 &&
                type != TypeSymbol.UInt32 && type != TypeSymbol.UInt64 && type != TypeSymbol.Float)
            {
                return;
            }

            var boxed = type == TypeSymbol.Boolean ? "System.Boolean"
                : type == TypeSymbol.Int32 ? "System.Int32"
                : type == TypeSymbol.Int64 ? "System.Int64"
                : type == TypeSymbol.Char ? "System.Char"
                : type == TypeSymbol.UInt8 ? "System.Byte"
                : type == TypeSymbol.Int8 ? "System.SByte"
                : type == TypeSymbol.Int16 ? "System.Int16"
                : type == TypeSymbol.UInt16 ? "System.UInt16"
                : type == TypeSymbol.UInt32 ? "System.UInt32"
                : type == TypeSymbol.UInt64 ? "System.UInt64"
                : type == TypeSymbol.Float ? "System.Single"
                : type == TypeSymbol.Double ? "System.Double"
                : "System.Int32"; // 枚举底层 int
            il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType(boxed));
        }

        private void EmitFormatExpression(IlAssembler il, BoundFormatExpression node)
        {
            var format = "{" + 0;
            if (node.Width != null)
            {
                format += "," + node.Width;
            }

            if (node.Format != null)
            {
                format += ":" + node.Format;
            }

            format += "}";

            il.Emit(IlOpCodeTable.Get("Ldstr"), format);
            EmitExpression(il, node.Value);
            EmitBoxIfValueType(il, node.Value.Type);
            il.Emit(IlOpCodeTable.Get("Call"), _framework.StringFormat);
        }

        private void EmitArrayCreationExpression(IlAssembler il, BoundArrayCreationExpression node)
        {
            EmitExpression(il, node.Length);

            var elementType = node.Type.ElementType!;
            if (IsReferenceElement(elementType))
            {
                // 6e-M22 C5+ 多播事件：类/delegate/函数类型元素数组 —— 类走 TypeDef/TypeRef，
                // 泛型实例化（Func\`N）经 TypeSpec 表注册后回填 token。
                il.Emit(IlOpCodeTable.Get("Newarr"), OperandForTypeToken(ToIlType(elementType)));
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Newarr"), _framework.RequireType(PrimitiveArrayElementTypeName(elementType)));
            }

            for (var i = 0; i < node.Initializers.Length; i++)
            {
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Ldc_I4"), i);
                EmitExpression(il, node.Initializers[i]);
                EmitElementStore(il, elementType);
            }
        }

        /// <summary>InlineType 操作数（Newarr 等）：泛型实例化必须注册为 TypeSpec（其 .Reference 指向的是
        /// 泛型定义 TypeRef，直接回填会得到错误 token）；TypeDef/TypeRef 直用；String 标记型映射框架 TypeRef。</summary>
        private object OperandForTypeToken(IlType type)
        {
            if (type.Kind == IlTypeKind.GenericInst)
            {
                return _metadata.DefineTypeSpec(type);
            }

            if (type.TypeDef != null || type.Reference != null)
            {
                return type;
            }

            if (type.Kind == IlTypeKind.String)
            {
                return _framework.RequireType("System.String");
            }

            return _metadata.DefineTypeSpec(type);
        }

        /// <summary>基元值类型元素的 Newarr 框架类型名（enum 按 int32 表示）。</summary>
        private string PrimitiveArrayElementTypeName(TypeSymbol elementType)
        {
            return elementType switch
            {
                _ when elementType == TypeSymbol.Int32 => "System.Int32",
                _ when elementType == TypeSymbol.Int64 => "System.Int64",
                _ when elementType == TypeSymbol.Char => "System.Char",
                _ when elementType == TypeSymbol.UInt8 => "System.Byte",
                _ when elementType == TypeSymbol.Double => "System.Double",
                _ when elementType == TypeSymbol.Boolean => "System.Boolean",
                _ when elementType is NamedTypeSymbol { TypeKind: TypeKind.Enum } => "System.Int32",
                _ => throw new System.NotSupportedException($"Array of '{elementType}' is not yet supported by the IL emitter."),
            };
        }

        /// <summary>引用型元素判定：非基元值类型（含函数类型 / delegate 类 / 用户类 / string / 数组 / Object/any）一律按 ref 存取。</summary>
        private static bool IsReferenceElement(TypeSymbol elementType)
        {
            if (elementType == TypeSymbol.Boolean || elementType == TypeSymbol.UInt8 || elementType == TypeSymbol.Int8 ||
                elementType == TypeSymbol.Int16 || elementType == TypeSymbol.UInt16 ||
                elementType == TypeSymbol.Int32 || elementType == TypeSymbol.UInt32 ||
                elementType == TypeSymbol.Int64 || elementType == TypeSymbol.UInt64 ||
                elementType == TypeSymbol.Char || elementType == TypeSymbol.Float || elementType == TypeSymbol.Double)
            {
                return false;
            }

            if (elementType is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return false;
            }

            return true;
        }

        private void EmitElementAccessExpression(IlAssembler il, BoundElementAccessExpression node)
        {
            EmitExpression(il, node.Target);
            EmitExpression(il, node.Index);

            if (node.Target.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringChars);
                return;
            }

            if (IsReferenceElement(node.Type))
            {
                // 6e-M22 C5+ 多播事件：函数值/delegate/类元素数组按引用加载
                il.Emit(IlOpCodeTable.Get("Ldelem_Ref"));
            }
            else if (node.Type == TypeSymbol.Char)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_U2"));
            }
            else if (node.Type == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_R8"));
            }
            else if (node.Type == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_I8"));
            }
            else if (node.Type == TypeSymbol.Boolean || node.Type == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_U1"));
            }
            else if (node.Type.ElementType != null)
            {
                throw new System.NotSupportedException("Jagged arrays are not yet supported by the IL emitter.");
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Ldelem_I4"));
            }
        }

        private void EmitElementAssignmentExpression(IlAssembler il, BoundElementAssignmentExpression node)
        {
            var temporaryLocal = AllocateTemporaryLocal(node);

            EmitExpression(il, node.Target.Target);
            EmitExpression(il, node.Target.Index);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
            EmitElementStore(il, node.Type);
            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private int AllocateTemporaryLocal(BoundExpression node, TypeSymbol? typeOverride = null)
        {
            if (!_temporaryLocalIndices.TryGetValue(node, out var index))
            {
                index = _currentFunctionLocals!.Count;
                _temporaryLocalIndices.Add(node, index);
                _currentFunctionLocals.Add(ToIlType(typeOverride ?? node.Type));
            }

            return index;
        }

        private static void EmitElementStore(IlAssembler il, TypeSymbol elementType)
        {
            if (IsReferenceElement(elementType))
            {
                // 6e-M22 C5+ 多播事件：函数值/delegate/类元素数组按引用存储
                il.Emit(IlOpCodeTable.Get("Stelem_Ref"));
            }
            else if (elementType == TypeSymbol.Boolean || elementType == TypeSymbol.UInt8)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I1"));
            }
            else if (elementType == TypeSymbol.Char)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I2"));
            }
            else if (elementType == TypeSymbol.Double)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_R8"));
            }
            else if (elementType == TypeSymbol.Int64)
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I8"));
            }
            else if (elementType.ElementType != null)
            {
                throw new System.NotSupportedException("Jagged arrays are not yet supported by the IL emitter.");
            }
            else
            {
                il.Emit(IlOpCodeTable.Get("Stelem_I4"));
            }
        }

        /// <summary>
        /// struct 字段访问/赋值取址（6e-M26 值语义）：把值类型接收者压为托管指针——
        /// 变量 → ldarga/ldloca；this → ldarga.0；嵌套字段 → 递归取址 + ldflda。
        /// 仅支持可寻址 lvalue（MVP：局部/参数/this/字段链）。
        /// </summary>
        private void EmitValueTypeReceiverAddress(IlAssembler il, BoundExpression target)
        {
            switch (target)
            {
                case BoundVariableExpression variable:
                    if (variable.Variable is ParameterSymbol parameter)
                    {
                        var argIndex = parameter.Ordinal + (_currentMethodIsInstance ? 1 : 0);
                        il.Emit(IlOpCodeTable.Get("Ldarga"), (ushort)argIndex);
                    }
                    else
                    {
                        il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)_locals[variable.Variable]);
                    }

                    return;

                case BoundThisExpression:
                    // struct 实例方法：this 已是托管指针（Point&），直接加载即可（不可 ldarga，否则变 Point&*）
                    il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)0);
                    return;

                case BoundMemberAccessExpression member when member.Field != null:
                    EmitValueTypeReceiverAddress(il, member.Target);
                    il.Emit(IlOpCodeTable.Get("Ldflda"), _fieldDefs[member.Field]);
                    return;

                default:
                    throw new System.Exception($"struct 字段访问的接收者必须可寻址：{target.Kind}");
            }
        }

        private void EmitMemberAccessExpression(IlAssembler il, BoundMemberAccessExpression node)
        {
            if (node.Field != null && node.Field.IsStatic)
            {
                il.Emit(IlOpCodeTable.Get("Ldsfld"), _fieldDefs[node.Field]);
                return;
            }

            if (node.Field != null)
            {
                if (node.Field.ContainingClass!.IsValueType)
                {
                    EmitValueTypeReceiverAddress(il, node.Target);
                }
                else
                {
                    EmitExpression(il, node.Target);
                }

                il.Emit(IlOpCodeTable.Get("Ldfld"), _fieldDefs[node.Field]);
                return;
            }

            // 非字段成员访问（如数组 Length、string 属性）：接收者须先入栈
            EmitExpression(il, node.Target);

            if (node.Target.Type == TypeSymbol.String)
            {
                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringLength);
                return;
            }

            il.Emit(IlOpCodeTable.Get("Ldlen"));
        }

        private void EmitMemberCallExpression(IlAssembler il, BoundMemberCallExpression node)
        {
            var isStatic = node.Method != null && node.Method.IsStatic;

            // 6e-M19 M2-c：System.Object 实例方法（receiver 在栈上，值类型先装箱）→ mscorlib callvirt；
            // 用户类 override 经 CLR callvirt 天然虚分派；base.Method() 用 Call 直调基类实现（防虚分派回 override）
            if (node.Method?.BuiltinKind != null && !isStatic)
            {
                var objectCallOp = node.IsBase ? "Call" : "Callvirt";
                switch (node.Method.BuiltinKind.Value)
                {
                    case BuiltinKind.ObjectToString:
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        il.Emit(IlOpCodeTable.Get(objectCallOp), _framework.ObjectToString);
                        return;
                    case BuiltinKind.ObjectGetHashCode:
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        il.Emit(IlOpCodeTable.Get(objectCallOp), _framework.ObjectGetHashCode);
                        return;
                    case BuiltinKind.ObjectEquals:
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        EmitExpression(il, node.Arguments[0]);
                        EmitBoxIfValueType(il, node.Arguments[0].Type);
                        il.Emit(IlOpCodeTable.Get(objectCallOp), _framework.ObjectEqualsInstance);
                        return;
                    case BuiltinKind.ObjectGetType:
                        // GetType 非虚：base./this. 语义一致
                        EmitExpression(il, node.Expression);
                        EmitBoxIfValueType(il, node.Expression.Type);
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.ObjectGetType);
                        return;

                    // 6e-M19 M3-b：System.Type 只读属性（receiver 为 CLR Type 引用，无装箱）。
                    // Name = FullName.Substring(FullName.LastIndexOf('.')+1)——无点时 -1+1=0 回退全名
                    case BuiltinKind.TypeName:
                        EmitExpression(il, node.Expression);
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.TypeGetFullName);
                        il.Emit(IlOpCodeTable.Get("Dup"));
                        il.Emit(IlOpCodeTable.Get("Ldc_I4_S"), (sbyte)'.');
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringLastIndexOfChar);
                        il.Emit(IlOpCodeTable.Get("Ldc_I4_1"));
                        il.Emit(IlOpCodeTable.Get("Add"));
                        il.Emit(IlOpCodeTable.Get("Call"), _framework.StringSubstringFrom);
                        return;
                    case BuiltinKind.TypeFullName:
                        EmitExpression(il, node.Expression);
                        il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.TypeGetFullName);
                        return;
                }
            }

            if (node.Method?.BuiltinKind != null)
            {
                // syscall 静态方法调用：复用内置函数分发（如 System.Runtime.Runtime.Print → Console.WriteLine）
                EmitBuiltinCall(il, node.Method, node.Arguments);
                return;
            }

            // facade 类成员调用：降级到 BCL（非泛型 → FindMethod；泛型实例化 → 直构 MemberRef）。
            // 实例性以“方法首参是否为 this 形参”判定（对齐 TryEmitFacadeBclCall；值类型接收者用托管指针 + Call）。
            // 必须在本方法统一的 receiver/参数发射之前处理，避免重复压栈导致栈不平衡。
            if (node.Method != null)
            {
                var cc = node.Method.ContainingClass;
                if (cc != null && IsFacadeRedirect(cc))
                {
                    var isInstance = node.Method.Parameters.Length > 0 && node.Method.Parameters[0].IsThisParameter;
                    var receiver = isInstance ? node.Expression : null;
                    var paramTypes = GetFacadeArgumentIlTypes(node.Method, isInstance, node.Arguments).Select(t => t.FullName).ToArray();
                    IlMethodRef? methodRef;

                    InstantiatedTypeSymbol? instType = cc as InstantiatedTypeSymbol
                        ?? (receiver?.Type as InstantiatedTypeSymbol);
                    if (instType != null)
                    {
                        methodRef = ResolveFacadeGenericMethodRef(instType, node.Method, node.Arguments, isInstance);
                    }
                    else
                    {
                        methodRef = _framework.FindMethod(FacadeBclFullName(cc), node.Identifier, paramTypes);
                    }

                    if (methodRef != null)
                    {
                        if (isInstance)
                        {
                            EmitFacadeInstanceReceiver(il, receiver!);
                        }

                        foreach (var a in node.Arguments)
                        {
                            EmitExpression(il, a);
                        }

                        var callOp = !isInstance || (receiver != null && IsValueTypeSymbol(receiver.Type)) ? "Call" : "Callvirt";
                        il.Emit(IlOpCodeTable.Get(callOp), methodRef);
                        return;
                    }
                    // 未找到 BCL 对应（Cocoa 独有成员）→ 回退下方 Cocoa 体发射
                }
            }

            if (!isStatic)
            {
                EmitExpression(il, node.Expression);
            }

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            // 动态链接（阶段 A3）：cod 容器类静态方法 → MemberRef 外部调用
            if (node.Method != null)
            {
                if (_codAssemblies.TryGetValue(node.Method, out var codAssembly))
                {
                    il.Emit(IlOpCodeTable.Get("Call"), CodMethodRef(node.Method, codAssembly));
                    return;
                }

                if (node.Method.ContainingClass!.IsExternal)
                {
                    var parameterNames = new string[node.Arguments.Length];
                    for (var i = 0; i < node.Arguments.Length; i++)
                    {
                        parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                    }

                    var methodRef = _framework.FindMethod(node.Method.ContainingClass.FullName, node.Identifier, parameterNames);
                    if (methodRef == null)
                    {
                        throw new System.Exception($"外部方法 {node.Method.ContainingClass.FullName}.{node.Identifier} 未找到。");
                    }

                    il.Emit(IlOpCodeTable.Get("Callvirt"), methodRef);
                    return;
                }

                // 静态方法：call；base.Method()：非虚 call；实例方法：callvirt 虚分派
                var op = isStatic || node.IsBase ? "Call" : "Callvirt";
                il.Emit(IlOpCodeTable.Get(op), _methods[node.Method]);
                return;
            }

            if (node.Expression.Type == TypeSymbol.String && node.Identifier == "substring")
            {
                il.Emit(IlOpCodeTable.Get("Callvirt"), _framework.StringSubstring);
                return;
            }

            throw new System.Exception($"Unexpected member call {node.Identifier}");
        }

        private void EmitMemberAssignmentExpression(IlAssembler il, BoundMemberAssignmentExpression node)
        {
            // 临时局部按字段类型分配（表达式可为 null 字面量——TypeSymbol.Null 无 IL 映射；槽语义 = 存入字段的值）
            var temporaryLocal = AllocateTemporaryLocal(node, node.Field.Type);

            if (node.Field.IsStatic)
            {
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
                il.Emit(IlOpCodeTable.Get("Stsfld"), _fieldDefs[node.Field]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                return;
            }

            if (node.Field.ContainingClass!.IsValueType)
            {
                EmitValueTypeReceiverAddress(il, node.Target);
                EmitExpression(il, node.Expression);
                il.Emit(IlOpCodeTable.Get("Dup"));
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
                il.Emit(IlOpCodeTable.Get("Stfld"), _fieldDefs[node.Field]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
                return;
            }

            EmitExpression(il, node.Target);
            EmitExpression(il, node.Expression);
            il.Emit(IlOpCodeTable.Get("Dup"));
            il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)temporaryLocal);
            il.Emit(IlOpCodeTable.Get("Stfld"), _fieldDefs[node.Field]);
            il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)temporaryLocal);
        }

        private void EmitObjectCreationExpression(IlAssembler il, BoundObjectCreationExpression node)
        {
            var classType = (NamedTypeSymbol)node.Type;

            // facade 类构造：重定向到 BCL .ctor（泛型直构 MemberRef）
            if (IsFacadeRedirect(classType))
            {
                var ctorRef = ResolveFacadeCtor(classType, node.Arguments);
                if (ctorRef != null)
                {
                    foreach (var argument in node.Arguments)
                    {
                        EmitExpression(il, argument);
                    }

                    il.Emit(IlOpCodeTable.Get("Newobj"), ctorRef);
                    return;
                }
            }

            if (classType.IsValueType)
            {
                // 6e-M26 值语义：临时局部 + ldloca + call .ctor + ldloc（非 newobj）
                var tempLocal = AllocateTemporaryLocal(node, classType);
                il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)tempLocal);
                foreach (var argument in node.Arguments)
                {
                    EmitExpression(il, argument);
                }

                var vtCtor = classType.GetMethod(classType.Name);
                if (vtCtor == null)
                {
                    throw new System.Exception($"struct {classType.Name} has no constructor.");
                }

                il.Emit(IlOpCodeTable.Get("Call"), _methods[vtCtor]);
                il.Emit(IlOpCodeTable.Get("Ldloc"), (ushort)tempLocal);
                return;
            }

            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            if (classType.IsExternal)
            {
                var parameterNames = new string[node.Arguments.Length];
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                }

                var ctorRef = _framework.FindMethod(classType.FullName, ".ctor", parameterNames);
                if (ctorRef == null)
                {
                    throw new System.Exception($"外部类型 {classType.FullName} 的构造函数未找到。");
                }

                il.Emit(IlOpCodeTable.Get("Newobj"), ctorRef);
                return;
            }

            var ctor = classType.GetMethod(classType.Name);
            if (ctor == null)
            {
                throw new System.Exception($"Class {classType.Name} has no constructor.");
            }

            il.Emit(IlOpCodeTable.Get("Newobj"), _methods[ctor]);
        }

        private void EmitThisExpression(IlAssembler il, BoundThisExpression node)
        {
            // this 恒为 arg.0：引用类型=对象引用(O)；struct 实例方法=托管指针(Point&)（调用端按 ref 传参）
            il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)0);
        }

        private void EmitConstructorChainExpression(IlAssembler il, BoundConstructorChainExpression node)
        {
            // 6e-M19 M2-c：链到内建 System.Object（无 .ctor 符号）——0 参 no-op，CLR newobj 已隐式调 object::.ctor
            if (node.Constructor == null)
            {
                return;
            }

            // this(arg0) + args → call 基类/本类 .ctor
            il.Emit(IlOpCodeTable.Get("Ldarg"), (ushort)0);
            foreach (var argument in node.Arguments)
            {
                EmitExpression(il, argument);
            }

            var target = node.Constructor;
            if (target.ContainingClass!.IsExternal)
            {
                var parameterNames = new string[node.Arguments.Length];
                for (var i = 0; i < node.Arguments.Length; i++)
                {
                    parameterNames[i] = ToIlType(node.Arguments[i].Type).FullName;
                }

                var methodRef = _framework.FindMethod(target.ContainingClass.FullName, ".ctor", parameterNames);
                if (methodRef == null)
                {
                    throw new System.Exception($"外部构造函数 {target.ContainingClass.FullName} 未找到。");
                }

                il.Emit(IlOpCodeTable.Get("Call"), methodRef);
                return;
            }

            // 6e-M25：facade 基类 .ctor 链（class MyError extends Exception → call System.Exception::.ctor）
            if (IsFacadeRedirect(target.ContainingClass!))
            {
                var methodRef = ResolveFacadeCtor(target.ContainingClass!, node.Arguments);
                if (methodRef == null)
                {
                    throw new System.Exception($"facade 构造函数 {target.ContainingClass.FullName} 未找到。");
                }

                il.Emit(IlOpCodeTable.Get("Call"), methodRef);
                return;
            }

            il.Emit(IlOpCodeTable.Get("Call"), _methods[target]);
        }
    }
}
