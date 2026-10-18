using System;
using System.Linq;
using Cocoa.CodeGen.Native.Lir;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// S1（native-s1 Phase A）：exe 拆分形态的运行时原语导入注册。
    /// 拆分形态下运行时原语不内联（<c>RuntimeEmitterLir.Append</c> 跳过），exe 对原语的调用
    /// （<c>LirOpCode.Call</c> + <c>LirOperand.Runtime(name)</c> + 陷阱点 <c>DivByZero/StackOverflow/…</c>）
    /// 全部经 IAT 导入 <c>System.Core.Native.dll</c> 的 <c>Cocoa_Rt_*</c>（默认内联形态零改动、字节不变）。
    /// </summary>
    internal static class RuntimeSplitter
    {
        /// <summary>把全部平台发射原语的导入挂到 LIR 程序（供 EmitData 建 IAT 槽；重复注册值等价无害）。</summary>
        public static void RegisterImports(LirProgram program, TargetPlatform platform)
        {
            foreach (var name in RuntimePrimitiveCoverage.EmittedPrimitiveNames(platform))
            {
                program.Imports.Add(new LirImport(NativeSymbolMangling.SystemCoreNativeName, NativeSymbolMangling.RtExportName(name), Cdecl: false));
            }

            // S1 Phase B：共享类型表入口（exe 启动 init stub 登记；String/Array 身份跨模块唯一）
            program.Imports.Add(new LirImport(NativeSymbolMangling.SystemCoreNativeName, NativeSymbolMangling.RtTypeGet, Cdecl: false));
            program.Imports.Add(new LirImport(NativeSymbolMangling.SystemCoreNativeName, NativeSymbolMangling.RtTypeRegister, Cdecl: false));
        }
    }
}