using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Interpreter;
using Cocoa.CodeGen.Managed.Writer;
using Cocoa.CodeGen.Native;

namespace Cocoa.IDE;

/// <summary>IDE 启动时的编译器后端注册（与 CLI Program.cs 对齐）：
/// 注册 managed/native 发射与解释器求值实现。</summary>
public static class CocoaServices
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (typeof(CocoaServices))
        {
            if (_initialized) return;

            // Core 不引用后端，经委托接入：managed/native 发射 + 解释器求值
            ManagedBackend.Register();
            NativeBackend.Register();
            InterpreterBackend.Register();

            _initialized = true;
        }
    }
}