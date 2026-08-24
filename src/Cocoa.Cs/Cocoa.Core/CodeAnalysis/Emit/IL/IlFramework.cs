using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// IL 后端的 .NET 框架引用集中管理（功能层 IL 实现的归属地，native 侧对应 RuntimeEmitterIR）：
    /// 类型/方法解析 + 缓存，按用途分组预解析常用方法引用（Console/String/Convert/Random/Debug/Object/Format）。
    /// </summary>
    internal sealed class IlFramework
    {
        private readonly MetadataReader _reader;
        private readonly MetadataBuilder _metadata;
        private readonly Dictionary<string, IlTypeRef> _typeCache = new();

        public IlFramework(MetadataBuilder metadata, string[] references)
        {
            _metadata = metadata;
            _reader = new MetadataReader(references);

            ObjectType = RequireType("System.Object");
            StringType = RequireType("System.String");
            ConsoleKeyInfoType = RequireType("System.ConsoleKeyInfo");
            Int32Type = RequireType("System.Int32");
            Int64Type = RequireType("System.Int64");
            UInt64Type = RequireType("System.UInt64");

            ObjectEquals = RequireMethod("System.Object", "Equals", new[] { "System.Object", "System.Object" });
            ObjectToString = RequireMethod("System.Object", "ToString", Array.Empty<string>());
            ObjectGetHashCode = RequireMethod("System.Object", "GetHashCode", Array.Empty<string>());
            ObjectEqualsInstance = RequireMethod("System.Object", "Equals", new[] { "System.Object" });
            ObjectGetType = RequireMethod("System.Object", "GetType", Array.Empty<string>());
            ObjectReferenceEquals = RequireMethod("System.Object", "ReferenceEquals", new[] { "System.Object", "System.Object" });
            // net9 CoreLib 的 System.Type 无 get_Name（Name 属性非虚实现），Type.Name 经 FullName+切分组合
            TypeGetFullName = RequireMethod("System.Type", "get_FullName", Array.Empty<string>());
            StringLastIndexOfChar = RequireMethod("System.String", "LastIndexOf", new[] { "System.Char" });
            StringSubstringFrom = RequireMethod("System.String", "Substring", new[] { "System.Int32" });
            ConsoleReadLine = RequireMethod("System.Console", "ReadLine", Array.Empty<string>());
            ConsoleWriteLine = RequireMethod("System.Console", "WriteLine", new[] { "System.Object" });
            ConsoleWrite = RequireMethod("System.Console", "Write", new[] { "System.Object" });
            ConsoleReadKey = RequireMethod("System.Console", "ReadKey", new[] { "System.Boolean" });
            ConsoleKeyInfoKeyChar = RequireMethod("System.ConsoleKeyInfo", "get_KeyChar", Array.Empty<string>());
            StringConcat2 = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String" });
            StringConcat3 = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String", "System.String" });
            StringConcat4 = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String", "System.String", "System.String" });
            StringConcatArray = RequireMethod("System.String", "Concat", new[] { "System.String[]" });
            ConvertToBoolean = RequireMethod("System.Convert", "ToBoolean", new[] { "System.Object" });
            ConvertToInt32 = RequireMethod("System.Convert", "ToInt32", new[] { "System.Object" });
            ConvertToInt64 = RequireMethod("System.Convert", "ToInt64", new[] { "System.Object" });
            ConvertToString = RequireMethod("System.Convert", "ToString", new[] { "System.Object" });
            ConvertToStringDouble = RequireMethod("System.Convert", "ToString", new[] { "System.Double" });
            ConvertToStringBoolean = RequireMethod("System.Convert", "ToString", new[] { "System.Boolean" });
            ConvertToStringChar = RequireMethod("System.Convert", "ToString", new[] { "System.Char" });
            ConvertToInt64FromString = RequireMethod("System.Convert", "ToInt64", new[] { "System.String" });
            StringChars = RequireMethod("System.String", "get_Chars", new[] { "System.Int32" });
            StringLength = RequireMethod("System.String", "get_Length", Array.Empty<string>());
            StringSubstring = RequireMethod("System.String", "Substring", new[] { "System.Int32", "System.Int32" });
            RandomCtor = RequireMethod("System.Random", ".ctor", Array.Empty<string>());
            RandomNext = RequireMethod("System.Random", "Next", new[] { "System.Int32" });
            ThreadSleep = RequireMethod("System.Threading.Thread", "Sleep", new[] { "System.Int32" });
            EnvironmentTickCount = RequireMethod("System.Environment", "get_TickCount", Array.Empty<string>());
            EnvironmentExit = RequireMethod("System.Environment", "Exit", new[] { "System.Int32" });
            ObjectCtor = RequireMethod("System.Object", ".ctor", Array.Empty<string>());
            DebuggableAttributeCtor = RequireMethod("System.Diagnostics.DebuggableAttribute", ".ctor", new[] { "System.Boolean", "System.Boolean" });
            StringFormat = RequireMethod("System.String", "Format", new[] { "System.String", "System.Object" });
            MathSqrt = RequireMethod("System.Math", "Sqrt", new[] { "System.Double" });
            MathFloor = RequireMethod("System.Math", "Floor", new[] { "System.Double" });
            MathCeiling = RequireMethod("System.Math", "Ceiling", new[] { "System.Double" });
            MathTruncate = RequireMethod("System.Math", "Truncate", new[] { "System.Double" });
            MathRound = RequireMethod("System.Math", "Round", new[] { "System.Double" });
            ConsoleBeep = RequireMethod("System.Console", "Beep", new[] { "System.Int32", "System.Int32" });
        }

        public IlTypeRef ObjectType { get; }
        public IlTypeRef StringType { get; }
        public IlTypeRef ConsoleKeyInfoType { get; }
        public IlTypeRef Int32Type { get; }
        public IlTypeRef Int64Type { get; }
        public IlTypeRef UInt64Type { get; }
        public IlMethodRef ObjectCtor { get; }
        public IlMethodRef ObjectEquals { get; }

        /// <summary>6e-M19 M2-c：System.Object 实例虚/静态方法（Object 成员面 IL 发射）。</summary>
        public IlMethodRef ObjectToString { get; }
        public IlMethodRef ObjectGetHashCode { get; }
        public IlMethodRef ObjectEqualsInstance { get; }
        public IlMethodRef ObjectGetType { get; }
        public IlMethodRef ObjectReferenceEquals { get; }

        /// <summary>6e-M19 M3-b：System.Type 只读属性（Type.Name 经 FullName 切分；Type.FullName 直取）。</summary>
        public IlMethodRef TypeGetFullName { get; }
        public IlMethodRef StringLastIndexOfChar { get; }
        public IlMethodRef StringSubstringFrom { get; }
        public IlMethodRef ConsoleReadLine { get; }
        public IlMethodRef ConsoleWriteLine { get; }
        public IlMethodRef ConsoleWrite { get; }
        public IlMethodRef ConsoleReadKey { get; }
        public IlMethodRef ConsoleKeyInfoKeyChar { get; }
        public IlMethodRef StringConcat2 { get; }
        public IlMethodRef StringConcat3 { get; }
        public IlMethodRef StringConcat4 { get; }
        public IlMethodRef StringConcatArray { get; }
        public IlMethodRef ConvertToBoolean { get; }
        public IlMethodRef ConvertToInt32 { get; }
        public IlMethodRef ConvertToInt64 { get; }
        public IlMethodRef ConvertToString { get; }
        public IlMethodRef ConvertToStringDouble { get; }
        public IlMethodRef ConvertToStringBoolean { get; }
        public IlMethodRef ConvertToStringChar { get; }
        public IlMethodRef ConvertToInt64FromString { get; }
        public IlMethodRef StringChars { get; }
        public IlMethodRef StringLength { get; }
        public IlMethodRef StringSubstring { get; }
        public IlMethodRef RandomCtor { get; }
        public IlMethodRef RandomNext { get; }
        public IlMethodRef ThreadSleep { get; }
        public IlMethodRef EnvironmentTickCount { get; }
        public IlMethodRef EnvironmentExit { get; }
        public IlMethodRef DebuggableAttributeCtor { get; }
        public IlMethodRef StringFormat { get; }
        public IlMethodRef MathSqrt { get; }
        public IlMethodRef MathFloor { get; }
        public IlMethodRef MathCeiling { get; }
        public IlMethodRef MathTruncate { get; }
        public IlMethodRef MathRound { get; }
        public IlMethodRef ConsoleBeep { get; }

        /// <summary>类型引用解析 + 缓存（消发射路径上重复解析，如 Box 类型）。</summary>
        public IlTypeRef RequireType(string fullName)
        {
            if (_typeCache.TryGetValue(fullName, out var cached))
            {
                return cached;
            }

            var resolved = _reader.FindType(fullName, _metadata)
                           ?? throw new Exception($"Type '{fullName}' not found in references.");
            _typeCache[fullName] = resolved;
            return resolved;
        }

        public IlMethodRef RequireMethod(string typeFullName, string methodName, string[] parameterTypeNames)
        {
            var resolved = _reader.FindMethod(typeFullName, methodName, parameterTypeNames, _metadata)
                           ?? throw new Exception($"Method '{typeFullName}.{methodName}' not found in references.");
            return ResolveMethodRef(resolved);
        }

        /// <summary>外部成员调用动态解析（FindMethod + 注册 MemberRef）；未找到返回 null。</summary>
        public IlMethodRef? FindMethod(string typeFullName, string methodName, string[] parameterTypeNames)
        {
            var resolved = _reader.FindMethod(typeFullName, methodName, parameterTypeNames, _metadata);
            return resolved == null ? null : ResolveMethodRef(resolved);
        }

        private IlMethodRef ResolveMethodRef(ResolvedMethodInfo resolved)
        {
            var returnType = ResolveClassType(resolved.ReturnType);
            var parameterTypes = new List<IlType>(resolved.ParameterTypes.Count);
            foreach (var parameterType in resolved.ParameterTypes)
            {
                parameterTypes.Add(ResolveClassType(parameterType));
            }

            return _metadata.DefineMethodRef(resolved.DeclaringType, resolved.Name, returnType, parameterTypes, resolved.IsStatic);
        }

        /// <summary>把签名中的 Class TypeRef 解析为带 scope 的注册引用（供签名编码使用）。</summary>
        private IlType ResolveClassType(IlType type)
        {
            if (type.Kind == IlTypeKind.Class)
            {
                var resolved = _reader.FindType(type.Reference!.FullName, _metadata);
                if (resolved != null)
                {
                    return IlType.Class(resolved, type.IsValueType);
                }
            }

            return type;
        }
    }
}
