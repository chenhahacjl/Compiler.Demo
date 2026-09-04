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
    /// <summary>
    /// ECMA-335 元数据写入器（最小子集）：Module/TypeRef/TypeDef/MethodDef/Param/MemberRef/
    /// CustomAttribute/Assembly/AssemblyRef/StandAloneSig 表 + #Strings/#US/#GUID/#Blob 堆。布局细节对照 Roslyn MetadataWriter / System.Reflection.Metadata.Ecma335.MetadataBuilder。
    /// </summary>
    public sealed partial class MetadataBuilder
    {
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

    }
}
