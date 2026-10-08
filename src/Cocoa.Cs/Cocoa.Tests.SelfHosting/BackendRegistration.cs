using Cocoa.CodeGen.Managed.Writer;
using Cocoa.CodeGen.Interpreter;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using System.Runtime.CompilerServices;

namespace Cocoa.Tests
{
    /// <summary>
    /// 自举测试程序集模块初始化：注册拆分后的 managed/native 后端发射委托与解释器求值委托。
    /// Cocoa.Tests.SelfHosting 独立测试工程副本（与原 Cocoa.Tests/BackendRegistration.cs 同构）。
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