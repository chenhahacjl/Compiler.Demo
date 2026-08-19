using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>我们自己的类型定义（TypeDef 表行）。顶层函数挂在 Program，class 各占一行。</summary>
    internal sealed class IlTypeDef
    {
        public IlTypeDef(string name, string @namespace, IlTypeRef? baseTypeRef, bool isPublic = true, IlTypeDef? baseTypeDef = null)
        {
            Name = name;
            Namespace = @namespace ?? "";
            BaseTypeRef = baseTypeRef;
            BaseTypeDef = baseTypeDef;
            IsPublic = isPublic;
            Fields = new List<IlFieldDef>();
            Methods = new List<IlMethodDef>();
        }

        public string Name { get; }
        public string Namespace { get; }
        public IlTypeRef? BaseTypeRef { get; }

        /// <summary>本程序集内的基类 TypeDef（优先于 BaseTypeRef）。</summary>
        public IlTypeDef? BaseTypeDef { get; }

        public bool IsPublic { get; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public List<IlFieldDef> Fields { get; }
        public List<IlMethodDef> Methods { get; }
    }

    /// <summary>我们自己的字段定义（FieldDef 表行）。</summary>
    internal sealed class IlFieldDef
    {
        public IlFieldDef(string name, IlType type, bool isPublic, bool isStatic = false)
        {
            Name = name;
            Type = type;
            IsPublic = isPublic;
            IsStatic = isStatic;
        }

        public string Name { get; }
        public IlType Type { get; }
        public bool IsPublic { get; }
        public bool IsStatic { get; }
    }

    /// <summary>我们自己的方法定义（MethodDef 表行 + 方法体）。</summary>
    internal sealed class IlMethodDef
    {
        public IlMethodDef(string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, IlMethodBody? body, string? dllName = null, string? importName = null, IlCallingConvention callingConvention = IlCallingConvention.Winapi, bool isStatic = true)
        {
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            Body = body;
            DllName = dllName;
            ImportName = importName;
            CallingConvention = callingConvention;
            IsStatic = isStatic;
        }

        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public IlMethodBody? Body { get; }

        /// <summary>P/Invoke 目标 DLL（null = 普通方法，不产生 ImplMap 行）。</summary>
        public string? DllName { get; }
        /// <summary>入口点名称（null = 与方法同名）。</summary>
        public string? ImportName { get; }
        public IlCallingConvention CallingConvention { get; }
        /// <summary>实例方法（含 this，签名 HAS_THIS）。</summary>
        public bool IsStatic { get; }

        public bool IsVirtual { get; set; }

        public bool IsAbstract { get; set; }

        public bool IsSealed { get; set; }
    }

    /// <summary>
    /// ECMA-335 元数据写入器（最小子集）：Module/TypeRef/TypeDef/MethodDef/Param/MemberRef/
    /// CustomAttribute/Assembly/AssemblyRef/StandAloneSig 表 + #Strings/#US/#GUID/#Blob 堆。布局细节对照 Roslyn MetadataWriter / System.Reflection.Metadata.Ecma335.MetadataBuilder。
    /// </summary>
    internal sealed class MetadataBuilder
    {
        private const string RuntimeVersion = "v4.0.30319";

        private readonly string _moduleName;
        private readonly string _assemblyName;
        private readonly Guid _mvid = Guid.NewGuid();

        private readonly List<IlTypeRef> _typeRefs = new List<IlTypeRef>();
        private readonly List<IlAssemblyRef> _assemblyRefs = new List<IlAssemblyRef>();
        private readonly List<IlTypeDef> _typeDefs = new List<IlTypeDef>();
        private readonly List<IlMethodRef> _memberRefs = new List<IlMethodRef>();
        private readonly List<IlCustomAttribute> _customAttributes = new List<IlCustomAttribute>();
        private readonly List<IlStandAloneSig> _standAloneSigs = new List<IlStandAloneSig>();

        /// <summary>全部方法（按类型分组：Program 在前，各 class 依序）。</summary>
        public IReadOnlyList<IlMethodDef> MethodDefs => _typeDefs.SelectMany(t => t.Methods).ToList();

        private readonly Dictionary<IlTypeRef, int> _typeRefIndex = new Dictionary<IlTypeRef, int>();
        private readonly Dictionary<IlAssemblyRef, int> _assemblyRefIndex = new Dictionary<IlAssemblyRef, int>();
        private readonly Dictionary<IlMethodRef, int> _memberRefIndex = new Dictionary<IlMethodRef, int>();
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
        private const uint StandAloneSigTable = 0x11;
        private const uint AssemblyRefTable = 0x23;
        private const uint UserStringTable = 0x70;

        public MetadataBuilder(string moduleName, string assemblyName)
        {
            _moduleName = moduleName;
            _assemblyName = assemblyName;

            // 索引 0 预置为空条目：GetOrAddString("")/GetOrAddBlob(empty) 必须返回 0，
            // 否则 AssemblyRef 的 Culture/HashValue 会指向非空堆条目，CLR 4.8 拒绝加载。
            _stringHeap.Add(0);
            _strings.Add("", 0);
            _usHeap.Add(0);
            _blobHeap.Add(0);
            _blobs.Add(new BlobKey(Array.Empty<byte>()), 0);
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

        /// <summary>构建 token 映射（IlAssembler 回填用）：引用对象 → 元数据 token。</summary>
        public Dictionary<object, uint> BuildTokenMap()
        {
            var map = new Dictionary<object, uint>();
            for (var i = 0; i < _typeRefs.Count; i++)
            {
                map[_typeRefs[i]] = TypeRefTable << 24 | (uint)(i + 1);
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

        public byte[] EncodeMethodSignature(IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic = true)
        {
            using var stream = new MemoryStream();
            // 注意：Extern 方法的签名必须使用默认调用约定（0x00），本机调用约定由 ImplMap 的 MappingFlags 表达。
            stream.WriteByte((byte)(isStatic ? 0x00 : 0x20)); // Method(0) | HAS_THIS=0x20 | 默认调用约定
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
                case IlTypeKind.Char:
                    stream.WriteByte(0x03); // ELEMENT_TYPE_CHAR
                    break;
                case IlTypeKind.U1:
                    stream.WriteByte(0x05); // ELEMENT_TYPE_U1
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
                    stream.WriteByte(0x12); // CLASS
                    WriteCompressedInteger(stream, type.TypeDef != null
                        ? CodedIndexTypeDefOrRef(type.TypeDef, _typeDefs)
                        : CodedIndexTypeDefOrRef(type.Reference!, _typeRefIndex));
                    break;
                case IlTypeKind.SzArray:
                    stream.WriteByte(0x1D); // SZARRAY
                    EncodeType(stream, type.ElementType!);
                    break;
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
        internal sealed class MetadataBlobs
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
            var paramCount = methodDefs.Sum(m => m.ParameterTypes.Count);
            var memberRefCount = _memberRefs.Count;
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
            var paramIsBig = paramCount > 0xFFFF;
            var memberRefIsBig = memberRefCount > 0xFFFF;
            var standAloneSigIsBig = standAloneSigCount > 0xFFFF;
            var assemblyRefIsBig = assemblyRefCount > 0xFFFF;
            var moduleRefIsBig = moduleRefCount > 0xFFFF;

            // coded index 宽（tag 位后余量 < 16 → 4 字节）
            var resolutionScopeIsBig = typeRefCount + assemblyRefCount + 1 > (1 << 14);
            var typeDefOrRefIsBig = typeDefCount + typeRefCount > (1 << 14);
            var memberRefParentIsBig = typeDefCount + typeRefCount + methodDefCount + fieldDefCount > (1 << 13);
            var hasCustomAttributeIsBig = new[] { typeRefCount, typeDefCount, methodDefCount, paramCount, memberRefCount, standAloneSigCount, 1, assemblyRefCount }.Max() > (1 << 11);
            var customAttributeTypeIsBig = Math.Max(methodDefCount, memberRefCount) > (1 << 13);
            var memberForwardedIsBig = Math.Max(methodDefCount, fieldDefCount) > (1 << 15); // 1 位 tag（MemberForwarded: Field=0/MethodDef=1）

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
            if (memberRefCount > 0) SetValid(0x0A); // MemberRef
            if (customAttributeCount > 0) SetValid(0x0C); // CustomAttribute
            if (standAloneSigCount > 0) SetValid(0x11); // StandAloneSig
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
            WriteRowCount(memberRefCount);  // MemberRef
            WriteRowCount(customAttributeCount);
            WriteRowCount(standAloneSigCount);
            WriteRowCount(moduleRefCount);  // ModuleRef
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
                if (typeDef.IsAbstract)
                {
                    flags |= 0x00000080u; // Abstract
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
                    // flags: Public=0x0006 / Private=0x0001 + Static=0x0010
                    var fieldFlags = field.IsPublic ? (ushort)0x0006 : (ushort)0x0001;
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
                var implFlags = (ushort)(method.DllName != null ? 0x0080 : 0); // ImplFlags: extern 方法 PreserveSig（对齐 csc）
                writer.Write(implFlags);
                var methodFlags = (ushort)(0x0096 | (method.DllName != null ? 0x2000 : 0)); // Flags: Public|Static|HideBySig|ReuseSlot|PInvokeImpl
                if (!method.IsStatic)
                {
                    methodFlags = (ushort)(methodFlags & ~0x0010); // 清掉 Static
                }
                if (method.IsVirtual)
                {
                    methodFlags = (ushort)(methodFlags | 0x0040); // Virtual
                }
                if (method.IsAbstract)
                {
                    methodFlags = (ushort)(methodFlags | 0x0040 | 0x0400); // 抽象方法必须 Virtual + Abstract
                }
                if (method.IsSealed)
                {
                    methodFlags = (ushort)(methodFlags | 0x0020); // Final
                }
                if (method.Name == ".ctor")
                {
                    methodFlags = (ushort)(methodFlags | 0x0800 | 0x1000); // SpecialName | RTSpecialName
                }
                writer.Write(methodFlags);
                WriteStringRef(method.Name, stringIsBig); // Name（MethodDef 行缺 Name 曾导致后续表全部偏移 2 字节）
                var methodSigBlob = GetOrAddBlob(EncodeMethodSignature(method.ReturnType, method.ParameterTypes, method.IsStatic));
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

            // ---- MemberRef ----
            foreach (var memberRef in _memberRefs)
            {
                WriteCoded(CodedIndexTypeRef(memberRef.DeclaringType, _typeRefIndex), memberRefParentIsBig);
                WriteStringRef(memberRef.Name, stringIsBig);
                WriteRef(GetOrAddBlob(EncodeMethodSignature(memberRef.ReturnType, memberRef.ParameterTypes, memberRef.IsStatic)), blobIsBig);
            }

            // ---- CustomAttribute（行：Parent(5-bit HasCustomAttribute) + Type(3-bit CustomAttributeType) + Value#）----
            foreach (var attribute in _customAttributes)
            {
                WriteCoded((1 << 5) | 0x0E, hasCustomAttributeIsBig); // HasCustomAttribute: Assembly 行 1, tag=0x0E
                var caType = (_memberRefIndex[attribute.Constructor] << 3) | 3;
                WriteCoded(caType, customAttributeTypeIsBig); // CustomAttributeType: MemberRef=3
                WriteRef(GetOrAddBlob(attribute.FixedArguments), blobIsBig); // Value
            }

            // ---- StandAloneSig ----
            foreach (var sig in _standAloneSigs)
            {
                WriteRef(GetOrAddBlob(sig.Signature), blobIsBig);
            }

            // ---- ModuleRef（行：Name #Strings）----
            foreach (var dll in moduleRefs)
            {
                WriteStringRef(dll, stringIsBig);
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
                    var mappingFlags = (ushort)(callConvMask | 0x0002 /* CharSetAnsi */);
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

