using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>签名中的类型（ECMA-335 III.1.1 元素类型编码所需的最小集）。</summary>
    public enum IlTypeKind
    {
        Void,
        Boolean,
        Int32,
        Int64,
        Char,
        U1,
        Double,
        String,
        Object,
        Class,
        SzArray,
        GenericInst,
        NativeInt,
        NativeUInt,
        GenericParameter,
        I1,
        I2,
        U2,
        U4,
        U8,
        R4,
        ByRef,
    }
}
