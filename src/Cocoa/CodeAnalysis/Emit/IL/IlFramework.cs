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

            ObjectEquals = RequireMethod("System.Object", "Equals", new[] { "System.Object", "System.Object" });
            ConsoleReadLine = RequireMethod("System.Console", "ReadLine", Array.Empty<string>());
            ConsoleWriteLine = RequireMethod("System.Console", "WriteLine", new[] { "System.Object" });
            StringConcat2 = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String" });
            StringConcat3 = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String", "System.String" });
            StringConcat4 = RequireMethod("System.String", "Concat", new[] { "System.String", "System.String", "System.String", "System.String" });
            StringConcatArray = RequireMethod("System.String", "Concat", new[] { "System.String[]" });
            ConvertToBoolean = RequireMethod("System.Convert", "ToBoolean", new[] { "System.Object" });
            ConvertToInt32 = RequireMethod("System.Convert", "ToInt32", new[] { "System.Object" });
            ConvertToString = RequireMethod("System.Convert", "ToString", new[] { "System.Object" });
            StringChars = RequireMethod("System.String", "get_Chars", new[] { "System.Int32" });
            StringLength = RequireMethod("System.String", "get_Length", Array.Empty<string>());
            StringSubstring = RequireMethod("System.String", "Substring", new[] { "System.Int32", "System.Int32" });
            RandomCtor = RequireMethod("System.Random", ".ctor", Array.Empty<string>());
            RandomNext = RequireMethod("System.Random", "Next", new[] { "System.Int32" });
            DebuggableAttributeCtor = RequireMethod("System.Diagnostics.DebuggableAttribute", ".ctor", new[] { "System.Boolean", "System.Boolean" });
            StringFormat = RequireMethod("System.String", "Format", new[] { "System.String", "System.Object" });
        }

        public IlTypeRef ObjectType { get; }
        public IlTypeRef StringType { get; }
        public IlMethodRef ObjectEquals { get; }
        public IlMethodRef ConsoleReadLine { get; }
        public IlMethodRef ConsoleWriteLine { get; }
        public IlMethodRef StringConcat2 { get; }
        public IlMethodRef StringConcat3 { get; }
        public IlMethodRef StringConcat4 { get; }
        public IlMethodRef StringConcatArray { get; }
        public IlMethodRef ConvertToBoolean { get; }
        public IlMethodRef ConvertToInt32 { get; }
        public IlMethodRef ConvertToString { get; }
        public IlMethodRef StringChars { get; }
        public IlMethodRef StringLength { get; }
        public IlMethodRef StringSubstring { get; }
        public IlMethodRef RandomCtor { get; }
        public IlMethodRef RandomNext { get; }
        public IlMethodRef DebuggableAttributeCtor { get; }
        public IlMethodRef StringFormat { get; }

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
                    return IlType.Class(resolved);
                }
            }

            return type;
        }
    }
}
