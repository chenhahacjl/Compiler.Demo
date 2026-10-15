using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>P/Invoke 调用约定（对应 ECMA-335 II.23.1.10 ImplMapFlags.CallConvMask）。</summary>
    public enum IlCallingConvention
    {
        Winapi,
        Cdecl,
        StdCall,
    }
}
