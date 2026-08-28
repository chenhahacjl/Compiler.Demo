using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>单个引用程序集的元数据读取（TypeDef/TypeRef/MethodDef/AssemblyRef + 方法签名）。</summary>
    internal sealed partial class AssemblyReader
    {
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
                case 0x04: return IlType.SByte;
                case 0x05: return IlType.Byte;
                case 0x06: return IlType.Int16;
                case 0x07: return IlType.UInt16;
                case 0x08: return IlType.Int32;
                case 0x09: return IlType.UInt32;
                case 0x0A: return IlType.Int64;
                case 0x0B: return IlType.UInt64;
                case 0x0C: return IlType.Float;
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
                        return IlType.Class(new IlTypeRef(NamespaceOf(fullName), NameOf(fullName), null), isValueType: element == 0x11);
                    }
                case 0x10: // BYREF
                    return IlType.ByRefOf(ParseType(blob, ref pos));
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
