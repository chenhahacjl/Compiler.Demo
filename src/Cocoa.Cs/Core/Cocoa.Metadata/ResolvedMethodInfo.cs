using Cocoa.Metadata; using System; using System.Collections.Generic; using System.IO; using System.Text;  
 
 namespace Cocoa.Metadata 
 { 
     public sealed class ResolvedMethodInfo
    {
        public ResolvedMethodInfo(IlTypeRef declaringType, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic)
        {
            DeclaringType = declaringType;
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            IsStatic = isStatic;
        }

        public IlTypeRef DeclaringType { get; }
        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public bool IsStatic { get; }
    } 
 } 
