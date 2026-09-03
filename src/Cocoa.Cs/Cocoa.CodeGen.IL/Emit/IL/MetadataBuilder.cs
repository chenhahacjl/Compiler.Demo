using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeGen.IL
{
    internal sealed partial class MetadataBuilder : IIlRefIssuer
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
        private readonly Dictionary<IlMethodRef, int> _memberRefIndex = new Dictionary<IlMethodRef, int>();
        private readonly Dictionary<IlFieldRef, int> _fieldRefIndex = new Dictionary<IlFieldRef, int>();
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
            using var stream = new MemoryStream();
            // 注意：Extern 方法的签名必须使用默认调用约定（0x00），本机调用约定由 ImplMap 的 MappingFlags 表达。
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

    }
}
