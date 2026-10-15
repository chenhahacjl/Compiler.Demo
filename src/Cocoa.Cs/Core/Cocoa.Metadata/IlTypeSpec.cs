using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>TypeSpec：类型规格签名（GENERICINST blob）。</summary>
    public sealed class IlTypeSpec : IlReference
    {
        public IlTypeSpec(byte[] signature) => Signature = signature;

        public byte[] Signature { get; }

        public override bool Equals(object? obj) =>
            obj is IlTypeSpec other && System.Linq.Enumerable.SequenceEqual(other.Signature, Signature);

        public override int GetHashCode()
        {
            var hash = 19;
            foreach (var b in Signature)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }
}
