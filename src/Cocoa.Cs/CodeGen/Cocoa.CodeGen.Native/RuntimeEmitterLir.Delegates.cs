using System;

using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    internal static partial class RuntimeEmitterLir
    {
        private sealed partial class RuntimeFunctionEmitter
        {
            // ------------------------------------------------------------------
            // 委托列表助手（6e-M22 委托真实类型化 M5）：函数值对象指针数组的 Combine/Remove/Equals。
            // 数组布局同 NewArray：[0..4) 长度；[8..) 元素（元素宽固定 8，存函数值对象指针，
            // x86 下指针 32 位 + 高位零填充）。
            // ------------------------------------------------------------------

            private void EmitDelegateCombine()
            {
                var a = _args[0];
                var b = _args[1];
                var la = NewReg(4);
                Load(la, a, 0, 4);
                var lb = NewReg(4);
                Load(lb, b, 0, 4);
                var total = NewReg(4);
                Add(total, la, lb);

                var array = NewPtr();
                CallRuntime(array, "NewArray", total, C(4, 8));
                EmitCopyRange(a, array, la, C(4, 0), C(4, 0));
                EmitCopyRange(b, array, lb, C(4, 0), la);
                StoreRet(array);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>移除 a 中最后一个完整的 b 连续子序列（函数值 fnptr+env 双字相等）；找不到返回原 a。</summary>
            private void EmitDelegateRemove()
            {
                var a = _args[0];
                var b = _args[1];
                var la = NewReg(4);
                Load(la, a, 0, 4);
                var lb = NewReg(4);
                Load(lb, b, 0, 4);

                var unchanged = NewLabel();
                Cmp(la, lb);
                Jcc(LirCond.Less, unchanged);
                Cmp(lb, 0);
                Jcc(LirCond.Equal, unchanged);

                var pos = NewReg(4);
                Sub(pos, la, lb);
                var find = NewLabel();
                var notWin = NewLabel();
                var nomatch = NewLabel();
                var found = NewLabel();
                var done = NewLabel();
                var k = NewReg(4);
                var winLoop = NewLabel();
                var winDone = NewLabel();

                Mark(find);
                Mov(k, C(4, 0));
                Mark(winLoop);
                Cmp(k, lb);
                Jcc(LirCond.GreaterOrEqual, winDone);

                var eaVal = NewPtr();
                var eaAddr = NewPtr();
                var eao = NewReg(4);
                Mov(eao, pos);
                Add(eao, eao, k);
                Shl(eao, eao, 3);
                Lea(eaAddr, a, 8);
                Add(eaAddr, eaAddr, eao);
                Load(eaVal, eaAddr, 0, 8);

                var ebVal = NewPtr();
                var ebAddr = NewPtr();
                var ebo = NewReg(4);
                Mov(ebo, k);
                Shl(ebo, ebo, 3);
                Lea(ebAddr, b, 8);
                Add(ebAddr, ebAddr, ebo);
                Load(ebVal, ebAddr, 0, 8);

                EmitFnEqualsOrJump(eaVal, ebVal, notWin);
                AddI(k, k, 1);
                Jmp(winLoop);

                Mark(winDone);
                Jmp(found);
                Mark(notWin);
                SubI(pos, pos, 1);
                Cmp(pos, 0);
                Jcc(LirCond.Less, nomatch);
                Jmp(find);

                Mark(found);
                var result = NewPtr();
                var newLen = NewReg(4);
                Sub(newLen, la, lb);
                CallRuntime(result, "NewArray", newLen, C(4, 8));
                EmitCopyRange(a, result, pos, C(4, 0), C(4, 0));
                var after = NewReg(4);
                Add(after, pos, lb);
                var rest = NewReg(4);
                Sub(rest, la, after);
                EmitCopyRange(a, result, rest, after, pos);
                StoreRet(result);
                Jmp(done);

                Mark(nomatch);
                StoreRet(a);
                Jmp(done);
                Mark(unchanged);
                StoreRet(a);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>两个委托调用列表逐元素相等（长度 + 每个函数值 fnptr+env 双字）。返回 1/0。</summary>
            private void EmitDelegateEquals()
            {
                var a = _args[0];
                var b = _args[1];
                var la = NewReg(4);
                Load(la, a, 0, 4);
                var lb = NewReg(4);
                Load(lb, b, 0, 4);
                var notEqual = NewLabel();
                var equal = NewLabel();
                var done = NewLabel();
                var result = NewReg(4);

                Cmp(la, lb);
                Jcc(LirCond.NotEqual, notEqual);

                var i = NewReg(4);
                Mov(i, C(4, 0));
                var loop = NewLabel();
                Mark(loop);
                Cmp(i, lb);
                Jcc(LirCond.GreaterOrEqual, equal);

                var eaVal = NewPtr();
                var eaAddr = NewPtr();
                var eao = NewReg(4);
                Mov(eao, i);
                Shl(eao, eao, 3);
                Lea(eaAddr, a, 8);
                Add(eaAddr, eaAddr, eao);
                Load(eaVal, eaAddr, 0, 8);

                var ebVal = NewPtr();
                var ebAddr = NewPtr();
                var ebo = NewReg(4);
                Mov(ebo, i);
                Shl(ebo, ebo, 3);
                Lea(ebAddr, b, 8);
                Add(ebAddr, ebAddr, ebo);
                Load(ebVal, ebAddr, 0, 8);

                EmitFnEqualsOrJump(eaVal, ebVal, notEqual);
                AddI(i, i, 1);
                Jmp(loop);

                Mark(equal);
                Mov(result, C(4, 1));
                Jmp(done);
                Mark(notEqual);
                Mov(result, C(4, 0));
                Mark(done);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            /// <summary>两个函数值对象（fnptr+env 双字）相等则继续，不等跳转 notEqualLabel。</summary>
            private void EmitFnEqualsOrJump(LirVirtualRegister x, LirVirtualRegister y, int notEqualLabel)
            {
                var pointerSize = _isX64 ? 8 : 4;
                var fx = NewPtr();
                Load(fx, x, pointerSize, pointerSize);
                var fy = NewPtr();
                Load(fy, y, pointerSize, pointerSize);
                Cmp(fx, fy);
                Jcc(LirCond.NotEqual, notEqualLabel);
                var ex = NewPtr();
                Load(ex, x, pointerSize * 2, pointerSize);
                var ey = NewPtr();
                Load(ey, y, pointerSize * 2, pointerSize);
                Cmp(ex, ey);
                Jcc(LirCond.NotEqual, notEqualLabel);
            }

            /// <summary>把 src 元素区 [srcBase..srcBase+count) 复制到 dst 的 dstBase 元素起（寄存器计数）。</summary>
            private void EmitCopyRange(LirVirtualRegister src, LirVirtualRegister dst, LirVirtualRegister count, LirVirtualRegister srcBase, LirVirtualRegister dstBase)
            {
                var index = NewReg(4);
                Mov(index, C(4, 0));
                var loop = NewLabel();
                var done = NewLabel();

                Mark(loop);
                Cmp(index, count);
                Jcc(LirCond.GreaterOrEqual, done);

                var srcOffset = NewReg(4);
                Mov(srcOffset, index);
                Add(srcOffset, srcOffset, srcBase);
                Shl(srcOffset, srcOffset, 3);
                var srcAddr = NewPtr();
                Lea(srcAddr, src, 8);
                Add(srcAddr, srcAddr, srcOffset);

                var value = NewPtr();
                Load(value, srcAddr, 0, 8);

                var dstOffset = NewReg(4);
                Mov(dstOffset, index);
                Add(dstOffset, dstOffset, dstBase);
                Shl(dstOffset, dstOffset, 3);
                var dstAddr = NewPtr();
                Lea(dstAddr, dst, 8);
                Add(dstAddr, dstAddr, dstOffset);
                Store(dstAddr, 0, value, 8);

                AddI(index, index, 1);
                Jmp(loop);
                Mark(done);
            }
        }
    }
}
