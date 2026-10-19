using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// M6（弱化）：运行期自省去 PE 头遍历——SelfIntrospect 读构建期回填的 `.cocoa` 元数据指针槽
    /// （data 段绝对 VA，PeFileWriter 按 cocoaRva 回填并经 .reloc 重定位），非 0 即判定元数据存在。
    /// 不再 GetModuleHandleW(NULL) 解析自身镜像、不再遍历节表比对节名、不再校验魔数。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        // SelfIntrospect() → bool（1=自身 .cocoa 元数据指针已回填）
        private void EmitSelfIntrospect()
        {
            var slot = NewPtr();
            LeaData(slot, _cocoaMetadataPtr);
            var ptr = NewPtr();
            Load(ptr, slot, 0, _isX64 ? 8 : 4);
            var ok = NewReg(4);
            Cmp(ptr, 0);
            Setcc(ok, LirCond.NotEqual);
            StoreRet(ok);
            EndFunction(_currentFunction!, 4);
        }
    }
}
