using System;
using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>InterfaceImpl 表行：类 → 接口（TypeDefOrRef：本程序集 TypeDef 或外部 TypeRef）。</summary>
    public sealed class IlInterfaceImpl
    {
        public IlInterfaceImpl(IlTypeDef? typeDef, IlTypeRef? typeRef)
        {
            TypeDef = typeDef;
            TypeRef = typeRef;
        }

        public IlTypeDef? TypeDef { get; }
        public IlTypeRef? TypeRef { get; }
    }
}
