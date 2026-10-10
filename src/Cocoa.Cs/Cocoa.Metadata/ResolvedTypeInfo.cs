using Cocoa.Metadata; using System; using System.Collections.Generic; using System.IO; using System.Text;  
 
 namespace Cocoa.Metadata 
 { 
     public sealed class ResolvedTypeInfo
    {
        public ResolvedTypeInfo(string fullName, bool isInterface, List<ResolvedFieldInfo> fields, List<ResolvedMethodInfo> methods)
        {
            FullName = fullName;
            IsInterface = isInterface;
            Fields = fields;
            Methods = methods;
        }

        public string FullName { get; }
        public bool IsInterface { get; }
        public List<ResolvedFieldInfo> Fields { get; }
        public List<ResolvedMethodInfo> Methods { get; }
    } 
 } 
