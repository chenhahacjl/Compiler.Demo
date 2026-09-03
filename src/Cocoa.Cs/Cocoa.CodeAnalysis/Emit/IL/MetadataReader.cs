using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>从引用程序集解析出的方法信息（供 IlEmitter 构造 MemberRef）。</summary>
    internal sealed class ResolvedMethodInfo
    {
        public ResolvedMethodInfo(IlTypeRef declaringType, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic)
        {
            DeclaringType = declaringType;
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            IsStatic = isStatic;
        }

        public IlTypeRef DeclaringType { get; }
        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public bool IsStatic { get; }
    }

    /// <summary>外部类型的成员描述（字段/方法签名）。</summary>
    internal sealed class ResolvedTypeInfo
    {
        public ResolvedTypeInfo(string fullName, bool isInterface, List<ResolvedFieldInfo> fields, List<ResolvedMethodInfo> methods)
        {
            FullName = fullName;
            IsInterface = isInterface;
            Fields = fields;
            Methods = methods;
        }

        public string FullName { get; }
        public bool IsInterface { get; }
        public List<ResolvedFieldInfo> Fields { get; }
        public List<ResolvedMethodInfo> Methods { get; }
    }

    internal sealed class ResolvedFieldInfo
    {
        public ResolvedFieldInfo(IlTypeRef declaringType, string name, IlType type, bool isPublic)
        {
            DeclaringType = declaringType;
            Name = name;
            Type = type;
            IsPublic = isPublic;
        }

        public IlTypeRef DeclaringType { get; }
        public string Name { get; }
        public IlType Type { get; }
        public bool IsPublic { get; }
    }

    /// <summary>
    /// ECMA-335 元数据读取器（最小子集）：解析 references 程序集，
    /// 按「类型 FullName + 方法名 + 参数类型名」查找方法，产出 IL 引用（IlTypeRef/IlAssemblyRef/签名类型）。
    /// </summary>
    internal sealed class MetadataReader
    {
        private readonly List<AssemblyReader> _assemblies = new List<AssemblyReader>();

        public MetadataReader(string[] references)
        {
            foreach (var reference in references)
            {
                try
                {
                    _assemblies.Add(new AssemblyReader(reference));
                }
                catch (BadImageFormatException)
                {
                    _assemblies.Add(AssemblyReader.Empty);
                }
            }
        }

        /// <summary>按 FullName（命名空间.名称）查找类型，返回其 TypeRef 描述。</summary>
        public IlTypeRef? FindType(string fullName, IIlRefIssuer builder)
        {
            foreach (var assembly in _assemblies)
            {
                var scope = assembly.FindTypeDef(fullName);
                if (scope != null)
                {
                    var assemblyRef = builder.DefineAssemblyRef(scope.AssemblyName, scope.Version, scope.PublicKeyOrToken, scope.Culture, scope.Flags);
                    var dot = fullName.LastIndexOf('.');
                    var ns = dot < 0 ? "" : fullName.Substring(0, dot);
                    var name = dot < 0 ? fullName : fullName.Substring(dot + 1);
                    return builder.DefineTypeRef(assemblyRef, ns, name);
                }
            }

            return null;
        }

        /// <summary>读取引用程序集中类型的定义（public 字段/方法签名）。</summary>
        public ResolvedTypeInfo? FindTypeInfo(string fullName)
        {
            foreach (var assembly in _assemblies)
            {
                var result = assembly.FindTypeInfo(fullName);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /// <summary>任一引用程序集中是否存在命名空间（类型命名空间 == ns 或在其下；6e-M15 using 解析警告用）。</summary>
        public bool NamespaceExists(string namespaceName)
        {
            foreach (var assembly in _assemblies)
            {
                if (assembly.ContainsNamespace(namespaceName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按「类型 FullName + 方法名 + 参数类型 FullName 列表」查找方法。</summary>
        public ResolvedMethodInfo? FindMethod(string typeFullName, string methodName, string[] parameterTypeNames, IIlRefIssuer builder)
        {
            foreach (var assembly in _assemblies)
            {
                var result = assembly.FindMethod(typeFullName, methodName, parameterTypeNames);
                if (result != null)
                {
                    var declaringType = FindType(typeFullName, builder);
                    if (declaringType == null)
                    {
                        return null;
                    }

                    var parameterTypes = new List<IlType>(result.ParameterTypes.Count);
                    foreach (var parameterType in result.ParameterTypes)
                    {
                        if (parameterType.Kind == IlTypeKind.Class)
                        {
                            var resolved = FindType(parameterType.Reference!.FullName, builder);
                            parameterTypes.Add(resolved == null ? parameterType : IlType.Class(resolved, parameterType.IsValueType));
                        }
                        else if (parameterType.Kind == IlTypeKind.SzArray)
                        {
                            parameterTypes.Add(IlType.SzArrayOf(parameterType.ElementType!));
                        }
                        else
                        {
                            parameterTypes.Add(parameterType);
                        }
                    }

                    return new ResolvedMethodInfo(declaringType, methodName, result.ReturnType, parameterTypes, result.IsStatic);
                }
            }

            return null;
        }

        /// <summary>按「类型 FullName + 字段名」查找 public 字段（facade 值类型字段重定向：Vector3.X 等）。</summary>
        public ResolvedFieldInfo? FindField(string typeFullName, string fieldName, IIlRefIssuer builder)
        {
            foreach (var assembly in _assemblies)
            {
                var result = assembly.FindFieldInstance(typeFullName, fieldName);
                if (result != null)
                {
                    var declaringType = FindType(typeFullName, builder);
                    if (declaringType == null)
                    {
                        return null;
                    }

                    var fieldType = result.Type.Kind == IlTypeKind.Class
                        ? (FindType(result.Type.Reference!.FullName, builder) is { } resolved
                            ? IlType.Class(resolved, result.Type.IsValueType)
                            : result.Type)
                        : result.Type;

                    return new ResolvedFieldInfo(declaringType, fieldName, fieldType, result.IsPublic);
                }
            }

            return null;
        }
    }

    /// <summary>单个引用程序集的元数据读取（TypeDef/TypeRef/MethodDef/AssemblyRef + 方法签名）。</summary>
    internal sealed partial class AssemblyReader
    {
        public static readonly AssemblyReader Empty = new AssemblyReader(null);

        private byte[]? _data;
        private uint _tableRva;
        private uint _stringsRva;
        private uint _blobRva;
        private uint _guidRva;
        private byte _heapSizes;
        private ulong _valid;
        private ulong _sorted;
        private int[] _rowCounts = Array.Empty<int>();
        private int[] _tableOffsets = new int[64];

        private string _assemblyName = "";
        private Version _version = new Version(0, 0, 0, 0);
        private byte[] _publicKeyOrToken = Array.Empty<byte>();
        private string _culture = "";
        private uint _flags;

        public string AssemblyName => _assemblyName;
        public Version Version => _version;
        public byte[] PublicKeyOrToken => _publicKeyOrToken;
        public string Culture => _culture;
        public uint Flags => _flags;

        public string DebugDump()
        {
            if (_data == null) return "no data";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"tableRva={_tableRva} stringsRva={_stringsRva} blobRva={_blobRva} heapSizes=0x{_heapSizes:X2}");
            sb.AppendLine($"valid=0x{_valid:X16}");
            var assemblyOffset = _tableOffsets[0x20];
            sb.AppendLine($"assemblyTableOffset={assemblyOffset}");
            if (assemblyOffset >= 0 && assemblyOffset < _data.Length)
            {
                var row = _data.AsSpan(assemblyOffset, Math.Min(64, _data.Length - assemblyOffset));
                sb.AppendLine($"assemblyRowBytes={BitConverter.ToString(row.ToArray())}");
            }
            return sb.ToString();
        }

        internal AssemblyReader(string? path)
        {
            if (path == null)
            {
                return;
            }

            var data = File.ReadAllBytes(path);
            _data = data;
            Parse(data);
        }

    }
}
