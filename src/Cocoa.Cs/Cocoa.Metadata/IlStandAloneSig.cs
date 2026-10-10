using System.Collections.Generic;

namespace Cocoa.Metadata
{
    public sealed class IlStandAloneSig : IlReference
    {
        public IlStandAloneSig(byte[] signature) => Signature = signature;

        public byte[] Signature { get; }

        public override bool Equals(object? obj) =>
            obj is IlStandAloneSig other && System.Linq.Enumerable.SequenceEqual(other.Signature, Signature);

        public override int GetHashCode()
        {
            var hash = 17;
            foreach (var b in Signature)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }
}
