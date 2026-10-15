using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>自研元数据引用：签名与 token 分配的最小描述。</summary>
    public sealed class IlType
    {
        public IlType(IlTypeKind kind, IlTypeRef? reference = null, IlType? elementType = null, IlTypeDef? typeDef = null, bool isValueType = false, IReadOnlyList<IlType>? genericArguments = null, int genericOrdinal = -1)
        {
            Kind = kind;
            Reference = reference;
            ElementType = elementType;
            TypeDef = typeDef;
            IsValueType = isValueType;
            GenericArguments = genericArguments;
            GenericOrdinal = genericOrdinal;
        }

        public IlTypeKind Kind { get; }
        public IlTypeRef? Reference { get; }
        public IlType? ElementType { get; }
        public IlTypeDef? TypeDef { get; }
        public IReadOnlyList<IlType>? GenericArguments { get; }
        public int GenericOrdinal { get; }
        public bool IsValueType { get; }

        public static readonly IlType Void = new IlType(IlTypeKind.Void);
        public static readonly IlType Boolean = new IlType(IlTypeKind.Boolean);
        public static readonly IlType Int32 = new IlType(IlTypeKind.Int32);
        public static readonly IlType Int64 = new IlType(IlTypeKind.Int64);
        public static readonly IlType Char = new IlType(IlTypeKind.Char);
        public static readonly IlType Byte = new IlType(IlTypeKind.U1);
        public static readonly IlType Double = new IlType(IlTypeKind.Double);
        public static readonly IlType String = new IlType(IlTypeKind.String);
        public static readonly IlType Object = new IlType(IlTypeKind.Object);
        public static readonly IlType SByte = new IlType(IlTypeKind.I1);
        public static readonly IlType Int16 = new IlType(IlTypeKind.I2);
        public static readonly IlType UInt16 = new IlType(IlTypeKind.U2);
        public static readonly IlType UInt32 = new IlType(IlTypeKind.U4);
        public static readonly IlType UInt64 = new IlType(IlTypeKind.U8);
        public static readonly IlType Float = new IlType(IlTypeKind.R4);

        public static IlType Class(IlTypeRef reference, bool isValueType = false) => new IlType(IlTypeKind.Class, reference, isValueType: isValueType);
        public static IlType Class(IlTypeDef typeDef, bool isValueType = false) => new IlType(IlTypeKind.Class, typeDef: typeDef, isValueType: isValueType);
        public static IlType SzArrayOf(IlType elementType) => new IlType(IlTypeKind.SzArray, elementType: elementType);
        public static IlType GenericInstance(IlTypeRef definition, IReadOnlyList<IlType> arguments) => new IlType(IlTypeKind.GenericInst, definition, genericArguments: arguments);
        public static readonly IlType NativeInt = new IlType(IlTypeKind.NativeInt);
        public static readonly IlType NativeUInt = new IlType(IlTypeKind.NativeUInt);
        public static IlType GenericVar(int ordinal) => new IlType(IlTypeKind.GenericParameter, genericOrdinal: ordinal);
        public static IlType ByRefOf(IlType elementType) => new IlType(IlTypeKind.ByRef, elementType: elementType);

        public string FullName => Kind switch
        {
            IlTypeKind.Void => "System.Void",
            IlTypeKind.Boolean => "System.Boolean",
            IlTypeKind.Int32 => "System.Int32",
            IlTypeKind.Int64 => "System.Int64",
            IlTypeKind.Char => "System.Char",
            IlTypeKind.U1 => "System.Byte",
            IlTypeKind.Double => "System.Double",
            IlTypeKind.String => "System.String",
            IlTypeKind.Object => "System.Object",
            IlTypeKind.I1 => "System.SByte",
            IlTypeKind.I2 => "System.Int16",
            IlTypeKind.U2 => "System.UInt16",
            IlTypeKind.U4 => "System.UInt32",
            IlTypeKind.U8 => "System.UInt64",
            IlTypeKind.R4 => "System.Single",
            IlTypeKind.Class => TypeDef != null
                ? (TypeDef.Namespace.Length == 0 ? TypeDef.Name : TypeDef.Namespace + "." + TypeDef.Name)
                : Reference!.FullName,
            IlTypeKind.SzArray => ElementType!.FullName + "[]",
            IlTypeKind.ByRef => ElementType!.FullName + "&",
            IlTypeKind.NativeInt => "System.IntPtr",
            IlTypeKind.NativeUInt => "System.UIntPtr",
            _ => "?",
        };
    }
}
