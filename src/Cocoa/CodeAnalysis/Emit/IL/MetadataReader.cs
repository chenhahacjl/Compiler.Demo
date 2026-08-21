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
        public ResolvedFieldInfo(string name, IlType type, bool isPublic)
        {
            Name = name;
            Type = type;
            IsPublic = isPublic;
        }

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
        public IlTypeRef? FindType(string fullName, MetadataBuilder builder)
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
        public ResolvedMethodInfo? FindMethod(string typeFullName, string methodName, string[] parameterTypeNames, MetadataBuilder builder)
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
                            parameterTypes.Add(resolved == null ? parameterType : IlType.Class(resolved));
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
    }

    /// <summary>单个引用程序集的元数据读取（TypeDef/TypeRef/MethodDef/AssemblyRef + 方法签名）。</summary>
    internal sealed class AssemblyReader
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

        private void Parse(byte[] data)
        {
            var dos = BitConverter.ToInt32(data, 0x3C);
            var pe = dos + 4 + 20;
            var optSize = BitConverter.ToUInt16(data, dos + 20);
            var is64 = BitConverter.ToUInt16(data, pe) == 0x20B;
            var dataDirStart = is64 ? pe + 112 : pe + 96;
            var clrRva = BitConverter.ToUInt32(data, dataDirStart + 14 * 8);
            var clrSize = BitConverter.ToUInt32(data, dataDirStart + 14 * 8 + 4);
            if (clrRva == 0)
            {
                throw new BadImageFormatException("Not a managed assembly.");
            }

            var sectionsStart = pe + optSize;
            var ns = BitConverter.ToUInt16(data, dos + 6);
            for (var i = 0; i < ns; i++)
            {
                var s = sectionsStart + i * 40;
                var va = BitConverter.ToUInt32(data, s + 12);
                var vsz = BitConverter.ToUInt32(data, s + 8);
                var raw = BitConverter.ToUInt32(data, s + 20);
                var rsz = BitConverter.ToUInt32(data, s + 16);
                
            }
            

            uint RvaToOffset(uint rva)
            {
                for (var i = 0; i < ns; i++)
                {
                    var s = sectionsStart + i * 40;
                    var va = BitConverter.ToUInt32(data, s + 12);
                    var vsz = BitConverter.ToUInt32(data, s + 8);
                    var raw = BitConverter.ToUInt32(data, s + 20);
                    var rsz = BitConverter.ToUInt32(data, s + 16);
                    if (rva >= va && rva < va + Math.Max(vsz, rsz))
                    {
                        return raw + (rva - va);
                    }
                }

                throw new BadImageFormatException("Invalid metadata RVA.");
            }

            var clrOffset = RvaToOffset(clrRva);
            var metadataRva = BitConverter.ToUInt32(data, (int)clrOffset + 8);
            var metadataOffset = RvaToOffset(metadataRva);
            

            // 元数据根
            var pos = (int)metadataOffset;
            if (BitConverter.ToUInt32(data, pos) != 0x424A5342)
            {
                throw new BadImageFormatException("Invalid metadata signature.");
            }

            var versionLength = BitConverter.ToInt32(data, pos + 12);
            pos += 16 + versionLength;
            pos = (pos + 3) & ~3;
            var streamCount = BitConverter.ToUInt16(data, pos + 2);
            pos += 4;

            var streams = new Dictionary<string, (uint Offset, uint Size)>();
            for (var i = 0; i < streamCount; i++)
            {
                var offset = BitConverter.ToUInt32(data, pos);
                var size = BitConverter.ToUInt32(data, pos + 4);
                var nameStart = pos + 8;
                var nameEnd = nameStart;
                while (data[nameEnd] != 0) nameEnd++;
                var name = Encoding.ASCII.GetString(data, nameStart, nameEnd - nameStart);
                streams[name] = (metadataOffset + offset, size);
                pos = (nameEnd + 4) & ~3;
            }

            if (!streams.TryGetValue("#~", out var tables) &&
                !streams.TryGetValue("#-", out tables))
            {
                throw new BadImageFormatException("Missing tables stream.");
            }

            _stringsRva = streams.TryGetValue("#Strings", out var strings) ? strings.Offset : 0;
            _blobRva = streams.TryGetValue("#Blob", out var blob) ? blob.Offset : 0;
            _guidRva = streams.TryGetValue("#GUID", out var guid) ? guid.Offset : 0;

            ParseTables(data, (int)tables.Offset);
        }

        private void ParseTables(byte[] data, int pos)
        {
            _heapSizes = data[pos + 6]; // 布局：Reserved(4) Major(1) Minor(1) HeapSizes(1) Reserved(1) Valid(8) Sorted(8)
            _valid = BitConverter.ToUInt64(data, pos + 8);
            _sorted = BitConverter.ToUInt64(data, pos + 16);
            

            var rowCounts = new int[64];
            var count = 0;
            for (var t = 0; t < 64; t++)
            {
                if ((_valid & (1UL << t)) != 0)
                {
                    rowCounts[t] = BitConverter.ToInt32(data, pos + 24 + count * 4);
                    count++;
                }
            }

            _rowCounts = rowCounts;

            // 表数据起始（行数数组后，4 对齐）
            var tablePos = pos + 24 + count * 4;

            var stringIsBig = (_heapSizes & 0x01) != 0;
            var blobIsBig = (_heapSizes & 0x04) != 0;

            // 计算每表行大小（只对需要的表）
            var typeDefCount = RowCount(0x02);
            var typeRefCount = RowCount(0x01);
            var methodDefCount = RowCount(0x06);
            var assemblyRefCount = RowCount(0x23);

            var typeDefOrRefIsBig = typeDefCount + typeRefCount > (1 << 14);
            var resolutionScopeIsBig = typeRefCount + assemblyRefCount > (1 << 14);

            // 记录各表偏移（按表号顺序遍历）
            _tableOffsets = new int[64];
            var offset = tablePos;
            
            for (var t = 0; t < 64; t++)
            {
                if ((_valid & (1UL << t)) == 0)
                {
                    continue;
                }

                _tableOffsets[t] = offset;
                offset += RowSize(t, stringIsBig, blobIsBig, typeDefOrRefIsBig, resolutionScopeIsBig) * _rowCounts[t];
            }

            

            _tableRva = (uint)tablePos;
            _tableData = data;
            _stringIsBig = stringIsBig;
            _blobIsBig = blobIsBig;
            _typeDefOrRefIsBig = typeDefOrRefIsBig;
            _resolutionScopeIsBig = resolutionScopeIsBig;

            // 读取 Assembly（表 0x20）：程序集自身标识（首选；corelib 无 AssemblyRef 表）
            var assemblyOffset = _tableOffsets[0x20];
            if (RowCount(0x20) > 0)
            {
                var row = assemblyOffset;
                _version = new Version(
                    BitConverter.ToUInt16(data, (int)row + 4),
                    BitConverter.ToUInt16(data, (int)row + 6),
                    BitConverter.ToUInt16(data, (int)row + 8),
                    BitConverter.ToUInt16(data, (int)row + 10));
                _flags = BitConverter.ToUInt32(data, (int)row + 12);
                var pktSize = blobIsBig ? 4 : 2;
                var strSize2 = stringIsBig ? 4 : 2;
                var publicKey = ReadBlob(ReadRef(data, row + 16, blobIsBig));
                _assemblyName = ReadString(ReadRef(data, row + 16 + pktSize, stringIsBig));
                _culture = ReadString(ReadRef(data, row + 16 + pktSize + strSize2, stringIsBig));

                // AssemblyRef 需要 PublicKeyToken（Flags=0x0 表示 token；0x1 才是 full public key）。
                // mscorlib 的 Assembly 表存 16 字节 ECMA 占位公钥（非真实 RSA 密钥），其 token 是固定 b77a5c561934e089；
                // 真实强名程序集按 SHA1(publicKey) 末 8 字节倒序计算。
                _publicKeyOrToken = ComputePublicKeyToken(publicKey, _flags);
                // Flags 置 0（PublicKeyToken 语义）：AssemblyRef 的 PublicKeyOrToken 列在无 PublicKey 标志时按 token 解释
                _flags = 0;
            }

            // 读取 AssemblyRef（表 0x23）——仅当无 Assembly 表时回退
            var assemblyRefOffset = _tableOffsets[0x23];
            var pktSize2 = blobIsBig ? 4 : 2;
            var strSize2b = stringIsBig ? 4 : 2;
            if (RowCount(0x20) == 0)
            {
                for (var i = 0; i < assemblyRefCount; i++)
                {
                    var row = assemblyRefOffset + i * AssemblyRefRowSize(stringIsBig, blobIsBig);
                    var major = BitConverter.ToUInt16(data, (int)row);
                    var minor = BitConverter.ToUInt16(data, (int)row + 2);
                    var build = BitConverter.ToUInt16(data, (int)row + 4);
                    var revision = BitConverter.ToUInt16(data, (int)row + 6);
                    _flags = BitConverter.ToUInt32(data, (int)row + 8);
                    _publicKeyOrToken = ReadBlob(ReadRef(data, row + 12, blobIsBig));
                    _assemblyName = ReadString(ReadRef(data, row + 12 + pktSize2, stringIsBig));
                    _culture = ReadString(ReadRef(data, row + 12 + pktSize2 + strSize2b, stringIsBig));
                    _version = new Version(major, minor, build, revision);
                }
            }
        }

        private byte[]? _tableData;
        private bool _stringIsBig;
        private bool _blobIsBig;
        private bool _typeDefOrRefIsBig;
        private bool _resolutionScopeIsBig;

        private int RowCount(int table) => _rowCounts[table];

        private int RowSize(int table, bool stringIsBig, bool blobIsBig, bool typeDefOrRefIsBig, bool resolutionScopeIsBig)
        {
            int S() => stringIsBig ? 4 : 2;
            int B() => blobIsBig ? 4 : 2;
            int G() => (_heapSizes & 0x02) != 0 ? 4 : 2; // GUID 堆索引
            int L(int count) => count > 0xFFFF ? 4 : 2; // 表行号引用
            int C(int tagBits, params int[] targets) // coded index：MAX(目标表行数) 超过 2^(16-tagBits) → 4 字节（ECMA-335 II.24.2.6）
            {
                long max = 0;
                foreach (var tg in targets)
                {
                    if (tg < 0) continue;
                    var c = RowCount(tg);
                    if (c == 0 && (tg == 0x00 || tg == 0x20)) c = 1; // Module/Assembly 恒有 1 行
                    if (c > max) max = c;
                }
                return max > (1L << (16 - tagBits)) ? 4 : 2;
            }

            var typeDefCount = RowCount(0x02);
            var typeRefCount = RowCount(0x01);
            var methodDefCount = RowCount(0x06);
            var memberRefCount = RowCount(0x0A);
            var paramCount = RowCount(0x08);
            var assemblyRefCount = RowCount(0x23);
            var fieldCount = RowCount(0x04);
            var eventCount = RowCount(0x14);
            var propertyCount = RowCount(0x17);
            var moduleRefCount = RowCount(0x1A);
            var genericParamCount = RowCount(0x2A);

            switch (table)
            {
                // 布局经 TempDiagnostics 对 System.Console.dll 逐表字节级验证（PEReader 交叉核对）
                case 0x00: return 2 + S() + 3 * G(); // Module: Generation,Name,Mvid,EncId,EncBaseId
                case 0x01: return C(2, 0x00, 0x1A, 0x23, 0x01) + 2 * S(); // TypeRef: ResolutionScope,Name,Namespace
                case 0x02: return 4 + 2 * S() + C(2, 0x02, 0x01, 0x1B) + L(fieldCount) + L(methodDefCount); // TypeDef
                case 0x03: return L(typeDefCount); // FieldPtr
                case 0x04: return 2 + S() + B(); // Field
                case 0x05: return L(methodDefCount); // MethodPtr
                case 0x06: return 4 + 2 + 2 + S() + B() + L(paramCount); // MethodDef
                case 0x07: return L(paramCount); // ParamPtr
                case 0x08: return 2 + 2 + S(); // Param
                case 0x09: return L(typeDefCount) + C(2, 0x02, 0x01, 0x1B); // InterfaceImpl: Class,Interface
                case 0x0A: return C(3, 0x02, 0x01, 0x1A, 0x06, 0x1B) + S() + B(); // MemberRef
                case 0x0B: return 1 + 1 + C(2, 0x04, 0x08, 0x17) + B(); // Constant: Type(1)+Pad(1)+Parent+Value
                case 0x0C: return C(5, 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B) + C(3, 0x06, 0x0A) + B(); // CustomAttribute
                case 0x0D: return C(1, 0x04, 0x08) + B(); // FieldMarshal（文件实证 1-bit HasFieldMarshal）
                case 0x0E: return 2 + C(2, 0x02, 0x06, 0x20) + B(); // DeclSecurity
                case 0x0F: return 2 + 4 + L(typeDefCount); // ClassLayout
                case 0x10: return 4 + L(fieldCount); // FieldLayout
                case 0x11: return B(); // StandAloneSig
                case 0x12: return L(typeDefCount) + L(eventCount); // EventMap
                case 0x13: return L(eventCount); // EventPtr
                case 0x14: return 2 + S() + C(2, 0x02, 0x01, 0x1B); // Event
                case 0x15: return L(typeDefCount) + L(propertyCount); // PropertyMap
                case 0x16: return L(propertyCount); // PropertyPtr
                case 0x17: return 2 + S() + B(); // Property
                case 0x18: return 2 + L(methodDefCount) + C(1, 0x14, 0x17); // MethodSemantics: Semantics,Method(plain),Association
                case 0x19: return L(typeDefCount) + C(1, 0x06, 0x0A) + C(1, 0x06, 0x0A); // MethodImpl: Class(TypeDef),MethodBody,MethodDeclaration(MethodDefOrRef)
                case 0x1A: return S(); // ModuleRef
                case 0x1B: return B(); // TypeSpec
                case 0x1C: return 2 + C(1, 0x04, 0x06) + S() + L(moduleRefCount); // ImplMap
                case 0x1D: return 4 + L(fieldCount); // FieldRva
                case 0x1E: return 4 + 4; // ENCLog
                case 0x1F: return 4; // ENCMap
                case 0x20: return 4 + 8 + 4 + B() + 2 * S(); // Assembly
                case 0x21: return 4; // AssemblyProcessor
                case 0x22: return 4 + 4 + 4; // AssemblyOS
                case 0x23: return 8 + 4 + B() + 2 * S() + B(); // AssemblyRef
                case 0x24: return 4 + L(assemblyRefCount); // AssemblyRefProcessor
                case 0x25: return 4 + 4 + 4 + L(assemblyRefCount); // AssemblyRefOS
                case 0x26: return 4 + S() + B(); // File
                case 0x27: return 4 + 4 + S() + S() + C(2, 0x26, 0x23, 0x27); // ExportedType
                case 0x28: return 4 + 4 + S() + C(2, 0x26, 0x23, 0x27); // ManifestResource
                case 0x29: return L(typeDefCount) + L(typeDefCount); // NestedClass
                case 0x2A: return 2 + 2 + C(1, 0x02, 0x06) + S(); // GenericParam
                case 0x2B: return C(1, 0x06, 0x0A) + B(); // MethodSpec
                case 0x2C: return C(2, 0x2A) + C(2, 0x02, 0x01, 0x1B); // GenericParamConstraint
                default: return 2;
            }
        }

        private static int AssemblyRefRowSize(bool stringIsBig, bool blobIsBig) => 8 + 4 + (blobIsBig ? 4 : 2) + 2 * (stringIsBig ? 4 : 2) + (blobIsBig ? 4 : 2);

        /// <summary>
        /// 由 Assembly 表的 PublicKey 计算 PublicKeyToken（AssemblyRef 使用）：
        /// - ECMA 占位公钥（mscorlib，16 字节 000000000400000000000000）→ 固定 token b77a5c561934e089；
        /// - 真实强名公钥 → SHA1(publicKey) 末 8 字节倒序（.NET 标准算法）。
        /// </summary>
        private static byte[] ComputePublicKeyToken(byte[] publicKey, uint assemblyFlags)
        {
            if (publicKey.Length == 0)
            {
                return Array.Empty<byte>();
            }

            // AssemblyFlags.PublicKey = 0x0001；PublicKeyToken = 0x0002
            var ecmaPlaceholder = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 };
            if (publicKey.AsSpan().SequenceEqual(ecmaPlaceholder))
            {
                return new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 };
            }

            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hash = sha1.ComputeHash(publicKey);
            var token = new byte[8];
            for (var i = 0; i < 8; i++)
            {
                token[i] = hash[hash.Length - 1 - i];
            }

            return token;
        }

        private int ReadRef(byte[] data, int pos, bool isBig) => isBig ? (int)BitConverter.ToUInt32(data, (int)pos) : BitConverter.ToUInt16(data, (int)pos);

        private string ReadString(int index) => ReadHeapString(_tableData!, (int)_stringsRva, index);
        private byte[] ReadBlob(int index) => ReadHeapBlob(_tableData!, (int)_blobRva, index);

        private static string ReadHeapString(byte[] data, int heapOffset, int index)
        {
            var pos = heapOffset + index;
            var end = pos;
            while (data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, pos, end - pos);
        }

        private static byte[] ReadHeapBlob(byte[] data, int heapOffset, int index)
        {
            var pos = heapOffset + index;
            var (length, size) = ReadCompressedInteger(data, pos);
            var result = new byte[length];
            Array.Copy(data, pos + size, result, 0, length);
            return result;
        }

        private static (int Length, int Size) ReadCompressedInteger(byte[] data, int pos)
        {
            var b0 = data[pos];
            if ((b0 & 0x80) == 0)
            {
                return (b0, 1);
            }

            if ((b0 & 0xC0) == 0x80)
            {
                return (((b0 & 0x3F) << 8) | data[pos + 1], 2);
            }

            return (((b0 & 0x3F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3], 4);
        }

        /// <summary>按 FullName 查找 TypeDef，返回其所属程序集信息（null 未找到）。</summary>
        internal AssemblyScope? FindTypeDef(string fullName)
        {
            if (_tableData == null)
            {
                return null;
            }

            var typeDefCount = RowCount(0x02);
            var methodDefCount = RowCount(0x06);
            for (var i = 0; i < typeDefCount; i++)
            {
                var row = _tableOffsets[0x02] + i * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                var nameIndex = ReadRef(_tableData, row + 4, _stringIsBig);
                var nsIndex = ReadRef(_tableData, row + 4 + (_stringIsBig ? 4 : 2), _stringIsBig);
                if (i == 0)
                {
                    
                }
                var name = ReadString(nameIndex);
                var ns = ReadString(nsIndex);
                if (i < 3 || i == 1)
                {
                    
                }
                var typeFullName = ns.Length == 0 ? name : ns + "." + name;
                if (typeFullName == fullName)
                {
                    // 仅导出 public 类型（internal/嵌套等对外不可见）
                    if ((BitConverter.ToUInt32(_tableData, row) & 0x7) != 0x1)
                    {
                        return null;
                    }

                    return new AssemblyScope(AssemblyName, Version, PublicKeyOrToken, Culture, Flags);
                }
            }

            return null;
        }

        /// <summary>是否含命名空间（类型命名空间 == ns 或在其下；6e-M15 using 解析警告用）。</summary>
        internal bool ContainsNamespace(string namespaceName)
        {
            if (_tableData == null)
            {
                return false;
            }

            var typeDefCount = RowCount(0x02);
            for (var i = 0; i < typeDefCount; i++)
            {
                var row = _tableOffsets[0x02] + i * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                var nsIndex = ReadRef(_tableData, row + 4 + (_stringIsBig ? 4 : 2), _stringIsBig);
                var ns = ReadString(nsIndex);

                if (ns == namespaceName || ns.StartsWith(namespaceName + "."))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>在 TypeDef 的方法中按名 + 参数类型名匹配方法，解析签名。</summary>
        internal ResolvedMethodSignature? FindMethod(string typeFullName, string methodName, string[] parameterTypeNames)
        {
            if (_tableData == null)
            {
                return null;
            }

            var typeDefCount = RowCount(0x02);
            var methodDefCount = RowCount(0x06);
            for (var i = 0; i < typeDefCount; i++)
            {
                var typeDefRow = _tableOffsets[0x02] + i * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                var name = ReadString(ReadRef(_tableData, typeDefRow + 4, _stringIsBig));
                var ns = ReadString(ReadRef(_tableData, typeDefRow + 4 + (_stringIsBig ? 4 : 2), _stringIsBig));
                var methodList = ReadRef(_tableData, typeDefRow + 4 + 2 * (_stringIsBig ? 4 : 2) + (_typeDefOrRefIsBig ? 4 : 2) + 2, false) - 1;
                if (ns.Length == 0 ? name != typeFullName : ns + "." + name != typeFullName)
                {
                    continue;
                }

                var endMethod = methodDefCount;
                if (i + 1 < typeDefCount)
                {
                    var nextRow = _tableOffsets[0x02] + (i + 1) * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                    endMethod = ReadRef(_tableData, nextRow + 4 + 2 * (_stringIsBig ? 4 : 2) + (_typeDefOrRefIsBig ? 4 : 2) + 2, false) - 1;
                }

                if (ns.Length == 0 ? name == typeFullName : ns + "." + name == typeFullName)
                {
                    
                }

                for (var m = methodList; m < endMethod && m < methodDefCount; m++)
                {
                    var methodRow = _tableOffsets[0x06] + m * RowSize(0x06, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                    var currentMethodName = ReadString(ReadRef(_tableData, methodRow + 8, _stringIsBig));
                    if (m == methodList)
                    {
                        
                    }
                    if (currentMethodName != methodName)
                    {
                        continue;
                    }

                    var signatureBlobIndex = ReadRef(_tableData, methodRow + 8 + (_stringIsBig ? 4 : 2), _blobIsBig);
                    ResolvedMethodSignature? signature;
                    try
                    {
                        signature = ParseMethodSignature(ReadBlob(signatureBlobIndex));
                    }
                    catch (BadImageFormatException)
                    {
                        continue; // 不支持的签名跳过该重载
                    }

                    if (Matches(signature, parameterTypeNames))
                    {
                        return signature;
                    }
                }
            }

            return null;
        }

        private static bool Matches(ResolvedMethodSignature signature, string[] parameterTypeNames)
        {
            if (signature.ParameterTypes.Count != parameterTypeNames.Length)
            {
                return false;
            }

            for (var i = 0; i < parameterTypeNames.Length; i++)
            {
                if (signature.ParameterTypes[i].FullName != parameterTypeNames[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>读取类型的 public 字段与方法签名。</summary>
        internal ResolvedTypeInfo? FindTypeInfo(string fullName)
        {
            if (_tableData == null)
            {
                return null;
            }

            var typeDefCount = RowCount(0x02);
            var methodDefCount = RowCount(0x06);
            var fieldCount = RowCount(0x04);
            var fieldListIsBig = fieldCount > 0xFFFF;
            var methodListIsBig = methodDefCount > 0xFFFF;

            for (var i = 0; i < typeDefCount; i++)
            {
                var typeDefRow = _tableOffsets[0x02] + i * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                var typeFlags = BitConverter.ToUInt32(_tableData, typeDefRow);
                var name = ReadString(ReadRef(_tableData, typeDefRow + 4, _stringIsBig));
                var ns = ReadString(ReadRef(_tableData, typeDefRow + 4 + (_stringIsBig ? 4 : 2), _stringIsBig));
                var typeFullName = ns.Length == 0 ? name : ns + "." + name;
                if (typeFullName != fullName)
                {
                    continue;
                }

                // 仅导出 public 类型（internal/嵌套等对外不可见）
                if ((typeFlags & 0x7) != 0x1)
                {
                    return null;
                }

                var fieldListOffset = typeDefRow + 4 + 2 * (_stringIsBig ? 4 : 2) + (_typeDefOrRefIsBig ? 4 : 2);
                var fieldList = ReadRef(_tableData, fieldListOffset, fieldListIsBig) - 1;
                var methodListOffset = fieldListOffset + (fieldListIsBig ? 4 : 2);
                var methodList = ReadRef(_tableData, methodListOffset, methodListIsBig) - 1;

                var fields = new List<ResolvedFieldInfo>();
                var fieldEnd = fieldCount;
                if (i + 1 < typeDefCount)
                {
                    var nextRow = _tableOffsets[0x02] + (i + 1) * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                    fieldEnd = ReadRef(_tableData, nextRow + 4 + 2 * (_stringIsBig ? 4 : 2) + (_typeDefOrRefIsBig ? 4 : 2), fieldListIsBig) - 1;
                }

                for (var f = fieldList; f < fieldEnd && f < fieldCount; f++)
                {
                    var fieldRow = _tableOffsets[0x04] + f * RowSize(0x04, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                    var fieldFlags = BitConverter.ToUInt16(_tableData, fieldRow);
                    var fieldName = ReadString(ReadRef(_tableData, fieldRow + 2, _stringIsBig));
                    var sigBlobIndex = ReadRef(_tableData, fieldRow + 2 + (_stringIsBig ? 4 : 2), _blobIsBig);
                    var sigBlob = ReadBlob(sigBlobIndex);
                    if (sigBlob.Length < 1 || sigBlob[0] != 0x06)
                    {
                        continue;
                    }

                    var pos = 1;
                    IlType fieldType;
                    try
                    {
                        fieldType = ParseType(sigBlob, ref pos);
                    }
                    catch (BadImageFormatException)
                    {
                        continue;
                    }

                    var isPublic = (fieldFlags & 0x0006) == 0x0006;
                    if (!isPublic)
                    {
                        continue; // 仅外部可见 public 字段
                    }
                    fields.Add(new ResolvedFieldInfo(fieldName, fieldType, isPublic));
                }

                var methods = new List<ResolvedMethodInfo>();
                var methodEnd = methodDefCount;
                if (i + 1 < typeDefCount)
                {
                    var nextRow = _tableOffsets[0x02] + (i + 1) * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                    methodEnd = ReadRef(_tableData, nextRow + 4 + 2 * (_stringIsBig ? 4 : 2) + (_typeDefOrRefIsBig ? 4 : 2) + (fieldListIsBig ? 4 : 2), methodListIsBig) - 1;
                }

                for (var m = methodList; m < methodEnd && m < methodDefCount; m++)
                {
                    var methodRow = _tableOffsets[0x06] + m * RowSize(0x06, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                    var methodFlags = BitConverter.ToUInt16(_tableData, methodRow + 6);
                    var methodName = ReadString(ReadRef(_tableData, methodRow + 8, _stringIsBig));
                    var signatureBlobIndex = ReadRef(_tableData, methodRow + 8 + (_stringIsBig ? 4 : 2), _blobIsBig);

                    ResolvedMethodSignature? signature;
                    try
                    {
                        signature = ParseMethodSignature(ReadBlob(signatureBlobIndex));
                    }
                    catch (BadImageFormatException)
                    {
                        continue;
                    }

                    var isPublic = (methodFlags & 0x0006) == 0x0006;
                    if (!isPublic)
                    {
                        continue; // 仅外部可见 public 方法（internal/protected/private 不导出）
                    }
                    methods.Add(new ResolvedMethodInfo(new IlTypeRef(ns, name, null), methodName, signature.ReturnType, signature.ParameterTypes, signature.IsStatic));
                }

                return new ResolvedTypeInfo(fullName, (typeFlags & 0x20) != 0, fields, methods);
            }

            return null;
        }

        /// <summary>解析方法签名 blob：返回类型 + 参数类型（ElementType → 类型名）。</summary>
        private ResolvedMethodSignature ParseMethodSignature(byte[] blob)
        {
            var pos = 0;
            var header = blob[pos++];
            var isStatic = (header & 0x20) == 0;
            var (paramCount, size) = ReadCompressedInteger(blob, pos);
            pos += size;
            var returnType = ParseType(blob, ref pos);
            var parameters = new List<IlType>();
            for (var i = 0; i < paramCount; i++)
            {
                parameters.Add(ParseType(blob, ref pos));
            }

            return new ResolvedMethodSignature(returnType, parameters, isStatic);
        }

        private IlType ParseType(byte[] blob, ref int pos)
        {
            var element = blob[pos++];
            switch (element)
            {
                case 0x01: return IlType.Void;
                case 0x02: return IlType.Boolean;
                case 0x03: return IlType.Char;
                case 0x04: return IlType.Class(new IlTypeRef("System", "SByte", null));
                case 0x05: return IlType.Class(new IlTypeRef("System", "Byte", null));
                case 0x06: return IlType.Class(new IlTypeRef("System", "Int16", null));
                case 0x07: return IlType.Class(new IlTypeRef("System", "UInt16", null));
                case 0x08: return IlType.Int32;
                case 0x09: return IlType.Class(new IlTypeRef("System", "UInt32", null));
                case 0x0A: return IlType.Class(new IlTypeRef("System", "Int64", null));
                case 0x0B: return IlType.Class(new IlTypeRef("System", "UInt64", null));
                case 0x0C: return IlType.Class(new IlTypeRef("System", "Single", null));
                case 0x0D: return IlType.Double;
                case 0x0E: return IlType.String;
                case 0x1C: return IlType.Object;
                case 0x18: return IlType.Class(new IlTypeRef("System", "IntPtr", null));
                case 0x19: return IlType.Class(new IlTypeRef("System", "UIntPtr", null));
                case 0x1D: // SZARRAY
                    return IlType.SzArrayOf(ParseType(blob, ref pos));
                case 0x12: // CLASS
                case 0x11: // VALUETYPE
                    {
                        var (codedIndex, csize) = ReadCompressedInteger(blob, pos);
                        pos += csize;
                        var fullName = ResolveTypeDefOrRef(codedIndex);
                        return IlType.Class(new IlTypeRef(NamespaceOf(fullName), NameOf(fullName), null));
                    }
                case 0x10: // BYREF
                    return ParseType(blob, ref pos);
                default:
                    throw new BadImageFormatException($"Unsupported element type 0x{element:X2} in signature.");
            }
        }

        private string ResolveTypeDefOrRef(int codedIndex)
        {
            var tag = codedIndex & 0x3;
            var rowId = codedIndex >> 2;
            if (tag == 0) // TypeDef
            {
                var row = _tableOffsets[0x02] + (rowId - 1) * RowSize(0x02, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                var name = ReadString(ReadRef(_tableData!, row + 4, _stringIsBig));
                var ns = ReadString(ReadRef(_tableData!, row + 4 + (_stringIsBig ? 4 : 2), _stringIsBig));
                return ns.Length == 0 ? name : ns + "." + name;
            }

            if (tag == 1) // TypeRef
            {
                var row = _tableOffsets[0x01] + (rowId - 1) * RowSize(0x01, _stringIsBig, _blobIsBig, _typeDefOrRefIsBig, _resolutionScopeIsBig);
                var name = ReadString(ReadRef(_tableData!, row + (_resolutionScopeIsBig ? 4 : 2), _stringIsBig));
                var ns = ReadString(ReadRef(_tableData!, row + (_resolutionScopeIsBig ? 4 : 2) + (_stringIsBig ? 4 : 2), _stringIsBig));
                return ns.Length == 0 ? name : ns + "." + name;
            }

            throw new BadImageFormatException("Unsupported TypeDefOrRef tag.");
        }

        private static string NamespaceOf(string fullName)
        {
            var dot = fullName.LastIndexOf('.');
            return dot < 0 ? "" : fullName.Substring(0, dot);
        }

        private static string NameOf(string fullName)
        {
            var dot = fullName.LastIndexOf('.');
            return dot < 0 ? fullName : fullName.Substring(dot + 1);
        }
    }

    internal sealed class AssemblyScope
    {
        public AssemblyScope(string assemblyName, Version version, byte[] publicKeyOrToken, string culture, uint flags)
        {
            AssemblyName = assemblyName;
            Version = version;
            PublicKeyOrToken = publicKeyOrToken;
            Culture = culture;
            Flags = flags;
        }

        public string AssemblyName { get; }
        public Version Version { get; }
        public byte[] PublicKeyOrToken { get; }
        public string Culture { get; }
        public uint Flags { get; }
    }

    internal sealed class ResolvedMethodSignature
    {
        public ResolvedMethodSignature(IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic)
        {
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            IsStatic = isStatic;
        }

        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public bool IsStatic { get; }
    }
}
