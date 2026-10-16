using Cocoa.CodeGen.Managed;
using Cocoa.CodeGen.Interpreter;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.ObjectFormat.PE;
using System.Runtime.CompilerServices;

namespace Cocoa.Tests.CodeGen
{
    /// <summary>
    /// 娴嬭瘯绋嬪簭闆嗘ā鍧楀垵濮嬪寲锛氭敞鍐屾媶鍒嗗悗鐨?managed/native 鍚庣鍙戝皠濮旀墭涓庤В閲婂櫒姹傚€煎鎵樸€?
    /// Core 涓嶅紩鐢ㄥ悗绔紝缁忓鎵樻帴鍏ワ紱娴嬭瘯鐩存帴璋冪敤 Compilation.Emit/EmitNative/Evaluate 鍓嶅繀椤诲凡娉ㄥ唽銆?
    /// </summary>
    internal static class BackendRegistration
    {
        [ModuleInitializer]
        internal static void RegisterBackends()
        {
            ManagedBackend.Register();
            NativeBackend.Register();
            InterpreterBackend.Register();
        }
    }
}
