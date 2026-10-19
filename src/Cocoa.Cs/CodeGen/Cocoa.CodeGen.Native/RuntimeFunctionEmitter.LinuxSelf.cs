using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// T3 N5b（弱化）：Linux SelfIntrospect——与 Windows 同语义，读 data 段 `.cocoa` 元数据指针槽
    /// （ElfFileWriter 按 .cocoa RVA 回填，ET_EXEC 固定基址免重定位）判非零。
    /// 不再扫描自身镜像（/proc/self/exe）与 ELF 节表。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        // SelfIntrospect() → bool（1=自身 .cocoa 元数据指针已回填）
        private void EmitSelfIntrospectLinux()
        {
            var slot = NewPtr();
            LeaData(slot, _cocoaMetadataPtr);
            var ptr = NewPtr();
            Load(ptr, slot, 0, 8);
            var ok = NewReg(4);
            Cmp(ptr, 0);
            Setcc(ok, LirCond.NotEqual);
            StoreRet(ok);
            EndFunction(_currentFunction!, 4);
        }
    }
}
