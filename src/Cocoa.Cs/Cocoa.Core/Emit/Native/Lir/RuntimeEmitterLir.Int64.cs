using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>
    /// 平台无关运行时 IR 生成：把原 x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs）
    /// 合并为单一 IR 程序挂接。<br/>
    /// 平台差异收敛为：指针槽宽（8/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）、
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 LirToAssembler.SysCall 负责。
    /// </summary>
    internal static partial class RuntimeEmitterLir
    {
        private sealed partial class RuntimeFunctionEmitter
        {
            // Int64ToString(value) → 字符串对象（x64 单 8 字节寄存器 / x86 lo=4,hi=4 双槽）
            // 思路：拆 lo/hi → 取绝对值 → 用 DivChain（128 位 ÷ 16 位）逐位抽十进制数字写入
            // 字符缓冲，再 Alloc + CopyChars 成对象。与 32 位 EmitIntToString 同构。
            // ------------------------------------------------------------------

            private void EmitInt64ToString()
            {
                var lo = NewReg(4);
                var hi = NewReg(4);
                if (_isX64)
                {
                    var tmp = NewReg(8);
                    Mov(tmp, _args[0]);
                    var buf0 = NewReg(8);
                    var scratch = NewReg(8);
                    LeaSlot(buf0, scratch);
                    Store(buf0, 0, tmp, 8);
                    Load(lo, buf0, 0, 4);
                    Load(hi, buf0, 4, 4);
                }
                else
                {
                    Mov(lo, _args[0]);
                    Mov(hi, _args[1]);
                }

                var sign = NewReg(4);
                Const(sign, 0);

                var neg = NewReg(4);
                Mov(neg, hi);
                Shr(neg, neg, 31);
                var isPos = NewLabel();
                Cmp(neg, 0);
                Jcc(LirCond.Equal, isPos);
                Const(sign, 1);
                Neg64Pair(lo, hi);
                Mark(isPos);

                var buf = NewReg(8);
                var bufScratch = NewReg(8);
                LeaSlot(buf, bufScratch);
                Store(buf, 0, lo, 4);
                Store(buf, 4, hi, 4);
                var zero = C(4, 0);
                Store(buf, 8, zero, 4);
                Store(buf, 12, zero, 4);

                var end = NewReg(8);
                Lea(end, buf, 64);
                var tail = NewReg(8);
                Mov(tail, end);
                var ten = C(4, 10);

                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                var acc = NewReg(4);
                Const(acc, 0);
                for (var i = 0; i < 8; i++)
                {
                    var w = NewReg(4);
                    Load(w, buf, i * 2, 2);
                    Or(acc, acc, w);
                }
                Cmp(acc, 0);
                Jcc(LirCond.Equal, done);

                var rem = NewReg(4);
                CallRuntime(rem, "DivChain", buf, ten);
                var ch = NewReg(4);
                AddI(ch, rem, (int)'0');
                SubI(tail, tail, 2);
                Store(tail, 0, ch, 2);
                Jmp(loop);
                Mark(done);

                Cmp(tail, end);
                var hasDigits = NewLabel();
                Jcc(LirCond.NotEqual, hasDigits);
                SubI(tail, tail, 2);
                var zch = C(4, (int)'0');
                Store(tail, 0, zch, 2);
                Mark(hasDigits);

                Cmp(sign, 0);
                var noSign = NewLabel();
                Jcc(LirCond.Equal, noSign);
                SubI(tail, tail, 2);
                var minus = C(4, (int)'-');
                Store(tail, 0, minus, 2);
                Mark(noSign);

                var lenBytes = NewReg(4);
                Sub(lenBytes, end, tail);

                var size = NewReg(4);
                Mov(size, lenBytes);
                AddI(size, size, 2);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                var oom = NewLabel();
                Jcc(LirCond.Equal, oom);

                var chars = NewReg(4);
                Mov(chars, lenBytes);
                Shr(chars, chars, 1);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, lenBytes);
                AddI(count, count, 2);
                Shr(count, count, 2);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, tail, count);

                StoreRet(obj);
                var doneL = NewLabel();
                Jmp(doneL);

                Mark(oom);
                var z2 = C(8, 0);
                StoreRet(z2);

                Mark(doneL);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>
            /// UInt64ToString：无符号 64 位 → 十进制字符串（6e-M21 Phase 7 补遗）。
            /// 与 Int64ToString 共用除 10 循环（DivChain 为无符号 Udiv），仅去掉符号分支。
            /// </summary>
            private void EmitUInt64ToString()
            {
                var lo = NewReg(4);
                var hi = NewReg(4);
                if (_isX64)
                {
                    var tmp = NewReg(8);
                    Mov(tmp, _args[0]);
                    var buf0 = NewReg(8);
                    var scratch = NewReg(8);
                    LeaSlot(buf0, scratch);
                    Store(buf0, 0, tmp, 8);
                    Load(lo, buf0, 0, 4);
                    Load(hi, buf0, 4, 4);
                }
                else
                {
                    Mov(lo, _args[0]);
                    Mov(hi, _args[1]);
                }

                var buf = NewReg(8);
                var bufScratch = NewReg(8);
                LeaSlot(buf, bufScratch);
                Store(buf, 0, lo, 4);
                Store(buf, 4, hi, 4);
                var zero = C(4, 0);
                Store(buf, 8, zero, 4);
                Store(buf, 12, zero, 4);

                var end = NewReg(8);
                Lea(end, buf, 64);
                var tail = NewReg(8);
                Mov(tail, end);
                var ten = C(4, 10);

                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                var acc = NewReg(4);
                Const(acc, 0);
                for (var i = 0; i < 8; i++)
                {
                    var w = NewReg(4);
                    Load(w, buf, i * 2, 2);
                    Or(acc, acc, w);
                }
                Cmp(acc, 0);
                Jcc(LirCond.Equal, done);

                var rem = NewReg(4);
                CallRuntime(rem, "DivChain", buf, ten);
                var ch = NewReg(4);
                AddI(ch, rem, (int)'0');
                SubI(tail, tail, 2);
                Store(tail, 0, ch, 2);
                Jmp(loop);
                Mark(done);

                Cmp(tail, end);
                var hasDigits = NewLabel();
                Jcc(LirCond.NotEqual, hasDigits);
                SubI(tail, tail, 2);
                var zch = C(4, (int)'0');
                Store(tail, 0, zch, 2);
                Mark(hasDigits);

                var lenBytes = NewReg(4);
                Sub(lenBytes, end, tail);

                var size = NewReg(4);
                Mov(size, lenBytes);
                AddI(size, size, 2);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                var oom = NewLabel();
                Jcc(LirCond.Equal, oom);

                var chars = NewReg(4);
                Mov(chars, lenBytes);
                Shr(chars, chars, 1);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, lenBytes);
                AddI(count, count, 2);
                Shr(count, count, 2);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, tail, count);

                StoreRet(obj);
                var doneL = NewLabel();
                Jmp(doneL);

                Mark(oom);
                var zz = C(8, 0);
                StoreRet(zz);

                Mark(doneL);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ParseInt64(s:8) → long（8 字节返回值：x64 RAX / x86 EDX:EAX）
            // 逐字符 acc = acc*10 + digit（64 位累加，Imul64 + Movzx64 零扩展）。
            // ------------------------------------------------------------------

            private void EmitParseInt64()
            {
                var s = _args[0];
                var loop = NewLabel();
                var done = NewLabel();

                var len = NewReg(4);
                Load(len, s, 0, 4);
                var p = NewReg(8);
                Lea(p, s, 4);

                // 负号前置检查：s[0] == '-' → neg=1，len-1、指针后移一个 UTF-16 字符
                var neg = NewReg(4);
                Const(neg, 0);
                var skipNeg = NewLabel();
                var c0 = NewReg(4);
                Load(c0, s, 4, 2);
                Cmp(c0, C(4, '-'));
                Jcc(LirCond.NotEqual, skipNeg);
                Const(neg, 1);
                AddI(len, len, -1);
                var p2 = NewReg(8);
                Lea(p2, s, 6);
                Mov(p, p2);
                Mark(skipNeg);

                var acc = NewReg(8);
                Const(acc, 0);
                var i = C(4, 0);
                var ten = C(8, 10);

                Mark(loop);
                Cmp(i, len);
                Jcc(LirCond.GreaterOrEqual, done);
                var ch = NewReg(4);
                Load(ch, p, 0, 2);
                var nextP = NewReg(8);
                Lea(nextP, p, 2);
                Mov(p, nextP);
                AddI(ch, ch, -'0');

                var dig = NewReg(8);
                Add(LirOpCode.Movzx64, dig, LirOperand.Reg(ch));
                var prod = NewReg(8);
                Imul64(prod, acc, ten);
                Add(acc, prod, dig);

                AddI(i, i, 1);
                Jmp(loop);

                Mark(done);

                // 负号修正：neg != 0 → Neg64(acc)
                Cmp(neg, 0);
                var outLbl = NewLabel();
                Jcc(LirCond.Equal, outLbl);
                Add(LirOpCode.Neg64, acc, LirOperand.Reg(acc));
                Mark(outLbl);

                StoreRet(acc);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // Idiv64(aLo,aHi,bLo,bHi) → 商 EDX:EAX（x86 64 位有符号除法辅助）
            // 绝对值 64/64 恢复式移位减法（IR 纯实现），最后按符号修正。
            // ------------------------------------------------------------------

            private void EmitIdiv64()
            {
                var aLo = _args[0];
                var aHi = _args[1];
                var bLo = _args[2];
                var bHi = _args[3];

                var qLo = NewReg(4);
                var qHi = NewReg(4);
                var rLo = NewReg(4);
                var rHi = NewReg(4);
                Const(qLo, 0);
                Const(qHi, 0);
                Const(rLo, 0);
                Const(rHi, 0);

                var bz = NewReg(4);
                Or(bz, bLo, bHi);
                Cmp(bz, 0);
                var notZero = NewLabel();
                Jcc(LirCond.NotEqual, notZero);
                CallRuntime(null, "DivByZero");
                Mark(notZero);

                var aNeg = NewReg(4);
                Mov(aNeg, aHi);
                Shr(aNeg, aNeg, 31);
                var bNeg = NewReg(4);
                Mov(bNeg, bHi);
                Shr(bNeg, bNeg, 31);
                var qsign = NewReg(4);
                Xor(qsign, aNeg, bNeg);

                var avLo = NewReg(4);
                var avHi = NewReg(4);
                var bvLo = NewReg(4);
                var bvHi = NewReg(4);
                Mov(avLo, aLo);
                Mov(avHi, aHi);
                Mov(bvLo, bLo);
                Mov(bvHi, bHi);

                var aPos = NewLabel();
                Cmp(aNeg, 0);
                Jcc(LirCond.Equal, aPos);
                Neg64Pair(avLo, avHi);
                Mark(aPos);

                var bPos = NewLabel();
                Cmp(bNeg, 0);
                Jcc(LirCond.Equal, bPos);
                Neg64Pair(bvLo, bvHi);
                Mark(bPos);

                var bit = C(4, 63);
                var bitLoop = NewLabel();
                var bitDone = NewLabel();
                Mark(bitLoop);
                Cmp(bit, 0);
                Jcc(LirCond.Less, bitDone);

                ShiftLeft64(rLo, rHi);
                var aBitReg = NewReg(4);
                LoadABit(aBitReg, avLo, avHi, bit);
                Or(rLo, rLo, aBitReg);

                var cmpRes = NewReg(4);
                Uge64(cmpRes, rLo, rHi, bvLo, bvHi);
                var skipSub = NewLabel();
                Cmp(cmpRes, 0);
                Jcc(LirCond.Equal, skipSub);
                Sub64(rLo, rHi, bvLo, bvHi);
                SetBit64(qLo, qHi, bit);
                Mark(skipSub);

                SubI(bit, bit, 1);
                Jmp(bitLoop);
                Mark(bitDone);

                Cmp(qsign, 0);
                var qPos = NewLabel();
                Jcc(LirCond.Equal, qPos);
                Neg64Pair(qLo, qHi);
                Mark(qPos);

                var q = NewReg(8);
                var qbuf = NewReg(8);
                var qscratch = NewReg(8);
                LeaSlot(qbuf, qscratch);
                Store(qbuf, 0, qLo, 4);
                Store(qbuf, 4, qHi, 4);
                Load(q, qbuf, 0, 8);
                StoreRet(q);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // Irem64(aLo,aHi,bLo,bHi) → 余数 EDX:EAX（符号同被除数）
            // 与 Idiv64 共用除法核心，最后返回余数并按 a 的符号修正。
            // ------------------------------------------------------------------

            private void EmitIrem64()
            {
                var aLo = _args[0];
                var aHi = _args[1];
                var bLo = _args[2];
                var bHi = _args[3];

                var qLo = NewReg(4);
                var qHi = NewReg(4);
                var rLo = NewReg(4);
                var rHi = NewReg(4);
                Const(qLo, 0);
                Const(qHi, 0);
                Const(rLo, 0);
                Const(rHi, 0);

                var bz = NewReg(4);
                Or(bz, bLo, bHi);
                Cmp(bz, 0);
                var notZero = NewLabel();
                Jcc(LirCond.NotEqual, notZero);
                CallRuntime(null, "DivByZero");
                Mark(notZero);

                var aNeg = NewReg(4);
                Mov(aNeg, aHi);
                Shr(aNeg, aNeg, 31);

                var avLo = NewReg(4);
                var avHi = NewReg(4);
                var bvLo = NewReg(4);
                var bvHi = NewReg(4);
                Mov(avLo, aLo);
                Mov(avHi, aHi);
                Mov(bvLo, bLo);
                Mov(bvHi, bHi);

                var aPos = NewLabel();
                Cmp(aNeg, 0);
                Jcc(LirCond.Equal, aPos);
                Neg64Pair(avLo, avHi);
                Mark(aPos);

                var bNeg = NewReg(4);
                Mov(bNeg, bHi);
                Shr(bNeg, bNeg, 31);
                var bPos = NewLabel();
                Cmp(bNeg, 0);
                Jcc(LirCond.Equal, bPos);
                Neg64Pair(bvLo, bvHi);
                Mark(bPos);

                var bit = C(4, 63);
                var bitLoop = NewLabel();
                var bitDone = NewLabel();
                Mark(bitLoop);
                Cmp(bit, 0);
                Jcc(LirCond.Less, bitDone);

                ShiftLeft64(rLo, rHi);
                var aBitReg = NewReg(4);
                LoadABit(aBitReg, avLo, avHi, bit);
                Or(rLo, rLo, aBitReg);

                var cmpRes = NewReg(4);
                Uge64(cmpRes, rLo, rHi, bvLo, bvHi);
                var skipSub = NewLabel();
                Cmp(cmpRes, 0);
                Jcc(LirCond.Equal, skipSub);
                Sub64(rLo, rHi, bvLo, bvHi);
                SetBit64(qLo, qHi, bit);
                Mark(skipSub);

                SubI(bit, bit, 1);
                Jmp(bitLoop);
                Mark(bitDone);

                Cmp(aNeg, 0);
                var rPos = NewLabel();
                Jcc(LirCond.Equal, rPos);
                Neg64Pair(rLo, rHi);
                Mark(rPos);

                var r = NewReg(8);
                var rbuf = NewReg(8);
                var rscratch = NewReg(8);
                LeaSlot(rbuf, rscratch);
                Store(rbuf, 0, rLo, 4);
                Store(rbuf, 4, rHi, 4);
                Load(r, rbuf, 0, 8);
                StoreRet(r);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // Udiv64(aLo,aHi,bLo,bHi) → 商 EDX:EAX（6e-M21 Phase 5，无符号恢复余数法）
            // 与 Idiv64 共用除法核心结构，无符号修正步骤。
            // ------------------------------------------------------------------

            private void EmitUdiv64()
            {
                var aLo = _args[0];
                var aHi = _args[1];
                var bLo = _args[2];
                var bHi = _args[3];

                var qLo = NewReg(4);
                var qHi = NewReg(4);
                var rLo = NewReg(4);
                var rHi = NewReg(4);
                Const(qLo, 0);
                Const(qHi, 0);
                Const(rLo, 0);
                Const(rHi, 0);

                var bz = NewReg(4);
                Or(bz, bLo, bHi);
                Cmp(bz, 0);
                var notZero = NewLabel();
                Jcc(LirCond.NotEqual, notZero);
                CallRuntime(null, "DivByZero");
                Mark(notZero);

                var bit = C(4, 63);
                var bitLoop = NewLabel();
                var bitDone = NewLabel();
                Mark(bitLoop);
                Cmp(bit, 0);
                Jcc(LirCond.Less, bitDone);

                ShiftLeft64(rLo, rHi);
                var aBitReg = NewReg(4);
                LoadABit(aBitReg, aLo, aHi, bit);
                Or(rLo, rLo, aBitReg);

                var cmpRes = NewReg(4);
                Uge64(cmpRes, rLo, rHi, bLo, bHi);
                var skipSub = NewLabel();
                Cmp(cmpRes, 0);
                Jcc(LirCond.Equal, skipSub);
                Sub64(rLo, rHi, bLo, bHi);
                SetBit64(qLo, qHi, bit);
                Mark(skipSub);

                SubI(bit, bit, 1);
                Jmp(bitLoop);
                Mark(bitDone);

                var q = NewReg(8);
                var qbuf = NewReg(8);
                var qscratch = NewReg(8);
                LeaSlot(qbuf, qscratch);
                Store(qbuf, 0, qLo, 4);
                Store(qbuf, 4, qHi, 4);
                Load(q, qbuf, 0, 8);
                StoreRet(q);
                EndFunction(_currentFunction!, 8);
            }

            private void EmitUrem64()
            {
                var aLo = _args[0];
                var aHi = _args[1];
                var bLo = _args[2];
                var bHi = _args[3];

                var qLo = NewReg(4);
                var qHi = NewReg(4);
                var rLo = NewReg(4);
                var rHi = NewReg(4);
                Const(qLo, 0);
                Const(qHi, 0);
                Const(rLo, 0);
                Const(rHi, 0);

                var bz = NewReg(4);
                Or(bz, bLo, bHi);
                Cmp(bz, 0);
                var notZero = NewLabel();
                Jcc(LirCond.NotEqual, notZero);
                CallRuntime(null, "DivByZero");
                Mark(notZero);

                var bit = C(4, 63);
                var bitLoop = NewLabel();
                var bitDone = NewLabel();
                Mark(bitLoop);
                Cmp(bit, 0);
                Jcc(LirCond.Less, bitDone);

                ShiftLeft64(rLo, rHi);
                var aBitReg = NewReg(4);
                LoadABit(aBitReg, aLo, aHi, bit);
                Or(rLo, rLo, aBitReg);

                var cmpRes = NewReg(4);
                Uge64(cmpRes, rLo, rHi, bLo, bHi);
                var skipSub = NewLabel();
                Cmp(cmpRes, 0);
                Jcc(LirCond.Equal, skipSub);
                Sub64(rLo, rHi, bLo, bHi);
                SetBit64(qLo, qHi, bit);
                Mark(skipSub);

                SubI(bit, bit, 1);
                Jmp(bitLoop);
                Mark(bitDone);

                var r = NewReg(8);
                var rbuf = NewReg(8);
                var rscratch = NewReg(8);
                LeaSlot(rbuf, rscratch);
                Store(rbuf, 0, rLo, 4);
                Store(rbuf, 4, rHi, 4);
                Load(r, rbuf, 0, 8);
                StoreRet(r);
                EndFunction(_currentFunction!, 8);
            }

            // ---- 64 位整型运算辅助（操作 4 字节对）----

            /// <summary>(hi:lo) = -(hi:lo)（二补码）。</summary>
            private void Neg64Pair(LirVirtualRegister lo, LirVirtualRegister hi)
            {
                Neg(lo);
                var loZero = NewLabel();
                Cmp(lo, 0);
                Jcc(LirCond.Equal, loZero);
                // 低 32 位取反后有借位（lo != 0）→ 高 32 位仅按位取反（~hi），不再 +1
                Xor(hi, hi, C(4, unchecked((int)0xFFFFFFFFu)));
                var d = NewLabel();
                Jmp(d);
                Mark(loZero);
                // 低 32 位取反进位（lo == 0）→ 高 32 位取反再加 1（~hi + 1）
                Xor(hi, hi, C(4, unchecked((int)0xFFFFFFFFu)));
                AddI(hi, hi, 1);
                Mark(d);
            }

            /// <summary>(hi:lo) = (hi:lo) << 1（逻辑左移，进位跨 32 位）。</summary>
            private void ShiftLeft64(LirVirtualRegister lo, LirVirtualRegister hi)
            {
                var t = NewReg(4);
                Mov(t, lo);
                Shl(t, t, 1);
                var carry = NewReg(4);
                Mov(carry, lo);
                Shr(carry, carry, 31);
                Mov(lo, t);
                var th = NewReg(4);
                Mov(th, hi);
                Shl(th, th, 1);
                Or(th, th, carry);
                Mov(hi, th);
            }

            /// <summary>取 (hi:lo) 的第 bit 位（0/1）。bit 为 0..63 的寄存器。</summary>
            private void LoadABit(LirVirtualRegister dst, LirVirtualRegister lo, LirVirtualRegister hi, LirVirtualRegister bit)
            {
                var dword = NewReg(4);
                Mov(dword, bit);
                Shr(dword, dword, 5);
                var isHi = NewLabel();
                var got = NewLabel();
                Cmp(dword, 0);
                Jcc(LirCond.NotEqual, isHi);
                var sh = NewReg(4);
                Mov(sh, bit);
                And(sh, sh, C(4, 31));
                var v = NewReg(4);
                Mov(v, lo);
                Shr(v, v, sh);
                And(v, v, C(4, 1));
                Mov(dst, v);
                Jmp(got);
                Mark(isHi);
                var sh2 = NewReg(4);
                Mov(sh2, bit);
                And(sh2, sh2, C(4, 31));
                var vh = NewReg(4);
                Mov(vh, hi);
                Shr(vh, vh, sh2);
                And(vh, vh, C(4, 1));
                Mov(dst, vh);
                Mark(got);
            }

            /// <summary>无符号 >=：(rHi:rLo) >= (bHi:bLo) → dst（1/0）。</summary>
            private void Uge64(LirVirtualRegister dst, LirVirtualRegister rLo, LirVirtualRegister rHi, LirVirtualRegister bLo, LirVirtualRegister bHi)
            {
                Cmp(rHi, bHi);
                var hiLt = NewLabel();
                Jcc(LirCond.Below, hiLt);
                var hiGt = NewLabel();
                Jcc(LirCond.Above, hiGt);
                // rHi == bHi：比较低 32 位
                Cmp(rLo, bLo);
                var loGe = NewLabel();
                Jcc(LirCond.AboveOrEqual, loGe);
                Const(dst, 0);
                var d1 = NewLabel();
                Jmp(d1);
                Mark(loGe);
                Const(dst, 1);
                Mark(d1);
                var done = NewLabel();
                Jmp(done);
                Mark(hiGt);
                Const(dst, 1);
                Jmp(done);
                Mark(hiLt);
                Const(dst, 0);
                Mark(done);
            }

            /// <summary>无符号减法：(rHi:rLo) -= (bHi:bLo)（借位跨 32 位，r 保证 < 2*b）。</summary>
            private void Sub64(LirVirtualRegister rLo, LirVirtualRegister rHi, LirVirtualRegister bLo, LirVirtualRegister bHi)
            {
                var borrow = NewReg(4);
                Cmp(rLo, bLo);
                var noB = NewLabel();
                Jcc(LirCond.AboveOrEqual, noB);
                Const(borrow, 1);
                var bdone = NewLabel();
                Jmp(bdone);
                Mark(noB);
                Const(borrow, 0);
                Mark(bdone);

                var t = NewReg(4);
                Mov(t, rLo);
                Sub(t, t, bLo);
                Mov(rLo, t);

                var th = NewReg(4);
                Mov(th, rHi);
                Sub(th, th, bHi);
                Sub(th, th, borrow);
                Mov(rHi, th);
            }

            /// <summary>q |= (1 << bit)（bit 为 0..63 的寄存器）。</summary>
            private void SetBit64(LirVirtualRegister qLo, LirVirtualRegister qHi, LirVirtualRegister bit)
            {
                var d = NewReg(4);
                Mov(d, bit);
                Shr(d, d, 5);
                var hiPart = NewLabel();
                var done = NewLabel();
                Cmp(d, 0);
                Jcc(LirCond.NotEqual, hiPart);
                var sh = NewReg(4);
                Mov(sh, bit);
                And(sh, sh, C(4, 31));
                var v = NewReg(4);
                Const(v, 1);
                Shl(v, v, sh);
                Or(qLo, qLo, v);
                Jmp(done);
                Mark(hiPart);
                var sh2 = NewReg(4);
                Mov(sh2, bit);
                And(sh2, sh2, C(4, 31));
                var vh = NewReg(4);
                Const(vh, 1);
                Shl(vh, vh, sh2);
                Or(qHi, qHi, vh);
                Mark(done);
            }
        }
    }
}
