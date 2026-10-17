using System;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;

using Cocoa.Targeting;
using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// 平台无关运行时 IR 生成：把原本 x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs）
    /// 合并为单一 IR 程序挂接。<br/>
    /// 平台差异收敛为：指针槽宽（8/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）。
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 LirToAssembler.SysCall 负责。
    /// </summary>
    internal static partial class RuntimeEmitterLir
    {
        public static void Append(LirProgram program, TargetPlatform platform)
        {
            var emitter = new RuntimeFunctionEmitter(program, platform);
            emitter.Emit();
        }
    }

}
