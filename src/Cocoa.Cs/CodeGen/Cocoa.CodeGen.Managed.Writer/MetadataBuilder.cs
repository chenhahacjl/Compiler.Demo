using Cocoa.CodeGen.Managed.Structure;
using Cocoa.CodeGen.Managed.Reader;
using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeGen.Managed.Writer
{
    public sealed class MetadataBuilder : IIlRefIssuer
    {
        private const string RuntimeVersion = "v4.0.30319";

        private readonly string _moduleName;
        private readonly string _assemblyName;
        private readonly Guid _mvid;

        private readonly List<IlTypeRef> _typeRefs = new List<IlTypeRef>();
        private readonly List<IlAssemblyRef> _assemblyRefs = new List<IlAssemblyRef>();
        private readonly List<IlTypeDef> _typeDefs = new List<IlTypeDef>();
        private readonly List<IlMethodRef> _memberRefs = new List<IlMethodRef>();
        private readonly List<IlFieldRef> _fieldRefs = new List<IlFieldRef>();
        private readonly List<IlCustomAttribute> _customAttributes = new List<IlCustomAttribute>();
        private readonly List<IlStandAloneSig> _standAloneSigs = new List<IlStandAloneSig>();

        /// <summary>全部方法（按类型分组：Program 在前，各 class 依序）。</summary>
        public IReadOnlyList<IlMethodDef> MethodDefs => _typeDefs.SelectMany(t => t.Methods).ToList();

        private readonly Dictionary<IlTypeRef, int> _typeRefIndex = new Dictionary<IlTypeRef, int>();
        private readonly Dictionary<IlAssemblyRef, int> _assemblyRefIndex = new Dictionary<IlAssemblyRef, int>();
        private readonly Dictionary<IlMethodRef, int> _memberRefIndex = new Dictionary<IlMethodRef, int>();        private readonly Dictionary<IlFieldRef, int> _fieldRefIndex = new Dictionary<IlFieldRef, int>();
        private readonly List<IlTypeSpec> _typeSpecs = new List<IlTypeSpec>();
        private readonly Dictionary<string, int> _strings = new Dictionary<string, int>();
        private readonly Dictionary<string, uint> _userStrings = new Dictionary<string, uint>();
        private readonly Dictionary<BlobKey, int> _blobs = new Dictionary<BlobKey, int>();

        private readonly List<byte> _stringHeap = new List<byte>();
        private readonly List<byte> _usHeap = new List<byte>();
        private readonly List<byte> _blobHeap = new List<byte>();

        // token 表号
        private const uint TypeRefTable = 0x01;
        private const uint TypeDefTable = 0x02;
        private const uint FieldTable = 0x04;
        private const uint MethodDefTable = 0x06;
        private const uint ParamTable = 0x08;
        private const uint MemberRefTable = 0x0A;
        private const uint TypeSpecTable = 0x1B;
        private const uint StandAloneSigTable = 0x11;
        private const uint AssemblyRefTable = 0x23;
        private const uint UserStringTable = 0x70;

        public MetadataBuilder(string moduleName, string assemblyName)
        {
            _moduleName = moduleName;
            _assemblyName = assemblyName;
            // 6e-M26：MVID 确定性派生（同程序多次编译字节可复现）。MVID 仅信息性、不参与程序集绑定，
            // 用 MD5(module|assembly) 的 16 字节生成稳定 GUID（对齐可复现构建语义）。
            _mvid = DeterministicMvid(moduleName, assemblyName);

            // 索引 0 预置为空条目：GetOrAddString("")/GetOrAddBlob(empty) 必须返回 0，
            // 否则 AssemblyRef 的 Culture/HashValue 会指向非空堆条目，CLR 4.8 拒绝加载。
            _stringHeap.Add(0);
            _strings.Add("", 0);
            _usHeap.Add(0);
            _blobHeap.Add(0);
            _blobs.Add(new BlobKey(Array.Empty<byte>()), 0);
        }

        /// <summary>由模块/程序集名确定性生成 MVID（16 字节 = MD5 前 16 字节）。</summary>
        private static Guid DeterministicMvid(string moduleName, string assemblyName)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(moduleName + "|" + assemblyName));
            return new Guid(hash);
        }

        // ------------------------------------------------------------------
        // 引用定义（去重）
        // ------------------------------------------------------------------

        public IlAssemblyRef DefineAssemblyRef(string name, Version version, byte[] publicKeyOrToken, string? culture, uint flags)
        {
            var reference = new IlAssemblyRef(name, version, publicKeyOrToken, culture, flags);
            if (!_assemblyRefIndex.ContainsKey(reference))
            {
                _assemblyRefIndex.Add(reference, _assemblyRefs.Count + 1);
                _assemblyRefs.Add(reference);
                // 预置 pkt blob：让 AssemblyRef 的 PublicKeyOrToken 位于 blob 堆前部（与 csc 布局一致）
                GetOrAddBlob(publicKeyOrToken);
            }

            return reference;
        }

        public IlTypeRef DefineTypeRef(IlAssemblyRef? scope, string? namespaceName, string name)
        {
            var reference = new IlTypeRef(namespaceName, name, scope);
            if (!_typeRefIndex.ContainsKey(reference))
            {
                _typeRefIndex.Add(reference, _typeRefs.Count + 1);
                _typeRefs.Add(reference);
            }

            return reference;
        }

        public IlMethodRef DefineMethodRef(IlTypeRef declaringType, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic = true)
        {
            var reference = new IlMethodRef(declaringType, name, returnType, parameterTypes, isStatic);
            if (!_memberRefIndex.ContainsKey(reference))
            {
                _memberRefIndex.Add(reference, _memberRefs.Count + 1);
                _memberRefs.Add(reference);
            }

            return reference;
        }

        /// <summary>facade 值类型字段重定向（Vector3.X 等）：外部字段登记为 MemberRef（与 _memberRefs 共用 MemberRef 表连续行号）。</summary>
        public IlFieldRef DefineFieldRef(IlTypeRef declaringType, string name, IlType fieldType)
        {
            var reference = new IlFieldRef(declaringType, name, fieldType);
            if (!_fieldRefIndex.ContainsKey(reference))
            {
                _fieldRefIndex.Add(reference, _fieldRefs.Count + 1);
                _fieldRefs.Add(reference);
            }

            return reference;
        }

        /// <summary>facade BCL 重定向：将方法引用的（可能尚未被程序引用到的）类型引用登记进 TypeRef 表，
        /// 否则 EncodeType 的 CodedIndexTypeDefOrRef 会因键缺失抛 KeyNotFoundException（仅被别处引用过的 BCL 类型才会自动登记）。</summary>
        public void RegisterTypeRef(IlTypeRef reference)
        {
            if (!_typeRefIndex.ContainsKey(reference))
            {
                _typeRefIndex.Add(reference, _typeRefs.Count + 1);
                _typeRefs.Add(reference);
            }
        }

        public void RegisterType(IlType type)
        {
            switch (type.Kind)
            {
                case IlTypeKind.Class:
                    if (type.Reference != null) RegisterTypeRef(type.Reference);
                    break;
                case IlTypeKind.ByRef:
                    RegisterType(type.ElementType!);
                    break;
                case IlTypeKind.SzArray:
                    RegisterType(type.ElementType!);
                    break;
                case IlTypeKind.GenericInst:
                    if (type.Reference != null) RegisterTypeRef(type.Reference);
                    if (type.GenericArguments != null)
                    {
                        foreach (var arg in type.GenericArguments) RegisterType(arg);
                    }
                    break;
            }
        }

        /// <summary>TypeSpec 注册（6e-M22 C4-b）：GENERICINST 类型签名 → 表行（token 经 BuildTokenMap）。</summary>
        public IlTypeSpec DefineTypeSpec(IlType instantiatedType)
        {
            var reference = new IlTypeSpec(EncodeTypeToBytes(instantiatedType));
            foreach (var existing in _typeSpecs)
            {
                if (existing.Equals(reference))
                {
                    return existing;
                }
            }

            _typeSpecs.Add(reference);
            return reference;
        }

        private byte[] EncodeTypeToBytes(IlType type)
        {
            using var stream = new MemoryStream();
            EncodeType(stream, type);
            return stream.ToArray();
        }

        /// <summary>泛型实例化父的 MemberRef（6e-M22 C4-b）：Func`N&lt;..&gt;::.ctor / ::Invoke。</summary>
        public IlMethodRef DefineMethodRef(IlTypeSpec declaringTypeSpec, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic)
        {
            var reference = new IlMethodRef(declaringTypeSpec, name, returnType, parameterTypes, isStatic);
            if (!_memberRefIndex.ContainsKey(reference))
            {
                _memberRefIndex.Add(reference, _memberRefs.Count + 1);
                _memberRefs.Add(reference);
            }

            return reference;
        }

        public void AddTypeDef(IlTypeDef typeDef) => _typeDefs.Add(typeDef);

        public void AddCustomAttribute(IlCustomAttribute attribute) => _customAttributes.Add(attribute);

        public uint GetOrAddUserString(string value)
        {
            if (_userStrings.TryGetValue(value, out var token))
            {
                return token;
            }

            var offset = _usHeap.Count;
            WriteCompressedInteger(_usHeap, value.Length * 2 + 1);
            foreach (var c in value)
            {
                _usHeap.Add((byte)c);
                _usHeap.Add((byte)(c >> 8));
            }
            _usHeap.Add(GetUserStringTrailingByte(value));

            token = UserStringTable << 24 | (uint)offset;
            _userStrings.Add(value, token);
            return token;
        }

        /// <summary>注册 StandAloneSig（局部变量签名等）并返回引用（token 由 <see cref="BuildTokenMap"/> 解析）。</summary>
        public IlStandAloneSig AddStandAloneSig(byte[] signatureBlob)
        {
            var reference = new IlStandAloneSig(signatureBlob);
            foreach (var existing in _standAloneSigs)
            {
                if (existing.Equals(reference))
                {
                    return existing;
                }
            }

            _standAloneSigs.Add(reference);
            return reference;
        }

        /// <summary>MemberRef 行号（发射侧构造 CustomAttribute ctor 编码用）。</summary>
        public int GetMemberRefRow(IlMethodRef reference) => _memberRefIndex[reference];

        /// <summary>TypeDef 行号（<Module> 行 1，其后按 _typeDefs 序 2..）。</summary>
        public int TypeDefRowOf(IlTypeDef typeDef) => 2 + _typeDefs.IndexOf(typeDef);

        /// <summary>MethodDef 行号（= _typeDefs 各类型方法扁平序 +1，与 Serialize 一致）。</summary>
        public int MethodDefRowOf(IlMethodDef method) => MethodDefs.ToList().IndexOf(method) + 1;

        /// <summary>构建 token 映射（IlAssembler 回填用）：引用对象 → 元数据 token。</summary>
        public Dictionary<object, uint> BuildTokenMap()
        {
            var map = new Dictionary<object, uint>();
            for (var i = 0; i < _typeRefs.Count; i++)
            {
                map[_typeRefs[i]] = TypeRefTable << 24 | (uint)(i + 1);
            }

            // TypeDef 表第 1 行为 <Module>（与下方写表/InterfaceImpl 行号约定一致），
            // 实际类型自第 2 行起——此前从 1 起导致所有 TypeDef token 偏小 1
            // （castclass/isinst 类目标全错位一行，6e-M19 M5-b 首次运行时验证暴露）。
            var typeDefRow = 2;
            foreach (var typeDef in _typeDefs)
            {
                map[typeDef] = TypeDefTable << 24 | (uint)typeDefRow;
                typeDefRow++;
            }

            var methodDefs = MethodDefs;
            for (var i = 0; i < methodDefs.Count; i++)
            {
                map[methodDefs[i]] = MethodDefTable << 24 | (uint)(i + 1);
            }

            var fieldRow = 1;
            foreach (var typeDef in _typeDefs)
            {
                foreach (var field in typeDef.Fields)
                {
                    map[field] = FieldTable << 24 | (uint)fieldRow;
                    fieldRow++;
                }
            }

            for (var i = 0; i < _memberRefs.Count; i++)
            {
                map[_memberRefs[i]] = MemberRefTable << 24 | (uint)(i + 1);
            }

            for (var i = 0; i < _fieldRefs.Count; i++)
            {
                map[_fieldRefs[i]] = MemberRefTable << 24 | (uint)(_memberRefs.Count + 1 + i);
            }

            for (var i = 0; i < _typeSpecs.Count; i++)
            {
                map[_typeSpecs[i]] = TypeSpecTable << 24 | (uint)(i + 1);
            }

            for (var i = 0; i < _standAloneSigs.Count; i++)
            {
                map[_standAloneSigs[i]] = StandAloneSigTable << 24 | (uint)(i + 1);
            }

            return map;
        }

        private int GetOrAddBlob(byte[] blob)
        {
            var key = new BlobKey(blob);
            if (_blobs.TryGetValue(key, out var index))
            {
                return index;
            }

            index = _blobHeap.Count;
            _blobs.Add(key, index);
            WriteCompressedInteger(_blobHeap, blob.Length);
            _blobHeap.AddRange(blob);
            return index;
        }

        private int GetOrAddString(string value)
        {
            if (_strings.TryGetValue(value, out var index))
            {
                return index;
            }

            index = _stringHeap.Count;
            _strings.Add(value, index);
            var bytes = Encoding.UTF8.GetBytes(value);
            _stringHeap.AddRange(bytes);
            _stringHeap.Add(0);
            return index;
        }

        private readonly struct BlobKey : IEquatable<BlobKey>
        {
            private readonly byte[] _bytes;

            public BlobKey(byte[] bytes) => _bytes = bytes;

            public bool Equals(BlobKey other) => _bytes.SequenceEqual(other._bytes);
            public override bool Equals(object? obj) => obj is BlobKey other && Equals(other);
            public override int GetHashCode()
            {
                var hash = 17;
                foreach (var b in _bytes)
                {
                    hash = hash * 31 + b;
                }

                return hash;
            }
        }

        // ------------------------------------------------------------------
        // 方法定义
        // ------------------------------------------------------------------

        /// <summary>把方法挂到所属类型（顶层函数挂 Program）。</summary>
        public void AddMethodDef(IlTypeDef typeDef, IlMethodDef method) => typeDef.Methods.Add(method);

        /// <summary>把字段挂到所属类型。</summary>
        public void AddFieldDef(IlTypeDef typeDef, IlFieldDef field) => typeDef.Fields.Add(field);

        // ------------------------------------------------------------------
        // 签名编码
        // ------------------------------------------------------------------

        public byte[] EncodeMethodSignature(IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic = true, bool explicitThis = false)
        {
            // 注意：Extern 方法的签名必须使用默认调用约定（0x00），本机调用约定由 ImplMap 的 MappingFlags 表达。
            using var stream = new MemoryStream();
            // 值类型实例方法：this 为托管指针，须用 EXPLICITTHIS（0x40）并在参数首位显式给出 byref<T>（HASTHIS 不足以表达 byref this）。
            var convention = isStatic ? 0x00 : 0x20; // Method(0) | HAS_THIS=0x20（值类型 this 隐式为托管指针）
            stream.WriteByte((byte)convention);
            WriteCompressedInteger(stream, parameterTypes.Count);
            EncodeType(stream, returnType);
            foreach (var parameterType in parameterTypes)
            {
                EncodeType(stream, parameterType);
            }

            return stream.ToArray();
        }

        public byte[] EncodeLocalVarSignature(IReadOnlyList<IlType> locals)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x07); // LocalVariables
            WriteCompressedInteger(stream, locals.Count);
            foreach (var local in locals)
            {
                EncodeType(stream, local);
            }

            return stream.ToArray();
        }

        /// <summary>字段签名：0x06 FIELD + 类型。</summary>
        public byte[] EncodeFieldSignature(IlType type)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x06); // FIELD
            EncodeType(stream, type);
            return stream.ToArray();
        }

        /// <summary>属性签名：0x08 PROPERTY + 0x20 HAS_THIS + 类型。</summary>
        public byte[] EncodePropertySignature(IlType type)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x08 | 0x20); // PROPERTY | HAS_THIS
            EncodeType(stream, type);
            return stream.ToArray();
        }

        /// <summary>DebuggableAttribute(bool, bool) 固定参数：prolog + 2 个 ELEMENT_TYPE_BOOLEAN(true)。</summary>
        public static byte[] EncodeDebuggableAttributeBlob()
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x01);
            stream.WriteByte(0x00);
            stream.WriteByte(0x02); // ELEMENT_TYPE_BOOLEAN
            stream.WriteByte(0x01);
            stream.WriteByte(0x02); // ELEMENT_TYPE_BOOLEAN
            stream.WriteByte(0x01);
            return stream.ToArray();
        }

        /// <summary>6e-M32 Tier-2：属性固定实参 blob（ECMA II.23.3）——prolog 0x0001 + 定长实参。
        /// 支持字面量集 {string/i32/bool/char/f64}；枚举暂按 int32。named args 暂不支持。</summary>
        public static byte[] EncodeAttributeBlob(IReadOnlyList<object?> values, IReadOnlyList<TypeSymbol> types)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x01); // prolog 低位
            stream.WriteByte(0x00); // prolog 高位
            for (var i = 0; i < values.Count; i++)
            {
                EncodeAttributeArgument(stream, values[i], types[i]);
            }

            return stream.ToArray();
        }

        private static void EncodeAttributeArgument(Stream stream, object? value, TypeSymbol type)
        {
            if (type == TypeSymbol.String)
            {
                stream.WriteByte(0x0E); // ELEMENT_TYPE_STRING
                var text = (string)value!;
                var utf16 = text.SelectMany(c => new[] { (byte)c, (byte)(c >> 8) }).ToArray();
                WriteCompressedInteger(stream, utf16.Length);
                stream.Write(utf16, 0, utf16.Length);
                stream.WriteByte(0x00); // null terminator byte
            }
            else if (type == TypeSymbol.Int32)
            {
                stream.WriteByte(0x08); // ELEMENT_TYPE_I4
                stream.WriteByte((byte)(int)value!);
                stream.WriteByte((byte)((int)value! >> 8));
                stream.WriteByte((byte)((int)value! >> 16));
                stream.WriteByte((byte)((int)value! >> 24));
            }
            else if (type == TypeSymbol.Boolean)
            {
                stream.WriteByte(0x02); // ELEMENT_TYPE_BOOLEAN
                stream.WriteByte((bool)value! ? (byte)1 : (byte)0);
            }
            else if (type == TypeSymbol.Char)
            {
                stream.WriteByte(0x03); // ELEMENT_TYPE_CHAR
                var c = (char)value!;
                stream.WriteByte((byte)c);
                stream.WriteByte((byte)(c >> 8));
            }
            else if (type == TypeSymbol.Double)
            {
                stream.WriteByte(0x0D); // ELEMENT_TYPE_R8
                var bytes = BitConverter.GetBytes((double)value!);
                stream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported attribute argument type {type}");
            }
        }

        /// <summary>可见性 → ECMA-335 可见性掩码（MethodDef/FieldDef 共用：Public=0x6/Assembly=0x3/Family=0x4/Private=0x1）。</summary>
        private static ushort VisibilityToFlags(IlVisibility visibility)
        {
            return visibility switch
            {
                IlVisibility.Public => 0x0006,
                IlVisibility.Internal => 0x0003,
                IlVisibility.Protected => 0x0004,
                _ => 0x0001,
            };
        }

        private void EncodeType(Stream stream, IlType type)
        {
            switch (type.Kind)
            {
                case IlTypeKind.Void:
                    stream.WriteByte(0x01);
                    break;
                case IlTypeKind.Boolean:
                    stream.WriteByte(0x02);
                    break;
                case IlTypeKind.Int32:
                    stream.WriteByte(0x08);
                    break;
                case IlTypeKind.Int64:
                    stream.WriteByte(0x0A); // ELEMENT_TYPE_I8
                    break;
                case IlTypeKind.Char:
                    stream.WriteByte(0x03); // ELEMENT_TYPE_CHAR
                    break;
                case IlTypeKind.U1:
                    stream.WriteByte(0x05); // ELEMENT_TYPE_U1
                    break;
                case IlTypeKind.I1:
                    stream.WriteByte(0x04); // ELEMENT_TYPE_I1
                    break;
                case IlTypeKind.I2:
                    stream.WriteByte(0x06); // ELEMENT_TYPE_I2
                    break;
                case IlTypeKind.U2:
                    stream.WriteByte(0x07); // ELEMENT_TYPE_U2
                    break;
                case IlTypeKind.U4:
                    stream.WriteByte(0x09); // ELEMENT_TYPE_U4
                    break;
                case IlTypeKind.U8:
                    stream.WriteByte(0x0B); // ELEMENT_TYPE_U8
                    break;
                case IlTypeKind.R4:
                    stream.WriteByte(0x0C); // ELEMENT_TYPE_R4
                    break;
                case IlTypeKind.Double:
                    stream.WriteByte(0x0D);
                    break;
                case IlTypeKind.String:
                    stream.WriteByte(0x0E);
                    break;
                case IlTypeKind.Object:
                    stream.WriteByte(0x1C);
                    break;
                case IlTypeKind.Class:
                    stream.WriteByte(type.IsValueType ? (byte)0x11 : (byte)0x12); // VALUETYPE / CLASS
                    WriteCompressedInteger(stream, type.TypeDef != null
                        ? CodedIndexTypeDefOrRef(type.TypeDef, _typeDefs)
                        : CodedIndexTypeDefOrRef(type.Reference!, _typeRefIndex));
                    break;
                case IlTypeKind.SzArray:
                    stream.WriteByte(0x1D); // SZARRAY
                    EncodeType(stream, type.ElementType!);
                    break;
                case IlTypeKind.ByRef:
                    stream.WriteByte(0x10); // ELEMENT_TYPE_BYREF（6e-M23 R6）
                    EncodeType(stream, type.ElementType!);
                    break;
                case IlTypeKind.NativeInt:
                    stream.WriteByte(0x18); // ELEMENT_TYPE_I
                    break;
                case IlTypeKind.NativeUInt:
                    stream.WriteByte(0x19); // ELEMENT_TYPE_U
                    break;
                case IlTypeKind.GenericParameter:
                    stream.WriteByte(0x13); // ELEMENT_TYPE_VAR (!n)
                    WriteCompressedInteger(stream, type.GenericOrdinal);
                    break;
                case IlTypeKind.GenericInst:
                    {
                        // GENERICINST CLASS/VALUETYPE TypeRefOrDef ArgCount Arg*
                        stream.WriteByte(0x15);
                        stream.WriteByte(type.IsValueType ? (byte)0x11 : (byte)0x12);
                        WriteCompressedInteger(stream, CodedIndexTypeDefOrRef(type.Reference!, _typeRefIndex));
                        WriteCompressedInteger(stream, type.GenericArguments!.Count);
                        foreach (var argument in type.GenericArguments)
                        {
                            EncodeType(stream, argument);
                        }

                        break;
                    }
                default:
                    throw new InvalidOperationException($"Unhandled type kind {type.Kind}");
            }
        }

        // ------------------------------------------------------------------
        // Coded index
        // ------------------------------------------------------------------

        private int CodedIndexTypeDefOrRef(IlTypeRef typeRef) => CodedIndexTypeDefOrRef(typeRef, _typeRefIndex);
        private int CodedIndexMemberRef(IlMethodRef methodRef) => CodedIndexMemberRef(methodRef, _memberRefIndex);

        private static int CodedIndexTypeDefOrRef(IlTypeRef typeRef, Dictionary<IlTypeRef, int> typeRefIndex)
        {
            // tag 2 位：TypeDef=0, TypeRef=1
            var rowId = typeRefIndex[typeRef];
            return (rowId << 2) | 1;
        }

        private static int CodedIndexTypeDefOrRef(IlTypeDef typeDef, IReadOnlyList<IlTypeDef> typeDefs)
        {
            // tag 2 位：TypeDef=0, TypeRef=1；TypeDef 行号含 <Module>（行 1）
            var rowId = 2;
            for (var i = 0; i < typeDefs.Count; i++)
            {
                if (typeDefs[i] == typeDef)
                {
                    rowId += i;
                    break;
                }
            }

            return rowId << 2;
        }

        private static int CodedIndexMemberRef(IlMethodRef methodRef, Dictionary<IlMethodRef, int> memberRefIndex)
        {
            // MemberRefParent tag 3 位：TypeRef=1
            var rowId = memberRefIndex[methodRef];
            return (rowId << 3) | 1;
        }

        private static int CodedIndexTypeRef(IlTypeRef typeRef, Dictionary<IlTypeRef, int> typeRefIndex)
        {
            // MemberRefParent tag 3 位：TypeRef=1
            var rowId = typeRefIndex[typeRef];
            return (rowId << 3) | 1;
        }

        // ------------------------------------------------------------------
        // 序列化
        // ------------------------------------------------------------------

        /// <summary>序列化结果：各流字节（表流/字符串/US/GUID/Blob），由 ManagedPEWriter 组装元数据根。</summary>
        public sealed class MetadataBlobs
        {
            public MetadataBlobs(byte[] tables, byte[] strings, byte[] us, byte[] guid, byte[] blob)
            {
                Tables = tables;
                Strings = strings;
                Us = us;
                Guid = guid;
                Blob = blob;
            }

            public byte[] Tables { get; }
            public byte[] Strings { get; }
            public byte[] Us { get; }
            public byte[] Guid { get; }
            public byte[] Blob { get; }
        }

        public byte[] MvidBytes => _mvid.ToByteArray();
        public IReadOnlyDictionary<string, uint> UserStringTokens => _userStrings;

        /// <summary>
        /// 序列化表流 + 四堆。
        /// </summary>
        /// <paramref name="methodRvas"/>：每个方法体的 RVA（由 ManagedPEWriter 布局后提供）。
        public MetadataBlobs Serialize(IReadOnlyDictionary<IlMethodDef, uint> methodRvas)
        {
 
            var typeRefCount = _typeRefs.Count;
            var assemblyRefCount = _assemblyRefs.Count;
            var typeDefCount = _typeDefs.Count + 1; // + <Module>
            var methodDefs = MethodDefs;
            var methodDefCount = methodDefs.Count;
            var fieldDefCount = _typeDefs.Sum(t => t.Fields.Count);
            var propertyCount = _typeDefs.Sum(t => t.Properties.Count);
            var propertyMapCount = _typeDefs.Count(t => t.Properties.Count > 0);
            var methodSemanticsCount = _typeDefs.Sum(t => t.Properties.Sum(p => (p.Getter != null ? 1 : 0) + (p.Setter != null ? 1 : 0)));
            var paramCount = methodDefs.Sum(m => m.ParameterTypes.Count);
            var interfaceImplCount = _typeDefs.Sum(t => t.Interfaces.Count);
            var methodImplCount = _typeDefs.Sum(t => t.MethodImpls.Count);
            var memberRefCount = _memberRefs.Count + _fieldRefs.Count;
            var typeSpecCount = _typeSpecs.Count;
            var customAttributeCount = _customAttributes.Count;
            var standAloneSigCount = _standAloneSigs.Count;

            var moduleRefs = methodDefs.Where(m => m.DllName != null).Select(m => m.DllName!).Distinct().ToList();
            var moduleRefCount = moduleRefs.Count;
            var implMapCount = methodDefs.Count(m => m.DllName != null);

            // 列宽（行数/堆大小 > 0xFFFF → 4 字节）
            var stringIsBig = _stringHeap.Count > 0xFFFF;
            var guidIsBig = false;
            var blobIsBig = _blobHeap.Count > 0xFFFF;
            var typeRefIsBig = typeRefCount > 0xFFFF;
            var typeDefIsBig = typeDefCount > 0xFFFF;
            var methodDefIsBig = methodDefCount > 0xFFFF;
            var fieldDefIsBig = fieldDefCount > 0xFFFF;
            var propertyIsBig = propertyCount > 0xFFFF;
            var paramIsBig = paramCount > 0xFFFF;
            var memberRefIsBig = memberRefCount > 0xFFFF;
            var typeSpecIsBig = typeSpecCount > 0xFFFF;
            var standAloneSigIsBig = standAloneSigCount > 0xFFFF;
            var assemblyRefIsBig = assemblyRefCount > 0xFFFF;
            var moduleRefIsBig = moduleRefCount > 0xFFFF;

            // coded index 宽（tag 位后余量 < 16 → 4 字节）
            var resolutionScopeIsBig = typeRefCount + assemblyRefCount + 1 > (1 << 14);
            var typeDefOrRefIsBig = typeDefCount + typeRefCount > (1 << 14);
            var memberRefParentIsBig = typeDefCount + typeRefCount + methodDefCount + fieldDefCount + typeSpecCount > (1 << 13);
            var hasCustomAttributeIsBig = new[] { typeRefCount, typeDefCount, methodDefCount, paramCount, memberRefCount, standAloneSigCount, 1, assemblyRefCount }.Max() > (1 << 11);
            var customAttributeTypeIsBig = Math.Max(methodDefCount, memberRefCount) > (1 << 13);
            var memberForwardedIsBig = Math.Max(methodDefCount, fieldDefCount) > (1 << 15); // 1 位 tag（MemberForwarded: Field=0/MethodDef=1）
            var hasConstantIsBig = new[] { fieldDefCount, paramCount, propertyCount }.Max() > (1 << 14); // HasConstant: Field=0/Param=1/Property=2
            var hasSemanticsIsBig = Math.Max(propertyCount, 1) > (1 << 15); // HasSemantics: Event=0/Property=1
            var methodDefOrRefIsBig = Math.Max(methodDefCount, memberRefCount) > (1 << 15); // MethodDefOrRef: MethodDef=0/MemberRef=1

            var heapSizes = (stringIsBig ? 0x01 : 0) | (guidIsBig ? 0x02 : 0) | (blobIsBig ? 0x04 : 0);

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            // ---- #~ 表流头 ----
            writer.Write(0u); // reserved（4 字节，ECMA-335 II.24.2.2）
            writer.Write((byte)2); // major
            writer.Write((byte)0); // minor
            writer.Write((byte)heapSizes);
            writer.Write((byte)1); // reserved

            var valid = 0UL;
            void SetValid(int table) => valid |= 1UL << table;
            SetValid(0x00); // Module（始终 1 行）
            if (typeRefCount > 0) SetValid(0x01); // TypeRef
            if (typeDefCount > 0) SetValid(0x02); // TypeDef
            if (fieldDefCount > 0) SetValid(0x04); // Field
            if (methodDefCount > 0) SetValid(0x06); // MethodDef
            if (paramCount > 0) SetValid(0x08); // Param
            if (interfaceImplCount > 0) SetValid(0x09); // InterfaceImpl
            if (memberRefCount > 0) SetValid(0x0A); // MemberRef
            if (typeSpecCount > 0) SetValid(0x1B); // TypeSpec（6e-M22 C4-b）
            if (customAttributeCount > 0) SetValid(0x0C); // CustomAttribute
            if (standAloneSigCount > 0) SetValid(0x11); // StandAloneSig
            if (propertyMapCount > 0) SetValid(0x15); // PropertyMap
            if (propertyCount > 0) SetValid(0x17); // Property
            if (methodSemanticsCount > 0) SetValid(0x18); // MethodSemantics
            if (methodImplCount > 0) SetValid(0x19); // MethodImpl（显式接口实现）
            if (moduleRefCount > 0) SetValid(0x1A); // ModuleRef
            if (implMapCount > 0) SetValid(0x1C); // ImplMap
            SetValid(0x20); // Assembly（始终 1 行）
            if (assemblyRefCount > 0) SetValid(0x23); // AssemblyRef
            writer.Write(valid);

            // Sorted bitmask（ECMA-335 II.24.2.6）：标记「要求排序」的表。取值与 ilasm/csc 一致
            // （0x000016003325FA00）。CLR 4.8 对带 native 导入（mscoree）的镜像校验该位掩码。
            const ulong sorted = 0x000016003325FA00UL;
            writer.Write(sorted);

            void WriteRowCount(int count) { if (count > 0) writer.Write((uint)count); }
            WriteRowCount(1);               // Module
            WriteRowCount(typeRefCount);    // TypeRef
            WriteRowCount(typeDefCount);    // TypeDef
            WriteRowCount(fieldDefCount);   // Field
            WriteRowCount(methodDefCount);  // MethodDef
            WriteRowCount(paramCount);      // Param
            WriteRowCount(interfaceImplCount); // InterfaceImpl
            WriteRowCount(memberRefCount);  // MemberRef
            WriteRowCount(customAttributeCount);
            WriteRowCount(standAloneSigCount);
            WriteRowCount(propertyMapCount);    // PropertyMap
            WriteRowCount(propertyCount);       // Property
            WriteRowCount(methodSemanticsCount); // MethodSemantics
            WriteRowCount(methodImplCount);     // MethodImpl
            WriteRowCount(moduleRefCount);  // ModuleRef
            // TypeSpec（0x1B）行数按表号序位于 ModuleRef(0x1A) 之后、ImplMap(0x1C) 之前
            WriteRowCount(typeSpecCount);
            WriteRowCount(implMapCount);    // ImplMap
            WriteRowCount(1);               // Assembly
            WriteRowCount(assemblyRefCount);

            void WriteRef(int value, bool isBig) { if (isBig) writer.Write((uint)value); else writer.Write((ushort)value); }
            void WriteStringRef(string value, bool isBig) => WriteRef(GetOrAddString(value), isBig);
            void WriteCoded(int value, bool isBig) => WriteRef(value, isBig);
            void WriteTypeDefRow(uint flags, string name, string ns, int extends, int fieldList, int methodList)
            {
                writer.Write(flags);
                WriteRef(GetOrAddString(name), stringIsBig);
                WriteRef(GetOrAddString(ns), stringIsBig);
                WriteRef(extends, typeDefOrRefIsBig);
                WriteRef(fieldList, typeDefIsBig);
                WriteRef(methodList, methodDefIsBig);
            }

            // ---- Module（1 行）----
            writer.Write((ushort)0);
            WriteStringRef(_moduleName, stringIsBig);
            WriteRef(1, guidIsBig);
            WriteRef(0, guidIsBig);
            WriteRef(0, guidIsBig);

            // ---- TypeRef ----
            foreach (var typeRef in _typeRefs)
            {
                // ResolutionScope（2 位 tag：Module=0, ModuleRef=1, AssemblyRef=2, TypeRef=3）
                var scope = typeRef.Scope == null ? 0 : (CodedIndexAssemblyRef(typeRef.Scope) << 2) | 2;
                WriteCoded(scope, resolutionScopeIsBig);
                WriteStringRef(typeRef.Name, stringIsBig);
                WriteStringRef(typeRef.Namespace, stringIsBig);
            }

            // ---- TypeDef（<Module> + Program + classes）----
            WriteTypeDefRow(0x00000000, "<Module>", "", 0, 1, 1);

            // 每个类型的字段/方法起始行（Field 表、MethodDef 表按类型分组排列）
            var fieldList = 1;
            var methodList = 1;
            foreach (var typeDef in _typeDefs)
            {
                var flags = typeDef.IsPublic ? 0x00000001u : 0x00000000u; // Public
                if (typeDef.IsInterface)
                {
                    flags |= 0x00000020u; // Interface
                }
                if (typeDef.IsAbstract)
                {
                    flags |= 0x00000080u; // Abstract
                }
                if (typeDef.IsValueType)
                {
                    // 值类型无独立标志位：以 extends System.ValueType 标识；加 SequentialLayout（0x08）默认布局。
                    flags |= 0x00000008u; // SequentialLayout
                }
                if (typeDef.IsSealed)
                {
                    flags |= 0x00000100u; // Sealed
                }
                var extends = typeDef.BaseTypeDef != null
                    ? CodedIndexTypeDefOrRef(typeDef.BaseTypeDef, _typeDefs)
                    : typeDef.BaseTypeRef == null ? 0 : CodedIndexTypeDefOrRef(typeDef.BaseTypeRef);
                WriteTypeDefRow(flags, typeDef.Name, typeDef.Namespace, extends, fieldList, methodList);
                fieldList += typeDef.Fields.Count;
                methodList += typeDef.Methods.Count;
            }

            // ---- Field ----
            var fieldRow = 1;
            foreach (var typeDef in _typeDefs)
            {
                foreach (var field in typeDef.Fields)
                {
                    // flags: Public=0x0006 / Internal(Assembly)=0x0003 / Protected(Family)=0x0004 / Private=0x0001 + Static=0x0010
                    var fieldFlags = (ushort)(VisibilityToFlags(field.Visibility));
                    if (field.IsStatic)
                    {
                        fieldFlags |= 0x0010;
                    }
                    writer.Write(fieldFlags);
                    WriteStringRef(field.Name, stringIsBig);
                    WriteRef(GetOrAddBlob(EncodeFieldSignature(field.Type)), blobIsBig);
                    fieldRow++;
                }
            }

            // ---- MethodDef ----
            var paramRow = 1;
            foreach (var method in methodDefs)
            {
                writer.Write(methodRvas.TryGetValue(method, out var rva) ? rva : 0u);
                var implFlags = method.IsRuntimeImplementation
                    ? (ushort)0x0003  // MethodImplAttributes.Runtime：CLR 特判方法（委托 .ctor/Invoke），RVA=0
                    : (ushort)(method.DllName != null ? 0x0080 : 0); // ImplFlags: extern 方法 PreserveSig（对齐 csc）
                writer.Write(implFlags);
                var methodFlags = (ushort)(VisibilityToFlags(method.Visibility) | 0x0080 | 0x0010 | (method.DllName != null ? 0x2000 : 0)); // Flags: 可见性|HideBySig|Static|PInvokeImpl
                if (!method.IsStatic)
                {
                    methodFlags = (ushort)(methodFlags & ~0x0010); // 清掉 Static
                }
                if (method.IsNewSlot)
                {
                    methodFlags = (ushort)(methodFlags | 0x0100); // NewSlot（MethodAttributes.NewSlot）
                }
                if (method.IsVirtual)
                {
                    methodFlags = (ushort)(methodFlags | 0x0040); // Virtual
                }
                if (method.IsAbstract)
                {
                    methodFlags = (ushort)(methodFlags | 0x0040 | 0x0100 | 0x0400); // 抽象方法必须 Virtual + NewSlot + Abstract
                }
                if (method.IsSealed)
                {
                    methodFlags = (ushort)(methodFlags | 0x0020); // Final
                }
                if (method.Name == ".ctor" || method.Name == ".cctor")
                {
                    methodFlags = (ushort)(methodFlags | 0x0800 | 0x1000); // SpecialName | RTSpecialName
                }
                writer.Write(methodFlags);
                WriteStringRef(method.Name, stringIsBig); // Name（MethodDef 行缺 Name 曾导致后续表全部偏移 2 字节）
                var methodSigBlob = GetOrAddBlob(EncodeMethodSignature(method.ReturnType, method.ParameterTypes, method.IsStatic, method.IsExplicitThis));
                WriteRef(methodSigBlob, blobIsBig);
                WriteRef(paramRow, paramIsBig);
                paramRow += method.ParameterTypes.Count;
            }

            // ---- Param ----
            foreach (var method in methodDefs)
            {
                var sequence = 1;
                foreach (var _ in method.ParameterTypes)
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)sequence++);
                    WriteStringRef("", stringIsBig);
                }
            }

            // ---- InterfaceImpl（行：Class(TypeDef) + Interface(TypeDefOrRef)；表 0x09 要求按 (Class, Interface) 排序）----
            foreach (var typeDef in _typeDefs)
            {
                if (typeDef.Interfaces.Count == 0)
                {
                    continue;
                }

                var typeDefRowIndex = 2;
                for (var i = 0; i < _typeDefs.Count; i++)
                {
                    if (_typeDefs[i] == typeDef)
                    {
                        typeDefRowIndex += i;
                        break;
                    }
                }

                foreach (var impl in typeDef.Interfaces.OrderBy(InterfaceCodedValue))
                {
                    var interfaceCoded = impl.TypeDef != null
                        ? CodedIndexTypeDefOrRef(impl.TypeDef, _typeDefs)
                        : CodedIndexTypeDefOrRef(impl.TypeRef!, _typeRefIndex);
                    WriteRef(typeDefRowIndex, typeDefIsBig);
                    WriteRef(interfaceCoded, typeDefOrRefIsBig);
                }
            }

            int InterfaceCodedValue(IlInterfaceImpl impl)
            {
                return impl.TypeDef != null
                    ? CodedIndexTypeDefOrRef(impl.TypeDef, _typeDefs)
                    : CodedIndexTypeDefOrRef(impl.TypeRef!, _typeRefIndex);
            }

            // ---- MemberRef ----
            foreach (var memberRef in _memberRefs)
            {
                // Parent（3 位 tag，ECMA-335 II.24.2.6）：TypeSpec=4 / TypeRef=1
                var parentCoded = memberRef.DeclaringTypeSpec != null
                    ? ((_typeSpecs.IndexOf(memberRef.DeclaringTypeSpec) + 1) << 3) | 4
                    : CodedIndexTypeRef(memberRef.DeclaringType!, _typeRefIndex);
                WriteCoded(parentCoded, memberRefParentIsBig);
                WriteStringRef(memberRef.Name, stringIsBig);
                WriteRef(GetOrAddBlob(EncodeMethodSignature(memberRef.ReturnType, memberRef.ParameterTypes, memberRef.IsStatic)), blobIsBig);
            }

            // ---- MemberRef（字段，facade 值类型字段重定向）----
            foreach (var fieldRef in _fieldRefs)
            {
                var parentCoded = CodedIndexTypeRef(fieldRef.DeclaringType, _typeRefIndex);
                WriteCoded(parentCoded, memberRefParentIsBig);
                WriteStringRef(fieldRef.Name, stringIsBig);
                WriteRef(GetOrAddBlob(EncodeFieldSignature(fieldRef.FieldType)), blobIsBig);
            }

            // ---- CustomAttribute（行：Parent(HasCustomAttribute coded) + Type(CustomAttributeType coded) + Value#）----
            foreach (var attribute in _customAttributes)
            {
                // 6e-M32 Tier-2：目标由发射侧编码（ParentRow/ParentTag/CtorRow/CtorTag）。
                // 旧路径（字段全 0）保持 Assembly 行 1 + MemberRef ctor 的既有语义（Debuggable）。
                var parentCoded = attribute.ParentRow == 0 && attribute.ParentTag == 0
                    ? (1 << 5) | 0x0E
                    : (attribute.ParentRow << 5) | attribute.ParentTag;
                WriteCoded(parentCoded, hasCustomAttributeIsBig);
                var caType = attribute.CtorRow == 0 && attribute.CtorTag == 0
                    ? (_memberRefIndex[attribute.Constructor] << 3) | 3
                    : (attribute.CtorRow << 3) | attribute.CtorTag;
                WriteCoded(caType, customAttributeTypeIsBig); // CustomAttributeType: MemberRef=3 / MethodDef=2
                WriteRef(GetOrAddBlob(attribute.FixedArguments), blobIsBig); // Value
            }

            // ---- StandAloneSig ----
            foreach (var sig in _standAloneSigs)
            {
                WriteRef(GetOrAddBlob(sig.Signature), blobIsBig);
            }

            // ---- PropertyMap（行：Parent(TypeDef 行) + PropertyList(Property 行)）----
            var propertyRow = 1;
            var typeDefRowNumber = 1; // 1 = <Module>
            foreach (var typeDef in _typeDefs)
            {
                typeDefRowNumber++;
                if (typeDef.Properties.Count > 0)
                {
                    WriteRef(typeDefRowNumber, typeDefIsBig);
                    WriteRef(propertyRow, propertyIsBig);
                    propertyRow += typeDef.Properties.Count;
                }
            }

            // ---- Property（行：Flags(2) + Name + Type(PropertySignature)）----
            foreach (var typeDef in _typeDefs)
            {
                foreach (var property in typeDef.Properties)
                {
                    writer.Write((ushort)0x0000); // PropertyAttributes: None
                    WriteStringRef(property.Name, stringIsBig);
                    WriteRef(GetOrAddBlob(EncodePropertySignature(property.Type)), blobIsBig);
                }
            }

            // ---- MethodSemantics（行：Semantics(2) + Method(MethodDef 行) + Association(HasSemantics coded)）----
            if (methodSemanticsCount > 0)
            {
                var methodRowMap = new Dictionary<IlMethodDef, int>();
                for (var i = 0; i < methodDefs.Count; i++)
                {
                    methodRowMap[methodDefs[i]] = i + 1;
                }

                var semantics = new List<(ushort Semantics, int MethodRow, int PropertyRow)>();
                var propertyRowForSemantics = 1;
                foreach (var typeDef in _typeDefs)
                {
                    foreach (var property in typeDef.Properties)
                    {
                        if (property.Getter != null)
                        {
                            semantics.Add((0x0002, methodRowMap[property.Getter], propertyRowForSemantics)); // Getter=0x2
                        }
                        if (property.Setter != null)
                        {
                            semantics.Add((0x0001, methodRowMap[property.Setter], propertyRowForSemantics)); // Setter=0x1
                        }
                        propertyRowForSemantics++;
                    }
                }

                // ECMA-335 II.22.24：MethodSemantics 表按 Method 列排序
                semantics.Sort((a, b) => a.MethodRow.CompareTo(b.MethodRow));

                foreach (var entry in semantics)
                {
                    writer.Write(entry.Semantics);
                    WriteRef(entry.MethodRow, methodDefIsBig);
                    WriteCoded((entry.PropertyRow << 1) | 1, hasSemanticsIsBig); // HasSemantics: Property, tag=1
                }
            }

            // ---- MethodImpl（行：Class(TypeDef) + MethodBody(MethodDefOrRef) + MethodDeclaration(MethodDefOrRef)）----
            // ECMA-335 II.22.29：表按 (Class, MethodBody, MethodDeclaration) 排序；本实现按 TypeDef 序 + 方法声明序添加即满足。
            if (methodImplCount > 0)
            {
                var methodRowMap = new Dictionary<IlMethodDef, int>();
                for (var i = 0; i < methodDefs.Count; i++)
                {
                    methodRowMap[methodDefs[i]] = i + 1;
                }

                // MethodDefOrRef coded：MethodDef=tag 0 / MemberRef=tag 1 → (row << 1) | tag
                foreach (var typeDef in _typeDefs)
                {
                    if (typeDef.MethodImpls.Count == 0)
                    {
                        continue;
                    }

                    var typeDefRowIndex = TypeDefRowOf(typeDef);
                    foreach (var impl in typeDef.MethodImpls.OrderBy(m => methodRowMap[m.MethodBody]))
                    {
                        WriteRef(typeDefRowIndex, typeDefIsBig);
                        WriteCoded(methodRowMap[impl.MethodBody] << 1, methodDefOrRefIsBig);
                        if (impl.DeclarationDef != null)
                        {
                            WriteCoded(methodRowMap[impl.DeclarationDef] << 1, methodDefOrRefIsBig);
                        }
                        else
                        {
                            var memberRow = _memberRefIndex[impl.DeclarationRef!];
                            WriteCoded((memberRow << 1) | 1, methodDefOrRefIsBig);
                        }
                    }
                }
            }

            // ---- ModuleRef（行：Name #Strings）----
            foreach (var dll in moduleRefs)
            {
                WriteStringRef(dll, stringIsBig);
            }

            // ---- TypeSpec（行：TypeSignature #Blob；6e-M22 C4-b）----
            foreach (var typeSpec in _typeSpecs)
            {
                WriteRef(GetOrAddBlob(typeSpec.Signature), blobIsBig);
            }

            // ---- ImplMap（按 MemberForwarded MethodDef 行递增排序；行：MappingFlags + MemberForwarded + ImportName + ImportScope）----
            var methodRow = 1;
            foreach (var method in methodDefs)
            {
                if (method.DllName != null)
                {
                    var callConvMask = method.CallingConvention switch
                    {
                        IlCallingConvention.Cdecl => 0x0200,
                        IlCallingConvention.StdCall => 0x0300,
                        _ => 0x0100, // Winapi
                    };
                    // CharSet 位（ECMA-335 II.22.14 / II.15.3）：Ansi=0x0002 Unicode=0x0004 Auto=0x0006（6e-M17 Step 5 起可配置）
                    var charSetMask = method.CharSet switch
                    {
                        IlCharSet.Ansi => 0x0002,
                        IlCharSet.Auto => 0x0006,
                        _ => 0x0004, // Unicode
                    };
                    var mappingFlags = (ushort)(callConvMask | charSetMask);
                    writer.Write(mappingFlags);
                    WriteCoded((methodRow << 1) | 1, memberForwardedIsBig); // MemberForwarded: MethodDef, tag=1
                    WriteStringRef(method.ImportName ?? method.Name, stringIsBig);
                    WriteRef(moduleRefs.IndexOf(method.DllName) + 1, moduleRefIsBig);
                }

                methodRow++;
            }

            // ---- Assembly（1 行）----
            writer.Write((uint)0x0804); // HashAlgId = SHA1
            writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((uint)0); // Flags
            WriteRef(GetOrAddBlob(Array.Empty<byte>()), blobIsBig);
            WriteStringRef(_assemblyName, stringIsBig);
            WriteStringRef("", stringIsBig);

            // ---- Assembly（1 行）----
            foreach (var assemblyRef in _assemblyRefs)
            {
                writer.Write((ushort)assemblyRef.Version.Major);
                writer.Write((ushort)assemblyRef.Version.Minor);
                writer.Write((ushort)assemblyRef.Version.Build);
                writer.Write((ushort)assemblyRef.Version.Revision);
                writer.Write((uint)assemblyRef.Flags);
                WriteRef(GetOrAddBlob(assemblyRef.PublicKeyOrToken), blobIsBig);
                WriteStringRef(assemblyRef.Name, stringIsBig);
                WriteStringRef(assemblyRef.Culture, stringIsBig);
                WriteRef(GetOrAddBlob(Array.Empty<byte>()), blobIsBig); // HashValue
            }

            // 表流尾对齐
            writer.Write((byte)0);
            while (stream.Position % 4 != 0) writer.Write((byte)0);

            var guid = _mvid.ToByteArray();
            return new MetadataBlobs(stream.ToArray(), _stringHeap.ToArray(), _usHeap.ToArray(), guid, _blobHeap.ToArray());
        }

        private int CodedIndexAssemblyRef(IlAssemblyRef assemblyRef) => _assemblyRefIndex[assemblyRef];

        // ------------------------------------------------------------------
 
        // ------------------------------------------------------------------

        private static void WriteCompressedInteger(List<byte> bytes, int value)
        {
            if (value <= 0x7F)
            {
                bytes.Add((byte)value);
            }
            else if (value <= 0x3FFF)
            {
                bytes.Add((byte)(0x80 | (value >> 8)));
                bytes.Add((byte)value);
            }
            else
            {
                bytes.Add((byte)(0xC0 | (value >> 24)));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
            }
        }

        private static void WriteCompressedInteger(Stream stream, int value)
        {
            if (value <= 0x7F)
            {
                stream.WriteByte((byte)value);
            }
            else if (value <= 0x3FFF)
            {
                stream.WriteByte((byte)(0x80 | (value >> 8)));
                stream.WriteByte((byte)value);
            }
            else
            {
                stream.WriteByte((byte)(0xC0 | (value >> 24)));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }
        }

        private static byte GetUserStringTrailingByte(string value)
        {
            foreach (var c in value)
            {
                if (c >= 0x7F)
                {
                    return 1;
                }

                var b = (byte)c;
                if (b >= 0x01 && b <= 0x08) return 1;
                if (b >= 0x0E && b <= 0x1F) return 1;
                if (b == 0x27 || b == 0x2D || b == 0x7F) return 1;
            }

            return 0;
        }
    }

}
