using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>StandAloneSig 引用（局部变量签名等）。</summary>
    public interface IIlRefIssuer
    {
        IlAssemblyRef DefineAssemblyRef(string name, Version version, byte[] publicKeyOrToken, string? culture, uint flags);
        IlTypeRef DefineTypeRef(IlAssemblyRef? scope, string? namespaceName, string name);
    }
}
