using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>TypeRef：对另一个程序集/模块中类型的引用。</summary>
    public sealed class IlTypeRef : IlReference
    {
        public IlTypeRef(string? namespaceName, string name, IlAssemblyRef? scope)
        {
            Namespace = namespaceName ?? "";
            Name = name;
            Scope = scope;
        }

        public string Namespace { get; }
        public string Name { get; }
        public IlAssemblyRef? Scope { get; }
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public override bool Equals(object? obj) =>
            obj is IlTypeRef other && other.Namespace == Namespace && other.Name == Name && Equals(other.Scope, Scope);

        public override int GetHashCode() => System.HashCode.Combine(Namespace, Name, Scope);
    }
}
