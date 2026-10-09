using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// M6：运行期内存自省——SelfIntrospect 定位自身镜像内嵌 `.cocoa` 节并校验魔数。
    /// GetModuleHandleW(NULL) → 模块基址 → DOS/NT 头 → 节表遍历 → 比对节名 ".cocoa" →
    /// 按 VirtualAddress 定位数据区 → 校验前 5 字节 "COCOA"。返回 bool（0/1）。
    /// 纯读内存，不触碰磁盘；与 M4 写侧节名/魔数约定一致。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        // SelfIntrospect() → bool（1=自身 .cocoa 节存在且魔数正确）
        private void EmitSelfIntrospect()
        {
            var fail = NewLabel();
            var done = NewLabel();

            var baseAddr = NewPtr();
            SysCall(baseAddr, "GetModuleHandleW", 1, NullPtr());
            Cmp(baseAddr, 0);
            Jcc(LirCond.Equal, fail);

            // e_lfanew = *(u32*)(base + 0x3C)
            var eLfanew = NewReg(4);
            Load(eLfanew, baseAddr, 0x3C, 4);
            Cmp(eLfanew, 0);
            Jcc(LirCond.Equal, fail);

            // pe = base + e_lfanew
            var pe = NewPtr();
            Add(pe, baseAddr, eLfanew);

            // numberOfSections = *(u16*)(pe + 6)
            var sectionCount = NewReg(4);
            Load(sectionCount, pe, 6, 2);
            Cmp(sectionCount, 0);
            Jcc(LirCond.Equal, fail);

            // sizeOfOptionalHeader = *(u16*)(pe + 20)
            var optSize = NewReg(4);
            Load(optSize, pe, 20, 2);

            // sectionTable = pe + 24 + optSize
            var sectionTable = NewPtr();
            AddI(pe, pe, 24);
            Add(sectionTable, pe, optSize);

            // 遍历 i in [0, sectionCount)：节表每行 40B
            var index = NewReg(4);
            Mov(index, C(4, 0));
            var loop = NewLabel();
            var next = NewLabel();
            Mark(loop);
            Cmp(index, sectionCount);
            Jcc(LirCond.GreaterOrEqual, fail);

            // sec = sectionTable + index*40
            var sec = NewPtr();
            Mov(sec, sectionTable);
            var offset40 = NewReg(4);
            Imul(offset40, index, C(4, 40));
            Add(sec, sec, offset40);

            // 节名前 4 字节 == ".coc"（节名 ".cocoa" 的前 4 字节）
            var nameDword = NewReg(4);
            Load(nameDword, sec, 0, 4);
            Cmp(nameDword, 0x636F632E); // ".coc" 小端（'.'=0x2E,'c','o','c' → 0x636F632E）
            Jcc(LirCond.NotEqual, next);

            // 第 6 字节 'a'（offset 5；'.','c','o','c'(0..3) 'o'(4) 'a'(5)）
            var sixth = NewReg(4);
            Load(sixth, sec, 5, 1);
            Cmp(sixth, (int)'a');
            Jcc(LirCond.NotEqual, next);

            // 命中 .cocoa 节：VirtualAddress 在节偏移 12，数据区 = base + VA
            var virtualAddress = NewReg(4);
            Load(virtualAddress, sec, 12, 4);
            var data = NewPtr();
            Add(data, baseAddr, virtualAddress);

            // 校验魔数 "COCOA"（前 4 字节 "COCO" + 第 5 字节 'A'）
            var magic = NewReg(4);
            Load(magic, data, 0, 4);
            Cmp(magic, 0x4F434F43); // "COCO" 小端
            Jcc(LirCond.NotEqual, fail);
            var magic5 = NewReg(4);
            Load(magic5, data, 4, 1);
            Cmp(magic5, (int)'A');
            Jcc(LirCond.NotEqual, fail);

            Mov(index, C(4, 1));
            Jmp(done);

            Mark(next);
            AddI(index, index, 1);
            Jmp(loop);

            Mark(fail);
            Mov(index, C(4, 0));
            Mark(done);
            StoreRet(index);
            EndFunction(_currentFunction!, 4);
        }
    }
}
