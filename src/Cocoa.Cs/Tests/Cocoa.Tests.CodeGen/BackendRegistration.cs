using Cocoa.CodeGen.Managed;
using Cocoa.CodeGen.Interpreter;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.ObjectFormat.PE;
using System.Runtime.CompilerServices;

namespace Cocoa.Tests.CodeGen
{
    /// <summary>
    /// 测试程序集模块初始化：注册拆分后。managed/native 后端发射委托与解释器求值委托。
    /// Core 不引用后端，经委托接入；测试直接调用 Compilation.Emit/EmitNative/Evaluate 前必须已注册。
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
