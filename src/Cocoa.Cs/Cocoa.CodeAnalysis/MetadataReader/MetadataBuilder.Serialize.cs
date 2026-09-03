using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// ECMA-335 元数据写入器（最小子集）：Module/TypeRef/TypeDef/MethodDef/Param/MemberRef/
    /// CustomAttribute/Assembly/AssemblyRef/StandAloneSig 表 + #Strings/#US/#GUID/#Blob 堆。布局细节对照 Roslyn MetadataWriter / System.Reflection.Metadata.Ecma335.MetadataBuilder。
    /// </summary>
    internal sealed partial class MetadataBuilder
    {
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
            var propertyCount = _typeDefs.Sum(t => t.Properties.Count);
            var propertyMapCount = _typeDefs.Count(t => t.Properties.Count > 0);
            var methodSemanticsCount = _typeDefs.Sum(t => t.Properties.Sum(p => (p.Getter != null ? 1 : 0) + (p.Setter != null ? 1 : 0)));
            var paramCount = methodDefs.Sum(m => m.ParameterTypes.Count);
            var interfaceImplCount = _typeDefs.Sum(t => t.Interfaces.Count);
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
                var implFlags = (ushort)(method.DllName != null ? 0x0080 : 0); // ImplFlags: extern 方法 PreserveSig（对齐 csc）
                writer.Write(implFlags);
                var methodFlags = (ushort)(VisibilityToFlags(method.Visibility) | 0x0080 | 0x0010 | (method.DllName != null ? 0x2000 : 0)); // Flags: 可见性|HideBySig|Static|PInvokeImpl
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
                        CharSet.Ansi => 0x0002,
                        CharSet.Auto => 0x0006,
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

