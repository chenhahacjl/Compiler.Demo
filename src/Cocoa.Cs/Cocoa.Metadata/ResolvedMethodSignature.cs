using Cocoa.Metadata;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.Metadata
{
    public sealed class ResolvedMethodSignature
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
