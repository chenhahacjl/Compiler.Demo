using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// IL 后端的 .NET 框架引用集中管理（功能层 IL 实现的归属地，native 侧对应 RuntimeEmitterLir）：
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
            ValueType = RequireType("System.ValueType");
            StringType = RequireType("System.String");
            ConsoleKeyInfoType = RequireType("System.ConsoleKeyInfo");

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
        StringCtorCharArray = RequireMethod("System.String", ".ctor", new[] { "System.Char[]" });

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
            ConsoleBeep = RequireMethod("System.Console", "Beep", new[] { "System.Int32", "System.Int32" });
        }

        // 6e-G7 ④：新增 syscall 方法引用——惰性解析（构造器不急切 RequireMethod，
        // 避免引用程序集不含目标 API 时全量编译失败），首次使用时解析并缓存。
        private readonly Dictionary<string, IlMethodRef?> _lazyRefs = new(StringComparer.Ordinal);

        /// <summary>按需解析框架方法引用——未命中返回 null（调用方决定如何处理）。</summary>
        public IlMethodRef? ResolveMethod(string typeFullName, string methodName, string[] parameterTypeNames)
        {
            var key = typeFullName + "::" + methodName + "(" + string.Join(",", parameterTypeNames) + ")";
            if (_lazyRefs.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                var result = RequireMethod(typeFullName, methodName, parameterTypeNames);
                _lazyRefs[key] = result;
                return result;
            }
            catch
            {
                _lazyRefs[key] = null;
                return null;
            }
        }

        public IlTypeRef ObjectType { get; }
        public IlTypeRef ValueType { get; }
        public IlTypeRef StringType { get; }
        public IlTypeRef ConsoleKeyInfoType { get; }
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
        public IlMethodRef StringCtorCharArray { get; }

        // 6e-G7 ④：文件 IO / 环境
        public IlMethodRef EnvironmentGetVariable { get; }
        public IlMethodRef EnvironmentCurrentDirectory { get; }
        public IlMethodRef EnvironmentSetCurrentDirectory { get; }
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

        /// <summary>外部字段动态解析（FindField + 注册 MemberRef）；未找到返回 null。</summary>
        public IlFieldRef? FindField(string typeFullName, string fieldName)
        {
            var resolved = _reader.FindField(typeFullName, fieldName, _metadata);
            if (resolved == null)
            {
                return null;
            }

            var fieldType = ResolveClassType(resolved.Type);
            _metadata.RegisterTypeRef(resolved.DeclaringType);
            return _metadata.DefineFieldRef(resolved.DeclaringType, resolved.Name, fieldType);
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
