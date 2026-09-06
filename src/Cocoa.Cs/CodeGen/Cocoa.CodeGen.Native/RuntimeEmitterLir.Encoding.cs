// 编码原语（自举 IO）：StringFromBytes（UTF8→string，MultiByteToWideChar 两遍直写 chars 区）与
// StringToBytes（string→UTF8 u8[]，手动 UTF-16→UTF-8 两阶段编码——WideCharToMultiByte 8 参超 syscall 上限）。
// 布局：CO 串 [len:4][chars:2×len]；u8[] [len:4][data@8]（8 字节对齐）。

using System;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;

using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    internal static partial class RuntimeEmitterLir
    {
        private sealed partial class RuntimeFunctionEmitter
    {
        // StringFromBytes(data:8=u8[]) → string：MultiByteToWideChar(CP_UTF8) 计数+写入两遍
        private void EmitStringFromBytes()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var result = NewPtr();

            var data = _args[0];
            var len = NewReg(4);
            Load(len, data, 0, 4);
            var src = NewPtr();
            Lea(src, data, 8);

            // 第一遍：目标宽字符数（CP_UTF8=65001）
            var wc = NewReg(4);
            SysCall(wc, "MultiByteToWideChar", 6, C(4, 65001), C(4, 0), src, len, C(8, 0), C(4, 0));
            Cmp(wc, 0);
            Jcc(LirCond.Equal, fail);

            // 分配 string 对象：[wc][chars]；size = ((wc+1)>>1)*4 + 4
            var objSize = NewReg(4);
            Mov(objSize, wc);
            AddI(objSize, objSize, 1);
            Shr(objSize, objSize, 1);
            Shl(objSize, objSize, 2);
            AddI(objSize, objSize, 4);
            CallRuntime(result, "Alloc", objSize);
            Cmp(result, 0);
            Jcc(LirCond.Equal, fail);
            Store(result, 0, wc, 4);
            var dst = NewPtr();
            Lea(dst, result, 4);
            var wc2 = NewReg(4);
            SysCall(wc2, "MultiByteToWideChar", 6, C(4, 65001), C(4, 0), src, len, dst, wc);
            Cmp(wc2, 0);
            Jcc(LirCond.Equal, fail);
            Jmp(done);

            Mark(fail);
            var empty = NewPtr();
            LeaData(empty, _emptyString);
            Mov(result, empty);

            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // StringToBytes(s:8) → u8[]：UTF-16→UTF-8 两阶段（count→encode）
        private void EmitStringToBytes()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var s = _args[0];
            var arr = NewPtr();
            var len = NewReg(4);
            Load(len, s, 0, 4);
            var lenBytes = NewReg(4);
            Shl(lenBytes, len, 1);

            // Phase 1：统计 UTF-8 字节数
            var sp = NewPtr();
            Lea(sp, s, 4);
            var end = NewPtr();
            Mov(end, sp);
            Add(end, end, lenBytes);
            var count = NewReg(4);
            Const(count, 0);
            var two = NewLabel();
            var threeB = NewLabel();
            var three = NewLabel();
            var four = NewLabel();
            var cntLoop = NewLabel();
            var cntDone = NewLabel();
            Mark(cntLoop);
            Cmp(sp, end);
            Jcc(LirCond.GreaterOrEqual, cntDone);
            var c1 = NewReg(4);
            Load(c1, sp, 0, 2);
            Cmp(c1, 0x80);
            Jcc(LirCond.AboveOrEqual, two);
            AddI(count, count, 1);
            AddI(sp, sp, 2);
            Jmp(cntLoop);
            Mark(two);
            Cmp(c1, 0x800);
            Jcc(LirCond.AboveOrEqual, threeB);
            AddI(count, count, 2);
            AddI(sp, sp, 2);
            Jmp(cntLoop);
            Mark(threeB);
            Cmp(c1, 0xD800);
            Jcc(LirCond.Less, three);
            Cmp(c1, 0xDC00);
            Jcc(LirCond.AboveOrEqual, three);
            AddI(count, count, 4);
            AddI(sp, sp, 4);
            Jmp(four);
            Mark(three);
            AddI(count, count, 3);
            AddI(sp, sp, 2);
            Jmp(cntLoop);
            Mark(four);
            Jmp(cntLoop);
            Mark(cntDone);

            CallRuntime(arr, "NewArray", count, C(4, 1));
            Cmp(arr, 0);
            Jcc(LirCond.Equal, fail);

            // Phase 2：编码写入 arr+8
            var sp2 = NewPtr();
            Lea(sp2, s, 4);
            var end2 = NewPtr();
            Mov(end2, sp2);
            Add(end2, end2, lenBytes);
            var dst = NewPtr();
            Lea(dst, arr, 8);
            var t2 = NewLabel();
            var t3b = NewLabel();
            var t3 = NewLabel();
            var t4 = NewLabel();
            var encLoop = NewLabel();
            var encDone = NewLabel();
            Mark(encLoop);
            Cmp(sp2, end2);
            Jcc(LirCond.GreaterOrEqual, encDone);
            var c2 = NewReg(4);
            Load(c2, sp2, 0, 2);
            Cmp(c2, 0x80);
            Jcc(LirCond.AboveOrEqual, t2);
            Store(dst, 0, c2, 1);
            AddI(dst, dst, 1);
            AddI(sp2, sp2, 2);
            Jmp(encLoop);
            Mark(t2);
            Cmp(c2, 0x800);
            Jcc(LirCond.AboveOrEqual, t3b);
            var b1 = NewReg(4);
            Shr(b1, c2, 6);
            Or(b1, b1, C(4, 0xC0));
            Store(dst, 0, b1, 1);
            var b2 = NewReg(4);
            And(b2, c2, C(4, 0x3F));
            Or(b2, b2, C(4, 0x80));
            Store(dst, 1, b2, 1);
            AddI(dst, dst, 2);
            AddI(sp2, sp2, 2);
            Jmp(encLoop);
            Mark(t3b);
            Cmp(c2, 0xD800);
            Jcc(LirCond.Less, t3);
            Cmp(c2, 0xDC00);
            Jcc(LirCond.AboveOrEqual, t3);
            Jmp(t4);
            Mark(t3);
            // 3 字节 BMP
            var e1 = NewReg(4);
            Shr(e1, c2, 12);
            Or(e1, e1, C(4, 0xE0));
            Store(dst, 0, e1, 1);
            var e2 = NewReg(4);
            Shr(e2, c2, 6);
            And(e2, e2, C(4, 0x3F));
            Or(e2, e2, C(4, 0x80));
            Store(dst, 1, e2, 1);
            var e3 = NewReg(4);
            And(e3, c2, C(4, 0x3F));
            Or(e3, e3, C(4, 0x80));
            Store(dst, 2, e3, 1);
            AddI(dst, dst, 3);
            AddI(sp2, sp2, 2);
            Jmp(encLoop);
            Mark(t4);
            // 代理对 → 4 字节
            var lo = NewReg(4);
            Load(lo, sp2, 2, 2);
            var cp = NewReg(4);
            Mov(cp, c2);
            SubI(cp, cp, 0xD800);
            Shl(cp, cp, 10);
            var loX = NewReg(4);
            Mov(loX, lo);
            SubI(loX, loX, 0xDC00);
            Or(cp, cp, loX);
            AddI(cp, cp, 0x10000);
            var f1 = NewReg(4);
            Shr(f1, cp, 18);
            Or(f1, f1, C(4, 0xF0));
            Store(dst, 0, f1, 1);
            var f2 = NewReg(4);
            Shr(f2, cp, 12);
            And(f2, f2, C(4, 0x3F));
            Or(f2, f2, C(4, 0x80));
            Store(dst, 1, f2, 1);
            var f3 = NewReg(4);
            Shr(f3, cp, 6);
            And(f3, f3, C(4, 0x3F));
            Or(f3, f3, C(4, 0x80));
            Store(dst, 2, f3, 1);
            var f4 = NewReg(4);
            And(f4, cp, C(4, 0x3F));
            Or(f4, f4, C(4, 0x80));
            Store(dst, 3, f4, 1);
            AddI(dst, dst, 4);
            AddI(sp2, sp2, 4);
            Jmp(encLoop);
            Mark(encDone);

            StoreRet(arr);
            Jmp(done);
            Mark(fail);
            CallRuntime(arr, "NewArray", C(4, 0), C(4, 1));
            Mark(done);
            StoreRet(arr);
            EndFunction(_currentFunction!, 8);
        }
        }
    }
}