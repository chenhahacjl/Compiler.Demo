using Cocoa.Metadata;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.Metadata
{
    public sealed class AssemblyScope
    {
        public AssemblyScope(string assemblyName, Version version, byte[] publicKeyOrToken, string culture, uint flags)
        {
            AssemblyName = assemblyName;
            Version = version;
            PublicKeyOrToken = publicKeyOrToken;
            Culture = culture;
            Flags = flags;
        }

        public string AssemblyName { get; }
        public Version Version { get; }
        public byte[] PublicKeyOrToken { get; }
        public string Culture { get; }
        public uint Flags { get; }
    }
}
