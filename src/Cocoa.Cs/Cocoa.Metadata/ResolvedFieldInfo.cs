using Cocoa.Metadata; using System; using System.Collections.Generic; using System.IO; using System.Text;  
 
 namespace Cocoa.Metadata 
 { 
     public sealed class ResolvedFieldInfo
    {
        public ResolvedFieldInfo(IlTypeRef declaringType, string name, IlType type, bool isPublic)
        {
            DeclaringType = declaringType;
            Name = name;
            Type = type;
            IsPublic = isPublic;
        }

        public IlTypeRef DeclaringType { get; }
        public string Name { get; }
        public IlType Type { get; }
        public bool IsPublic { get; }
    } 
 } 
