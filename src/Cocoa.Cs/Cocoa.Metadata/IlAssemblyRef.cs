using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>AssemblyRef：对引用程序集的描述。</summary>
    public sealed class IlAssemblyRef : IlReference
    {
        public IlAssemblyRef(string name, System.Version version, byte[] publicKeyOrToken, string? culture, uint flags)
        {
            Name = name;
            Version = version;
            PublicKeyOrToken = publicKeyOrToken;
            Culture = culture ?? "";
            Flags = flags;
        }

        public string Name { get; }
        public System.Version Version { get; }
        public byte[] PublicKeyOrToken { get; }
        public string Culture { get; }
        public uint Flags { get; }

        public override bool Equals(object? obj) =>
            obj is IlAssemblyRef other &&
            other.Name == Name && other.Version == Version && other.Flags == Flags &&
            System.Linq.Enumerable.SequenceEqual(other.PublicKeyOrToken, PublicKeyOrToken) &&
            other.Culture == Culture;

        public override int GetHashCode() => System.HashCode.Combine(Name, Version);
    }
}
