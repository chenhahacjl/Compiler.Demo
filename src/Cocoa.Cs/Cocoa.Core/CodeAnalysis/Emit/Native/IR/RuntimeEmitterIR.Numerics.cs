using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>
    /// 平台无关运行时 IR 生成：把原 x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs）
    /// 合并为单一 IR 程序挂接。<br/>
    /// 平台差异收敛为：指针槽宽（8/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）、
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 IrToAssembler.SysCall 负责。
    /// </summary>
    internal static partial class RuntimeEmitterIR
    {
        private sealed partial class RuntimeFunctionEmitter
        {
            /// <summary>pow = 10^n（n ≥ 0），运行时循环计算（n 来自格式解析，非编译期常量）。</summary>
            private void EmitPow10(IrVirtualRegister n, IrVirtualRegister powOut)
            {
                FConst(powOut, _formatOne);
                var ten = NewReg(8);
                FConst(ten, _formatTen);
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, n);
                Jcc(IrCond.GreaterOrEqual, done);
                FMul(powOut, powOut, ten);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>rounded = round(value × 10^n)（round-half-away-from-zero，与旧 FormatInt 语义一致）。x86 下 value 拆 low/high 两参数，槽内拼装。</summary>
            private void EmitDoubleFixed()
            {
                IrVirtualRegister d;
                var n = _isX64 ? _args[1] : _args[2];
                if (_isX64)
                {
                    d = _args[0];
                }
                else
                {
                    d = NewReg(8);
                    StoreSlotField(d, -4, _args[1], 4);
                    StoreSlotField(d, 0, _args[0], 4);
                }
                var pow = NewReg(8);
                EmitPow10(n, pow);
                var s = NewReg(8);
                FMul(s, d, pow);
                var zero = NewReg(8);
                FConst(zero, _formatZero);
                var half = NewReg(8);
                FConst(half, _formatHalf);
                var sR = NewReg(8);
                var isNeg = NewLabel();
                var done = NewLabel();
                FCmp(d, zero);
                Jcc(IrCond.Below, isNeg);
                FAdd(sR, s, half);
                Jmp(done);
                Mark(isNeg);
                FSub(sR, s, half);
                Mark(done);
                var rounded = NewReg(4);
                FCvtSD(rounded, sR);
                StoreRet(rounded);
                EndFunction(_currentFunction!, 4);
            }

            /// <summary>运行时解析格式串（UTF-16）：首字符决定 code（D/X/F/G/E 大小写），后续数字为 n；F 且 n==0 默认 2。</summary>
            private void ParseFormat(IrVirtualRegister fmtPtr, IrVirtualRegister fmtLen, IrVirtualRegister code, IrVirtualRegister n, IrVirtualRegister lowerCase)
            {
                Const(code, 0);
                Const(n, 0);
                Const(lowerCase, 0);

                var len = NewReg(4);
                Mov(len, fmtLen);
                var afterParse = NewLabel();
                Cmp(len, 0);
                Jcc(IrCond.LessOrEqual, afterParse);

                var ch = NewReg(4);
                Load(ch, fmtPtr, 4, 2);

                var skipLower = NewLabel();
                Cmp(ch, (int)'a'); Jcc(IrCond.Less, skipLower);
                Cmp(ch, (int)'z'); Jcc(IrCond.Greater, skipLower);
                Const(lowerCase, 1);
                Mark(skipLower);

                var lD = NewLabel();
                var lX = NewLabel();
                var lF = NewLabel();
                var lG = NewLabel();
                var lE = NewLabel();
                var digits = NewLabel();
                Cmp(ch, (int)'D'); Jcc(IrCond.Equal, lD);
                Cmp(ch, (int)'d'); Jcc(IrCond.Equal, lD);
                Cmp(ch, (int)'X'); Jcc(IrCond.Equal, lX);
                Cmp(ch, (int)'x'); Jcc(IrCond.Equal, lX);
                Cmp(ch, (int)'F'); Jcc(IrCond.Equal, lF);
                Cmp(ch, (int)'f'); Jcc(IrCond.Equal, lF);
                Cmp(ch, (int)'G'); Jcc(IrCond.Equal, lG);
                Cmp(ch, (int)'g'); Jcc(IrCond.Equal, lG);
                Cmp(ch, (int)'E'); Jcc(IrCond.Equal, lE);
                Cmp(ch, (int)'e'); Jcc(IrCond.Equal, lE);
                Jmp(digits);
                Mark(lD); Const(code, 1); Jmp(digits);
                Mark(lX); Const(code, 2); Jmp(digits);
                Mark(lF); Const(code, 3); Jmp(digits);
                Mark(lG); Const(code, 4); Jmp(digits);
                Mark(lE); Const(code, 5); Jmp(digits);

                Mark(digits);
                var hasDigits = NewReg(4);
                Const(hasDigits, 0);
                var p = NewReg(8);
                Lea(p, fmtPtr, 6);
                var i = NewReg(4);
                Const(i, 1);
                var loop = NewLabel();
                var digDone = NewLabel();
                Mark(loop);
                Cmp(i, len);
                Jcc(IrCond.GreaterOrEqual, digDone);
                var c = NewReg(4);
                Load(c, p, 0, 2);
                var brk = NewLabel();
                Cmp(c, (int)'0'); Jcc(IrCond.Less, brk);
                Cmp(c, (int)'9'); Jcc(IrCond.Greater, brk);
                Const(hasDigits, 1);
                var digit = NewReg(4);
                Sub(digit, c, C(4, (int)'0'));
                var n10 = NewReg(4);
                Imul(n10, n, C(4, 10));
                Add(n, n10, digit);
                Lea(p, p, 2);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(brk);
                Mark(digDone);

                // F 且显式精度缺失时默认 2 位小数（F0 的 0 是显式精度，保留）
                var fDefDone = NewLabel();
                Cmp(code, 3); Jcc(IrCond.NotEqual, fDefDone);
                Cmp(hasDigits, 0); Jcc(IrCond.NotEqual, fDefDone);
                Const(n, 2);
                Mark(fDefDone);

                // E 且显式精度缺失时默认 6 位小数（对齐 .NET）
                var eDefDone = NewLabel();
                Cmp(code, 5); Jcc(IrCond.NotEqual, eDefDone);
                Cmp(hasDigits, 0); Jcc(IrCond.NotEqual, eDefDone);
                Const(n, 6);
                Mark(eDefDone);

                // G 且显式精度缺失时默认 15 位有效数字（对齐 .NET）
                var gDefDone = NewLabel();
                Cmp(code, 4); Jcc(IrCond.NotEqual, gDefDone);
                Cmp(hasDigits, 0); Jcc(IrCond.NotEqual, gDefDone);
                Const(n, 15);
                Mark(gDefDone);

                Mark(afterParse);
            }


            // ------------------------------------------------------------------
            // WriteStr(buf:8, len:4)：控制台 → WriteConsoleW，否则 WriteFile
            // ------------------------------------------------------------------

            private void EmitWriteStr()
            {
                var buf = _args[0];
                var length = _args[1];
                var notConsole = NewLabel();
                var done = NewLabel();

                var written = NewReg(4);
                var writtenAddr = NewReg(8);
                LeaSlot(writtenAddr, written);

                var handle = C(8, 0);
                SysCall(handle, "GetStdHandle", 1, C(4, -11));
                var fileType = NewReg(4);
                SysCall(fileType, "GetFileType", 1, handle);
                Cmp(fileType, 2);
                Jcc(IrCond.NotEqual, notConsole);

                SysCall(null, "WriteConsoleW", 5, handle, buf, length, writtenAddr);
                Jmp(done);

                Mark(notConsole);
                var byteLen = NewReg(4);
                Mov(byteLen, length);
                Shl(byteLen, byteLen, 1);
                SysCall(null, "WriteFile", 5, handle, buf, byteLen, writtenAddr);

                Mark(done);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // BuildInt(value:4, buf:8)：紧凑 UTF-16 数字写入 buf，返回字节长度
            // ------------------------------------------------------------------

            private void EmitBuildInt()
            {
                var value = _args[0];
                var buf = _args[1];
                var positive = NewLabel();
                var signDone = NewLabel();
                var digitLoop = NewLabel();
                var copyLoop = NewLabel();
                var copyDone = NewLabel();

                var sign = C(4, 0);
                Cmp(value, 0);
                Jcc(IrCond.GreaterOrEqual, positive);
                Const(sign, 1);
                Neg(value);

                Mark(positive);
                var tail = NewReg(8);
                Lea(tail, buf, 44);
                var ten = C(4, 10);

                Mark(digitLoop);
                var quotient = NewReg(4);
                Mov(quotient, value);
                Udiv(quotient, ten);
                var digit = NewReg(4);
                Imul(digit, quotient, ten);
                Sub(value, value, digit);
                var digitChar = NewReg(4);
                AddI(digitChar, value, '0');
                var nextTail = NewReg(8);
                Lea(nextTail, tail, -2);
                Store(nextTail, 0, digitChar, 2);
                Mov(value, quotient);
                Mov(tail, nextTail);
                Cmp(value, 0);
                Jcc(IrCond.NotEqual, digitLoop);

                Cmp(sign, 0);
                var copyTail = NewReg(8);
                var copyReady = NewLabel();
                Jcc(IrCond.Equal, signDone);
                var minus = C(4, '-');
                var signTail = NewReg(8);
                Lea(signTail, tail, -2);
                Store(signTail, 0, minus, 2);
                Mov(copyTail, signTail);
                Jmp(copyReady);
                Mark(signDone);
                Mov(copyTail, tail);
                Mark(copyReady);
                tail = copyTail;

                // lenBytes = 44 + buf - tail
                var len = NewReg(4);
                var endAddr = NewReg(8);
                Mov(endAddr, buf);
                AddI(endAddr, endAddr, 44);
                Sub(endAddr, endAddr, tail);
                Mov(len, endAddr);

                // 复制 (len+2)>>2 个 dword：紧凑区 → buf
                var count = NewReg(4);
                Mov(count, len);
                AddI(count, count, 2);
                Shr(count, count, 2);

                Mark(copyLoop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, copyDone);
                var word = NewReg(4);
                Load(word, tail, 0, 4);
                Store(buf, 0, word, 4);
                var nextTail2 = NewReg(8);
                Lea(nextTail2, tail, 4);
                Mov(tail, nextTail2);
                var nextBuf = NewReg(8);
                Lea(nextBuf, buf, 4);
                Mov(buf, nextBuf);
                AddI(count, count, -1);
                Jmp(copyLoop);

                Mark(copyDone);
                StoreRet(len);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Alloc(size:4) → 堆指针（8/x86:4），失败 0
            // ------------------------------------------------------------------

            private void EmitAlloc()
            {
                var size = _args[0];
                var have = NewLabel();
                var newSizeReady = NewLabel();
                var fail = NewLabel();
                var heart = NewLabel();
                var done = NewLabel();

                var heap = NewReg(8);
                LeaData(heap, _heapBase);
                var ptrSize = _isX64 ? 8 : 4;

                // 首次使用：VirtualAlloc(0, 1MB, MEM_RESERVE|COMMIT, RW)
                var baseAddr = NewReg(8);
                Load(baseAddr, heap, 0, ptrSize);
                Cmp(baseAddr, 0);
                Jcc(IrCond.NotEqual, have);
                var first = NewReg(8);
                SysCall(first, "VirtualAlloc", 4, C(4, 0), C(4, 0x100000), C(4, 0x3000), C(4, 0x04));
                Cmp(first, 0);
                Jcc(IrCond.Equal, fail);
                Store(heap, 0, first, ptrSize);
                var firstEnd = NewReg(8);
                Lea(firstEnd, first, 0x100000);
                Store(heap, _heapEndOffset, firstEnd, ptrSize);
                Store(heap, _heapPtrOffset, first, ptrSize);

                Mark(have);
                // HeapPtr + size <= HeapEnd → 直接分配；否则新块
                var ptr = NewReg(8);
                Load(ptr, heap, _heapPtrOffset, ptrSize);
                var newPtr = NewReg(8);
                Add(newPtr, ptr, size);
                var end = NewReg(8);
                Load(end, heap, _heapEndOffset, ptrSize);
                Cmp(newPtr, end);
                Jcc(IrCond.Below, heart);

                var newSize = NewReg(4);
                Mov(newSize, size);
                Cmp(newSize, 0x100000);
                Jcc(IrCond.AboveOrEqual, newSizeReady);
                Const(newSize, 0x100000);

                Mark(newSizeReady);
                var second = NewReg(8);
                SysCall(second, "VirtualAlloc", 4, C(4, 0), newSize, C(4, 0x3000), C(4, 0x04));
                Cmp(second, 0);
                Jcc(IrCond.Equal, fail);
                Store(heap, 0, second, ptrSize);
                Store(heap, _heapPtrOffset, second, ptrSize);
                var secondEnd = NewReg(8);
                Add(secondEnd, second, newSize);
                Store(heap, _heapEndOffset, secondEnd, ptrSize);

                Mark(heart);
                var current = NewReg(8);
                Load(current, heap, _heapPtrOffset, ptrSize);
                var next = NewReg(8);
                Add(next, current, size);
                Store(heap, _heapPtrOffset, next, ptrSize);
                StoreRet(current);
                Jmp(done);

                Mark(fail);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // CopyChars(dst:8, src:8, count:4 words)
            // ------------------------------------------------------------------

            private void EmitCopyChars()
            {
                var dst = _args[0];
                var src = _args[1];
                var count = _args[2];
                var loop = NewLabel();
                var done = NewLabel();

                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, done);
                var word = NewReg(4);
                Load(word, src, 0, 4);
                Store(dst, 0, word, 4);
                var nextSrc = NewReg(8);
                Lea(nextSrc, src, 4);
                Mov(src, nextSrc);
                var nextDst = NewReg(8);
                Lea(nextDst, dst, 4);
                Mov(dst, nextDst);
                AddI(count, count, -1);
                Jmp(loop);

                Mark(done);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // PrintString(s:8)
            // ------------------------------------------------------------------

            private void EmitPrintString()
            {
                var s = _args[0];
                var nullStr = NewLabel();

                // 6e-M19 M5-a：null 打印为空（对齐 Console.WriteLine(null)）
                Cmp(s, 0);
                Jcc(IrCond.Equal, nullStr);
                var len = NewReg(4);
                Load(len, s, 0, 4);
                var chars = NewReg(8);
                Lea(chars, s, 4);
                CallRuntime(null, "WriteStr", chars, len);
                Mark(nullStr);
                EmitWriteNewLine();
                EndFunction(_currentFunction!, 0);
            }

            /// <summary>语言层 write 语义：文本不换行（Console.Write 对齐，6e-M18+ 原语 Write）。</summary>
            private void EmitWriteString()
            {
                var s = _args[0];
                var writeDone = NewLabel();

                // 6e-M19 M5-a：null 打印为空
                Cmp(s, 0);
                Jcc(IrCond.Equal, writeDone);
                var len = NewReg(4);
                Load(len, s, 0, 4);
                var chars = NewReg(8);
                Lea(chars, s, 4);
                CallRuntime(null, "WriteStr", chars, len);
                Mark(writeDone);
                EndFunction(_currentFunction!, 0);
            }

            /// <summary>语言层 print 语义：文本 + CRLF（与解释器/IL 后端 Console.WriteLine 一致）。</summary>
            private void EmitWriteNewLine()
            {
                var newLine = NewReg(8);
                LeaData(newLine, _newLine);
                var newLineChars = NewReg(8);
                Lea(newLineChars, newLine, 4);
                CallRuntime(null, "WriteStr", newLineChars, C(4, 2));
            }

            // ------------------------------------------------------------------
            // PrintInt(value:4)
            // ------------------------------------------------------------------

            private void EmitPrintInt()
            {
                var value = _args[0];
                var buf = NewReg(8);
                var scratch = NewReg(8);
                LeaSlot(buf, scratch);
                var len = NewReg(4);
                CallRuntime(len, "BuildInt", value, buf);
                var chars = NewReg(4);
                Mov(chars, len);
                Shr(chars, chars, 1);
                CallRuntime(null, "WriteStr", buf, chars);
                EmitWriteNewLine();
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // WriteInt(value:4)：BuildInt + WriteStr（不换行）
            // ------------------------------------------------------------------

            private void EmitWriteInt()
            {
                var value = _args[0];
                var buf = NewReg(8);
                var scratch = NewReg(8);
                LeaSlot(buf, scratch);
                var len = NewReg(4);
                CallRuntime(len, "BuildInt", value, buf);
                var chars = NewReg(4);
                Mov(chars, len);
                Shr(chars, chars, 1);
                CallRuntime(null, "WriteStr", buf, chars);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // IntToString(value:4) → 字符串对象（含 [len:4]）
            // ------------------------------------------------------------------

            private void EmitIntToString()
            {
                var value = _args[0];
                var oom = NewLabel();
                var done = NewLabel();

                var scratch = NewReg(8);
                var buf = NewReg(8);
                LeaSlot(buf, scratch);
                var lenBytes = NewReg(4);
                CallRuntime(lenBytes, "BuildInt", value, buf);

                var size = NewReg(4);
                Mov(size, lenBytes);
                AddI(size, size, 2);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

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
                CallRuntime(null, "CopyChars", dst, buf, count);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // DivChain(buf:8, divisor:4) → rem:4
            // 128 位数（8 个 16 位块，LE，buf[0..15]）除以小除数（< 2^16，链式保证
            // rem<<16 不溢出 32 位），商写回 buf，返回余数。
            // ------------------------------------------------------------------

            private void EmitDivChain()
            {
                var buf = _args[0];
                var divisor = _args[1];
                var loop = NewLabel();
                var done = NewLabel();

                var p = NewReg(8);
                Lea(p, buf, 14);
                var count = C(4, 8);
                var rem = C(4, 0);

                Mark(loop);
                var block = NewReg(4);
                Load(block, p, 0, 2);
                var t = NewReg(4);
                Shl(t, rem, 16);
                Or(t, t, block);
                var q = NewReg(4);
                Mov(q, t);
                Udiv(q, divisor);
                var back = NewReg(4);
                Imul(back, q, divisor);
                Sub(rem, t, back);
                Store(p, 0, q, 2);
                var next = NewReg(8);
                Lea(next, p, -2);
                Mov(p, next);
                AddI(count, count, -1);
                Cmp(count, 0);
                Jcc(IrCond.NotEqual, loop);

                Mark(done);
                StoreRet(rem);
                EndFunction(_currentFunction!, 4);
            }

            // BigDiv(buf:8, divisor:4, blocks:4) → rem:4
            // 大整数（blocks 个 16 位块，LE）除以小除数（< 2^16），链式 16 位块商写回，返回余数。
            // ------------------------------------------------------------------

            private void EmitBigDiv()
            {
                var buf = _args[0];
                var divisor = _args[1];
                var blocks = _args[2];
                var loop = NewLabel();
                var done = NewLabel();

                var p = NewReg(8);
                Mov(p, buf);
                var top = NewReg(4);
                Mov(top, blocks);
                Shl(top, top, 1);
                Add(p, p, top);
                AddI(p, p, -2);
                var count = NewReg(4);
                Mov(count, blocks);
                var rem = NewReg(4);
                Const(rem, 0);

                Mark(loop);
                var block = NewReg(4);
                Load(block, p, 0, 2);
                var t = NewReg(4);
                Shl(t, rem, 16);
                Or(t, t, block);
                var q = NewReg(4);
                Mov(q, t);
                Udiv(q, divisor);
                var back = NewReg(4);
                Imul(back, q, divisor);
                Sub(rem, t, back);
                Store(p, 0, q, 2);
                var next = NewReg(8);
                Lea(next, p, -2);
                Mov(p, next);
                AddI(count, count, -1);
                Cmp(count, 0);
                Jcc(IrCond.NotEqual, loop);

                Mark(done);
                StoreRet(rem);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // 大整数（80×u16 = 1280 位，LE）原语 —— 全 double 范围定点格式化基础。
            // 布局：u32 limb i（LE）位于 buf+4i，等价 16 位块 2i/2i+1（乘/除按 16 位块，移位按 32 位 limb）。
            // ------------------------------------------------------------------

            private const int BigBlocks = 80;
            private const int BigLimbs = 40;

            private void AndI(IrVirtualRegister dst, IrVirtualRegister a, int imm) => Add(IrOpCode.And, dst, IrOperand.Reg(a), IrOperand.Constant(imm));

            private void ShlReg(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister count) => Add(IrOpCode.Shl, dst, IrOperand.Reg(a), IrOperand.Reg(count));

            private void ShrReg(IrVirtualRegister dst, IrVirtualRegister a, IrVirtualRegister count) => Add(IrOpCode.Shr, dst, IrOperand.Reg(a), IrOperand.Reg(count));

            private void EmitBigAddr(IrVirtualRegister buf, IrVirtualRegister idx, int scale, IrVirtualRegister addr)
            {
                Mov(addr, buf);
                var off = NewReg(4);
                Mov(off, idx);
                if (scale == 4)
                {
                    Shl(off, off, 2);
                }
                else
                {
                    Shl(off, off, 1);
                }
                Add(addr, addr, off);
            }

            private void EmitBigSetZero(IrVirtualRegister buf)
            {
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 4, addr);
                Store(addr, 0, C(4, 0), 4);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            private void EmitBigSetFromMantissa(IrVirtualRegister buf, IrVirtualRegister m0, IrVirtualRegister m1)
            {
                Store(buf, 0, m0, 4);
                Store(buf, 4, m1, 4);
                var i = NewReg(4);
                Const(i, 2);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 4, addr);
                Store(addr, 0, C(4, 0), 4);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>整值 ×k（k ≤ 0xFFFF 编译期常量）原地，16 位块链式进位。</summary>
            private void EmitBigMulSmall(IrVirtualRegister buf, int k)
            {
                var carry = NewReg(4);
                Const(carry, 0);
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigBlocks);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 2, addr);
                var blk = NewReg(4);
                Load(blk, addr, 0, 2);
                var t = NewReg(4);
                Imul(t, blk, C(4, k));
                Add(t, t, carry);
                var nb = NewReg(4);
                AndI(nb, t, 0xFFFF);
                Store(addr, 0, nb, 2);
                Mov(carry, t);
                Shr(carry, carry, 16);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>读 buf[idx]（idx 越界 → 0）。</summary>
            private void EmitBigGetLimb(IrVirtualRegister buf, IrVirtualRegister idx, IrVirtualRegister val)
            {
                Const(val, 0);
                var done = NewLabel();
                Cmp(idx, 0);
                Jcc(IrCond.Less, done);
                Cmp(idx, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, idx, 4, addr);
                Load(val, addr, 0, 4);
                Mark(done);
            }

            private void EmitBigSetLimb(IrVirtualRegister buf, IrVirtualRegister idx, IrVirtualRegister val)
            {
                var addr = NewReg(8);
                EmitBigAddr(buf, idx, 4, addr);
                Store(addr, 0, val, 4);
            }

            /// <summary>右移 bits（0..1074）截断（无舍入）。</summary>
            private void EmitBigShrTrunc(IrVirtualRegister buf, IrVirtualRegister bits)
            {
                var full = NewReg(4);
                Mov(full, bits);
                Shr(full, full, 5);
                var rem = NewReg(4);
                Mov(rem, bits);
                AndI(rem, rem, 31);
                var doneAll = NewLabel();
                var remZero = NewLabel();
                var zeroTop = NewLabel();
                var zeroTop2 = NewLabel();
                var shiftDone = NewLabel();

                var i = NewReg(4);
                Const(i, 0);
                Cmp(rem, 0);
                Jcc(IrCond.Equal, remZero);

                // rem != 0：limb[i] = (limb[i+full]>>rem) | (limb[i+full+1]<<(32-rem))
                var maxLo = NewReg(4);
                Mov(maxLo, full);
                Neg(maxLo);
                AddI(maxLo, maxLo, BigLimbs - 1);
                var sRem = NewReg(4);
                Mov(sRem, rem);
                Neg(sRem);
                AddI(sRem, sRem, 32);
                var loop1 = NewLabel();
                Mark(loop1);
                Cmp(i, maxLo);
                Jcc(IrCond.GreaterOrEqual, zeroTop);
                var idxA = NewReg(4);
                Add(idxA, i, full);
                var a = NewReg(4);
                EmitBigGetLimb(buf, idxA, a);
                var tA = NewReg(4);
                ShrReg(tA, a, rem);
                var idxB = NewReg(4);
                AddI(idxB, idxA, 1);
                var b = NewReg(4);
                EmitBigGetLimb(buf, idxB, b);
                var tB = NewReg(4);
                ShlReg(tB, b, sRem);
                Or(tA, tA, tB);
                EmitBigSetLimb(buf, i, tA);
                AddI(i, i, 1);
                Jmp(loop1);

                Mark(zeroTop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, shiftDone);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, 1);
                Jmp(zeroTop);
                Mark(shiftDone);
                Jmp(doneAll);

                Mark(remZero);
                var maxLo2 = NewReg(4);
                Mov(maxLo2, full);
                Neg(maxLo2);
                AddI(maxLo2, maxLo2, BigLimbs - 1);
                var loop2 = NewLabel();
                Mark(loop2);
                Cmp(i, maxLo2);
                Jcc(IrCond.GreaterOrEqual, zeroTop2);
                var idx2 = NewReg(4);
                Add(idx2, i, full);
                var v2 = NewReg(4);
                EmitBigGetLimb(buf, idx2, v2);
                EmitBigSetLimb(buf, i, v2);
                AddI(i, i, 1);
                Jmp(loop2);
                Mark(zeroTop2);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, doneAll);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, 1);
                Jmp(zeroTop2);
                Mark(doneAll);
            }

            /// <summary>左移 bits（0..1074），高位丢弃。</summary>
            private void EmitBigShl(IrVirtualRegister buf, IrVirtualRegister bits)
            {
                var full = NewReg(4);
                Mov(full, bits);
                Shr(full, full, 5);
                var rem = NewReg(4);
                Mov(rem, bits);
                AndI(rem, rem, 31);
                var doneAll = NewLabel();
                var remZero = NewLabel();
                var zeroLow = NewLabel();
                var zeroLow2 = NewLabel();
                var shiftDone = NewLabel();

                var i = NewReg(4);
                Const(i, BigLimbs - 1);
                Cmp(rem, 0);
                Jcc(IrCond.Equal, remZero);

                // rem != 0：limb[i] = (limb[i-full]<<rem) | (limb[i-full-1]>>(32-rem))
                var sRem = NewReg(4);
                Mov(sRem, rem);
                Neg(sRem);
                AddI(sRem, sRem, 32);
                var loop1 = NewLabel();
                Mark(loop1);
                Cmp(i, full);
                Jcc(IrCond.Less, zeroLow);
                var idxA = NewReg(4);
                Sub(idxA, i, full);
                var a = NewReg(4);
                EmitBigGetLimb(buf, idxA, a);
                var tA = NewReg(4);
                ShlReg(tA, a, rem);
                var idxB = NewReg(4);
                AddI(idxB, idxA, -1);
                var b = NewReg(4);
                EmitBigGetLimb(buf, idxB, b);
                var tB = NewReg(4);
                ShrReg(tB, b, sRem);
                Or(tA, tA, tB);
                EmitBigSetLimb(buf, i, tA);
                AddI(i, i, -1);
                Jmp(loop1);

                Mark(zeroLow);
                Cmp(i, 0);
                Jcc(IrCond.Less, shiftDone);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, -1);
                Jmp(zeroLow);
                Mark(shiftDone);
                Jmp(doneAll);

                Mark(remZero);
                var loop2 = NewLabel();
                Mark(loop2);
                Cmp(i, full);
                Jcc(IrCond.Less, zeroLow2);
                var idx2 = NewReg(4);
                Sub(idx2, i, full);
                var v2 = NewReg(4);
                EmitBigGetLimb(buf, idx2, v2);
                EmitBigSetLimb(buf, i, v2);
                AddI(i, i, -1);
                Jmp(loop2);
                Mark(zeroLow2);
                Cmp(i, 0);
                Jcc(IrCond.Less, doneAll);
                EmitBigSetLimb(buf, i, C(4, 0));
                AddI(i, i, -1);
                Jmp(zeroLow2);
                Mark(doneAll);
            }

            /// <summary>在 bitpos 位置 +2^bitpos（进位链，用于 away-from-zero 舍入）。</summary>
            private void EmitBigAddBitAt(IrVirtualRegister buf, IrVirtualRegister bitpos)
            {
                var limbIdx = NewReg(4);
                Mov(limbIdx, bitpos);
                Shr(limbIdx, limbIdx, 5);
                var r = NewReg(4);
                Mov(r, bitpos);
                AndI(r, r, 31);
                var bit = NewReg(4);
                Const(bit, 1);
                var bitLoop = NewLabel();
                var bitDone = NewLabel();
                Mark(bitLoop);
                Cmp(r, 0);
                Jcc(IrCond.Equal, bitDone);
                Shl(bit, bit, 1);
                AddI(r, r, -1);
                Jmp(bitLoop);
                Mark(bitDone);

                var cur = NewReg(4);
                EmitBigGetLimb(buf, limbIdx, cur);
                Add(cur, cur, bit);
                var carry = NewReg(4);
                Setcc(carry, IrCond.Below);
                EmitBigSetLimb(buf, limbIdx, cur);
                var j = NewReg(4);
                Mov(j, limbIdx);
                var prop = NewLabel();
                var propDone = NewLabel();
                Mark(prop);
                Cmp(carry, 0);
                Jcc(IrCond.Equal, propDone);
                AddI(j, j, 1);
                Cmp(j, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, propDone);
                var cur2 = NewReg(4);
                EmitBigGetLimb(buf, j, cur2);
                Add(cur2, cur2, carry);
                Setcc(carry, IrCond.Below);
                EmitBigSetLimb(buf, j, cur2);
                Jmp(prop);
                Mark(propDone);
            }

            /// <summary>右移 bits 且 round-half-away-from-zero（先加 2^(bits-1) 再截断）。</summary>
            private void EmitBigShrRoundAway(IrVirtualRegister buf, IrVirtualRegister bits)
            {
                var done = NewLabel();
                Cmp(bits, 0);
                Jcc(IrCond.Equal, done);
                var bp = NewReg(4);
                Mov(bp, bits);
                AddI(bp, bp, -1);
                EmitBigAddBitAt(buf, bp);
                EmitBigShrTrunc(buf, bits);
                Mark(done);
            }

            private void EmitBigIsZero(IrVirtualRegister buf, IrVirtualRegister z)
            {
                var acc = NewReg(4);
                Const(acc, 0);
                var i = NewReg(4);
                Const(i, 0);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(i, BigLimbs);
                Jcc(IrCond.GreaterOrEqual, done);
                var addr = NewReg(8);
                EmitBigAddr(buf, i, 4, addr);
                var w = NewReg(4);
                Load(w, addr, 0, 4);
                Or(acc, acc, w);
                AddI(i, i, 1);
                Jmp(loop);
                Mark(done);
                Mov(z, acc);
            }

            /// <summary>把 buf 的十进制数字全部提取（LSB 先出，反向写 tail），返回数字数与数字起始 tail。</summary>
            private void EmitBigDigitsToTail(IrVirtualRegister buf, IrVirtualRegister tailEnd, IrVirtualRegister digitCount, IrVirtualRegister newTail)
            {
                var count = NewReg(4);
                Const(count, 0);
                var cur = NewReg(8);
                Mov(cur, tailEnd);
                var loop = NewLabel();
                var isZero = NewReg(4);
                var rem = NewReg(4);
                Mark(loop);
                CallRuntime(rem, "BigDiv", buf, C(4, 10), C(4, BigBlocks));
                var ch = NewReg(4);
                AddI(ch, rem, '0');
                var prev = NewReg(8);
                Lea(prev, cur, -2);
                Store(prev, 0, ch, 2);
                Mov(cur, prev);
                AddI(count, count, 1);
                EmitBigIsZero(buf, isZero);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, loop);
                Mov(digitCount, count);
                Mov(newTail, cur);
            }

            // ------------------------------------------------------------------
            // 全 double 范围格式化核心：1280 位大整数定点 v×10^S，S=6（普通）/n（F）。
            // ------------------------------------------------------------------

            /// <summary>把 double 位模式拆为 sign/exp/m0/m1（m1 含隐式位）/e/isSpecial/isZero/isMantZero。</summary>
            private void EmitSplitDouble(IrVirtualRegister b0, IrVirtualRegister b1, IrVirtualRegister sign, IrVirtualRegister exp,
                IrVirtualRegister m0, IrVirtualRegister m1, IrVirtualRegister e, IrVirtualRegister isSpecial, IrVirtualRegister isZero, IrVirtualRegister isMantZero)
            {
                Mov(sign, b1);
                Shr(sign, sign, 31);
                Mov(exp, b1);
                AndI(exp, exp, 0x7FF00000);
                Shr(exp, exp, 20);
                Mov(m1, b1);
                AndI(m1, m1, 0xFFFFF);
                Mov(m0, b0);

                Cmp(exp, 0x7FF);
                Setcc(isSpecial, IrCond.Equal);
                var m1Zero = NewReg(4);
                var m0Zero = NewReg(4);
                Cmp(m1, 0);
                Setcc(m1Zero, IrCond.Equal);
                Cmp(m0, 0);
                Setcc(m0Zero, IrCond.Equal);
                And(isMantZero, m1Zero, m0Zero);
                var expZero = NewReg(4);
                Cmp(exp, 0);
                Setcc(expZero, IrCond.Equal);
                And(isZero, expZero, isMantZero);

                var hasHidden = NewReg(4);
                Cmp(exp, 0);
                Setcc(hasHidden, IrCond.NotEqual);
                var hidden = NewReg(4);
                Mov(hidden, hasHidden);
                Neg(hidden);
                AndI(hidden, hidden, 0x100000);
                Or(m1, m1, hidden);

                var normalExpLabel = NewLabel();
                var expReady = NewLabel();
                Cmp(exp, 0);
                Jcc(IrCond.NotEqual, normalExpLabel);
                Const(e, -1074);
                Jmp(expReady);
                Mark(normalExpLabel);
                Mov(e, exp);
                AddI(e, e, -1075);
                Mark(expReady);
            }

            private void EmitStoreCharAt(IrVirtualRegister fmt, IrVirtualRegister pos, char ch)
            {
                var addr = NewReg(8);
                Mov(addr, fmt);
                Add(addr, addr, pos);
                Store(addr, 0, C(4, (int)ch), 2);
                AddI(pos, pos, 2);
            }

            private void EmitWriteRepeatedCharAt(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister count, char ch)
            {
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.LessOrEqual, done);
                EmitStoreCharAt(fmt, pos, ch);
                AddI(count, count, -1);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>从 src 拷 charCount 个 UTF-16 字符到 fmt+pos 并推进 pos。</summary>
            private void EmitCopyCharsAt(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister src, IrVirtualRegister charCount)
            {
                var dst = NewReg(8);
                Mov(dst, fmt);
                Add(dst, dst, pos);
                var dwords = NewReg(4);
                Mov(dwords, charCount);
                AddI(dwords, dwords, 1);
                Shr(dwords, dwords, 1);
                CallRuntime(null, "CopyChars", dst, src, dwords);
                var adv = NewReg(4);
                Mov(adv, charCount);
                Shl(adv, adv, 1);
                Add(pos, pos, adv);
            }

            /// <summary>固定格式组装：[sign] intpart ['.' frac] → _formatBuffer → 字符串对象。
            /// n 位小数；trim=true 剪尾零并去尾随小数点（普通打印）。digits 在 tail 中按正向排列。</summary>
            private void EmitAssembleFixed(IrVirtualRegister objOut, IrVirtualRegister tail, IrVirtualRegister digitCount, IrVirtualRegister nReg, IrVirtualRegister sign, bool trim)
            {
                var fmt = NewReg(8);
                LeaData(fmt, _formatBuffer);
                var pos = NewReg(4);
                Const(pos, 0);

                var negLabel = NewLabel();
                var signDone = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negLabel);
                Jmp(signDone);
                Mark(negLabel);
                EmitStoreCharAt(fmt, pos, '-');
                Mark(signDone);

                var intLen = NewReg(4);
                var intEnd = NewReg(4);
                var dGTn = NewLabel();
                var intDone = NewLabel();
                Cmp(digitCount, nReg);
                Jcc(IrCond.Greater, dGTn);
                EmitStoreCharAt(fmt, pos, '0');
                Const(intLen, 1);
                Jmp(intDone);
                Mark(dGTn);
                var intChars = NewReg(4);
                Sub(intChars, digitCount, nReg);
                Mov(intLen, intChars);
                EmitCopyCharsAt(fmt, pos, tail, intChars);
                Mark(intDone);
                Mov(intEnd, pos);

                var noFrac = NewLabel();
                var fracDone = NewLabel();
                Cmp(nReg, 0);
                Jcc(IrCond.Equal, noFrac);
                EmitStoreCharAt(fmt, pos, '.');
                var fracCopy = NewLabel();
                var fracWrote = NewLabel();
                Cmp(digitCount, nReg);
                Jcc(IrCond.Greater, fracCopy);
                var padZeros = NewReg(4);
                Sub(padZeros, nReg, digitCount);
                EmitWriteRepeatedCharAt(fmt, pos, padZeros, '0');
                EmitCopyCharsAt(fmt, pos, tail, digitCount);
                Jmp(fracWrote);
                Mark(fracCopy);
                var tailOff = NewReg(8);
                Mov(tailOff, tail);
                var offBytes = NewReg(4);
                Mov(offBytes, intLen);
                Shl(offBytes, offBytes, 1);
                Add(tailOff, tailOff, offBytes);
                EmitCopyCharsAt(fmt, pos, tailOff, nReg);
                Mark(fracWrote);

                if (trim)
                {
                    var trimLoop = NewLabel();
                    var trimDone = NewLabel();
                    var ch = NewReg(4);
                    var addr = NewReg(8);
                    Mark(trimLoop);
                    var minPos = NewReg(4);
                    Mov(minPos, intEnd);
                    AddI(minPos, minPos, 2);
                    Cmp(pos, minPos);
                    Jcc(IrCond.LessOrEqual, trimDone);
                    Mov(addr, fmt);
                    Add(addr, addr, pos);
                    AddI(addr, addr, -2);
                    Load(ch, addr, 0, 2);
                    Cmp(ch, '0');
                    Jcc(IrCond.NotEqual, trimDone);
                    AddI(pos, pos, -2);
                    Jmp(trimLoop);
                    Mark(trimDone);
                    var onlyDot = NewReg(4);
                    Mov(onlyDot, intEnd);
                    AddI(onlyDot, onlyDot, 2);
                    Cmp(pos, onlyDot);
                    Jcc(IrCond.NotEqual, fracDone);
                    Mov(pos, intEnd);
                }
                Jmp(fracDone);
                Mark(noFrac);
                Mark(fracDone);

                var lenBytes = NewReg(4);
                Mov(lenBytes, pos);
                CallRuntime(objOut, "AllocStringFromBuf", fmt, lenBytes);
            }

            // ------------------------------------------------------------------
            // DoubleToString(bits) → 字符串对象（全范围：6 位小数剪尾零，v×10^6 大整数定点）
            // ------------------------------------------------------------------

            private void EmitDoubleToString()
            {
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var fixedDone = NewLabel();

                var b0 = NewReg(4);
                var b1 = NewReg(4);
                if (_isX64)
                {
                    var t = NewReg(8);
                    Mov(t, _args[0]);
                    Shr(t, t, 16);
                    Shr(t, t, 16);
                    Mov(b1, t);
                    Mov(b0, _args[0]);
                }
                else
                {
                    Mov(b0, _args[0]);
                    Mov(b1, _args[1]);
                }

                var sign = NewReg(4);
                var exp = NewReg(4);
                var m0 = NewReg(4);
                var m1 = NewReg(4);
                var e = NewReg(4);
                var isSpecial = NewReg(4);
                var isZero = NewReg(4);
                var isMantZero = NewReg(4);
                EmitSplitDouble(b0, b1, sign, exp, m0, m1, e, isSpecial, isZero, isMantZero);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                var buf = NewReg(8);
                LeaData(buf, _fmtBigBuf);
                EmitBigSetZero(buf);
                EmitBigSetFromMantissa(buf, m0, m1);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);
                EmitBigMulSmall(buf, 10);

                var negShift = NewLabel();
                var shiftDone = NewLabel();
                var negE = NewReg(4);
                Cmp(e, 0);
                Jcc(IrCond.Less, negShift);
                EmitBigShl(buf, e);
                Jmp(shiftDone);
                Mark(negShift);
                Mov(negE, e);
                Neg(negE);
                EmitBigShrRoundAway(buf, negE);
                Mark(shiftDone);

                var tail = NewReg(8);
                Mov(tail, buf);
                AddI(tail, tail, 1600);
                var d = NewReg(4);
                var tail2 = NewReg(8);
                EmitBigDigitsToTail(buf, tail, d, tail2);

                var obj = NewReg(8);
                EmitAssembleFixed(obj, tail2, d, C(4, 6), sign, trim: true);
                StoreRet(obj);
                Jmp(fixedDone);

                Mark(zeroLabel);
                EmitReturnFixedString(sign, _zeroString, _negZeroString);
                Jmp(fixedDone);
                Mark(specialLabel);
                Cmp(isMantZero, 0);
                Jcc(IrCond.Equal, nanLabel);
                EmitReturnFixedString(sign, _infinityString, _negInfinityString);
                Jmp(fixedDone);
                Mark(nanLabel);
                EmitReturnFixedString(sign, _nanString, _nanString);

                Mark(fixedDone);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // FormatFixed(value, n) → 字符串对象（F 格式：n 位小数零填充，away-from-zero，全范围）
            // ------------------------------------------------------------------

            private void EmitFormatFixed()
            {
                var n = _isX64 ? _args[1] : _args[2];
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var done = NewLabel();

                var b0 = NewReg(4);
                var b1 = NewReg(4);
                if (_isX64)
                {
                    var t = NewReg(8);
                    Mov(t, _args[0]);
                    Shr(t, t, 16);
                    Shr(t, t, 16);
                    Mov(b1, t);
                    Mov(b0, _args[0]);
                }
                else
                {
                    Mov(b0, _args[0]);
                    Mov(b1, _args[1]);
                }

                var sign = NewReg(4);
                var exp = NewReg(4);
                var m0 = NewReg(4);
                var m1 = NewReg(4);
                var e = NewReg(4);
                var isSpecial = NewReg(4);
                var isZero = NewReg(4);
                var isMantZero = NewReg(4);
                EmitSplitDouble(b0, b1, sign, exp, m0, m1, e, isSpecial, isZero, isMantZero);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                var buf = NewReg(8);
                LeaData(buf, _fmtBigBuf);
                EmitBigSetZero(buf);
                EmitBigSetFromMantissa(buf, m0, m1);
                var i = NewReg(4);
                Const(i, 0);
                var mulLoop = NewLabel();
                var mulDone = NewLabel();
                Mark(mulLoop);
                Cmp(i, n);
                Jcc(IrCond.GreaterOrEqual, mulDone);
                EmitBigMulSmall(buf, 10);
                AddI(i, i, 1);
                Jmp(mulLoop);
                Mark(mulDone);

                var negShift = NewLabel();
                var shiftDone = NewLabel();
                var negE = NewReg(4);
                Cmp(e, 0);
                Jcc(IrCond.Less, negShift);
                EmitBigShl(buf, e);
                Jmp(shiftDone);
                Mark(negShift);
                Mov(negE, e);
                Neg(negE);
                EmitBigShrRoundAway(buf, negE);
                Mark(shiftDone);

                var tail = NewReg(8);
                Mov(tail, buf);
                AddI(tail, tail, 1600);
                var d = NewReg(4);
                var tail2 = NewReg(8);
                EmitBigDigitsToTail(buf, tail, d, tail2);

                var obj = NewReg(8);
                EmitAssembleFixed(obj, tail2, d, n, sign, trim: false);
                StoreRet(obj);
                Jmp(done);

                Mark(zeroLabel);
                var zbuf = NewReg(8);
                LeaData(zbuf, _fmtBigBuf);
                EmitBigSetZero(zbuf);
                EmitBigSetFromMantissa(zbuf, C(4, 0), C(4, 0));
                var tailZ = NewReg(8);
                Mov(tailZ, zbuf);
                AddI(tailZ, tailZ, 1600);
                var dZ = NewReg(4);
                var tailZ2 = NewReg(8);
                EmitBigDigitsToTail(zbuf, tailZ, dZ, tailZ2);
                var objZ = NewReg(8);
                EmitAssembleFixed(objZ, tailZ2, dZ, n, C(4, 0), trim: false);
                StoreRet(objZ);
                Jmp(done);

                Mark(specialLabel);
                Cmp(isMantZero, 0);
                Jcc(IrCond.Equal, nanLabel);
                EmitReturnFixedString(sign, _infinityString, _negInfinityString);
                Jmp(done);
                Mark(nanLabel);
                EmitReturnFixedString(sign, _nanString, _nanString);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // E / G 格式（FormatSci(value, n, mode)）：half-to-even 舍入，3 位补零指数（E），
            // G 剪尾零并定点/科学切换。mode 0=E（n 小数位）1=G（n 有效数字）。
            // ------------------------------------------------------------------

            private void EmitLoadDigit(IrVirtualRegister tail, IrVirtualRegister idx, IrVirtualRegister ch)
            {
                var addr = NewReg(8);
                Mov(addr, tail);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                Add(addr, addr, off);
                Load(ch, addr, 0, 2);
            }

            private void EmitStoreDigit(IrVirtualRegister tail, IrVirtualRegister idx, IrVirtualRegister ch)
            {
                var addr = NewReg(8);
                Mov(addr, tail);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                Add(addr, addr, off);
                Store(addr, 0, ch, 2);
            }

            /// <summary>把 tail 中前 D 位数字按 round-half-away-from-zero 舍入写回 tail[0..D)；进位时置 carryOut 并把 tail 写为 "1" + 零。</summary>
            private void EmitRoundMantissa(IrVirtualRegister tail, IrVirtualRegister d, IrVirtualRegister D, IrVirtualRegister carryOut)
            {
                var doUp = NewLabel();
                var doDown = NewLabel();
                var roundDone = NewLabel();
                var dD = NewReg(4);
                EmitLoadDigit(tail, D, dD);
                Cmp(dD, '5');
                Jcc(IrCond.GreaterOrEqual, doUp);
                Jmp(doDown);
                Mark(doDown);
                Const(carryOut, 0);
                Jmp(roundDone);
                Mark(doUp);
                var i = NewReg(4);
                Mov(i, D);
                AddI(i, i, -1);
                var incCarry = NewReg(4);
                Const(incCarry, 1);
                var incLoop = NewLabel();
                var incBreak = NewLabel();
                var noOver = NewLabel();
                var writeD = NewLabel();
                Mark(incLoop);
                Cmp(incCarry, 0);
                Jcc(IrCond.Equal, incBreak);
                Cmp(i, 0);
                Jcc(IrCond.Less, incBreak);
                var dch = NewReg(4);
                EmitLoadDigit(tail, i, dch);
                AddI(dch, dch, 1);
                Cmp(dch, '9');
                Jcc(IrCond.LessOrEqual, noOver);
                Const(dch, '0');
                Jmp(writeD);
                Mark(noOver);
                Const(incCarry, 0);
                Mark(writeD);
                EmitStoreDigit(tail, i, dch);
                AddI(i, i, -1);
                Jmp(incLoop);
                Mark(incBreak);
                Mov(carryOut, incCarry);
                Cmp(carryOut, 0);
                Jcc(IrCond.Equal, roundDone);
                EmitStoreDigit(tail, C(4, 0), C(4, '1'));
                var z = NewReg(4);
                Mov(z, D);
                AddI(z, z, -1);
                var zloop = NewLabel();
                var zdone = NewLabel();
                Mark(zloop);
                Cmp(z, 1);
                Jcc(IrCond.Less, zdone);
                EmitStoreDigit(tail, z, C(4, '0'));
                AddI(z, z, -1);
                Jmp(zloop);
                Mark(zdone);
                Mark(roundDone);
            }

            /// <summary>剪 _formatBuffer 末尾 '0'（停在 intEnd+2 即小数点前）并去除尾随小数点。</summary>
            private void EmitTrimTrailingZeros(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister intEnd)
            {
                var trimLoop = NewLabel();
                var trimDone = NewLabel();
                var afterTrim = NewLabel();
                var ch = NewReg(4);
                var addr = NewReg(8);
                Mark(trimLoop);
                var minPos = NewReg(4);
                Mov(minPos, intEnd);
                AddI(minPos, minPos, 2);
                Cmp(pos, minPos);
                Jcc(IrCond.LessOrEqual, trimDone);
                Mov(addr, fmt);
                Add(addr, addr, pos);
                AddI(addr, addr, -2);
                Load(ch, addr, 0, 2);
                Cmp(ch, '0');
                Jcc(IrCond.NotEqual, trimDone);
                AddI(pos, pos, -2);
                Jmp(trimLoop);
                Mark(trimDone);
                var onlyDot = NewReg(4);
                Mov(onlyDot, intEnd);
                AddI(onlyDot, onlyDot, 2);
                Cmp(pos, onlyDot);
                Jcc(IrCond.NotEqual, afterTrim);
                Mov(pos, intEnd);
                Mark(afterTrim);
            }

            /// <summary>指数后缀：'E'/'e' ± |e10|（lowerE 运行时 0=大写）。minPad 为指数最小位数（E 3 位 / G 2 位）。</summary>
            private void EmitSciExp(IrVirtualRegister fmt, IrVirtualRegister pos, IrVirtualRegister e10, int minPad, IrVirtualRegister lowerE)
            {
                var useLower = NewLabel();
                var eDone = NewLabel();
                Cmp(lowerE, 0);
                Jcc(IrCond.NotEqual, useLower);
                EmitStoreCharAt(fmt, pos, 'E');
                Jmp(eDone);
                Mark(useLower);
                EmitStoreCharAt(fmt, pos, 'e');
                Mark(eDone);
                var e10Neg = NewLabel();
                var e10SignDone = NewLabel();
                var e10Abs = NewReg(4);
                Mov(e10Abs, e10);
                Cmp(e10, 0);
                Jcc(IrCond.Less, e10Neg);
                EmitStoreCharAt(fmt, pos, '+');
                Jmp(e10SignDone);
                Mark(e10Neg);
                EmitStoreCharAt(fmt, pos, '-');
                Neg(e10Abs);
                Mark(e10SignDone);
                var eStr = NewReg(8);
                CallRuntime(eStr, "IntToString", e10Abs);
                var eLen = NewReg(4);
                Load(eLen, eStr, 0, 4);
                var pad = NewReg(4);
                Mov(pad, eLen);
                Neg(pad);
                AddI(pad, pad, minPad);
                EmitWriteRepeatedCharAt(fmt, pos, pad, '0');
                var eData = NewReg(8);
                Lea(eData, eStr, 4);
                EmitCopyCharsAt(fmt, pos, eData, eLen);
            }

            /// <summary>按 D 位尾数（tail 正向）+ e10 组装。sci=true 科学（1 位整数 + E 后缀）；否则定点（小数点放 e10+1 处）。trim 剪尾零。</summary>
            private void EmitAssembleSciG(IrVirtualRegister objOut, IrVirtualRegister tail, IrVirtualRegister D, IrVirtualRegister e10, IrVirtualRegister sign, bool sci, bool trim, int minPad, IrVirtualRegister lowerE)
            {
                var fmt = NewReg(8);
                LeaData(fmt, _formatBuffer);
                var pos = NewReg(4);
                Const(pos, 0);
                var intEnd = NewReg(4);
                var negLabel = NewLabel();
                var signDone = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negLabel);
                Jmp(signDone);
                Mark(negLabel);
                EmitStoreCharAt(fmt, pos, '-');
                Mark(signDone);

                if (sci)
                {
                    EmitCopyCharsAt(fmt, pos, tail, C(4, 1));
                    Mov(intEnd, pos);
                    var noPoint = NewLabel();
                    Cmp(D, 1);
                    Jcc(IrCond.Equal, noPoint);
                    EmitStoreCharAt(fmt, pos, '.');
                    var tailF = NewReg(8);
                    Mov(tailF, tail);
                    AddI(tailF, tailF, 2);
                    var fracLen = NewReg(4);
                    Mov(fracLen, D);
                    AddI(fracLen, fracLen, -1);
                    EmitCopyCharsAt(fmt, pos, tailF, fracLen);
                    Mark(noPoint);
                    if (trim)
                    {
                        EmitTrimTrailingZeros(fmt, pos, intEnd);
                    }
                    EmitSciExp(fmt, pos, e10, minPad, lowerE);
                }
                else
                {
                    var intDigits = NewReg(4);
                    Mov(intDigits, e10);
                    AddI(intDigits, intDigits, 1);
                    var big = NewLabel();
                    var mid = NewLabel();
                    var small = NewLabel();
                    var done = NewLabel();
                    Cmp(intDigits, D);
                    Jcc(IrCond.GreaterOrEqual, big);
                    Cmp(intDigits, 0);
                    Jcc(IrCond.Greater, mid);
                    Jmp(small);
                    Mark(big);
                    EmitCopyCharsAt(fmt, pos, tail, D);
                    var extraZ = NewReg(4);
                    Sub(extraZ, intDigits, D);
                    EmitWriteRepeatedCharAt(fmt, pos, extraZ, '0');
                    Mov(intEnd, pos);
                    Jmp(done);
                    Mark(mid);
                    EmitCopyCharsAt(fmt, pos, tail, intDigits);
                    Mov(intEnd, pos);
                    EmitStoreCharAt(fmt, pos, '.');
                    var tailM = NewReg(8);
                    Mov(tailM, tail);
                    var offM = NewReg(4);
                    Mov(offM, intDigits);
                    Shl(offM, offM, 1);
                    Add(tailM, tailM, offM);
                    var restM = NewReg(4);
                    Sub(restM, D, intDigits);
                    EmitCopyCharsAt(fmt, pos, tailM, restM);
                    Jmp(done);
                    Mark(small);
                    EmitStoreCharAt(fmt, pos, '0');
                    Mov(intEnd, pos);
                    EmitStoreCharAt(fmt, pos, '.');
                    var negID = NewReg(4);
                    Mov(negID, intDigits);
                    Neg(negID);
                    EmitWriteRepeatedCharAt(fmt, pos, negID, '0');
                    EmitCopyCharsAt(fmt, pos, tail, D);
                    Mark(done);
                    if (trim)
                    {
                        EmitTrimTrailingZeros(fmt, pos, intEnd);
                    }
                }

                var lenBytes = NewReg(4);
                Mov(lenBytes, pos);
                CallRuntime(objOut, "AllocStringFromBuf", fmt, lenBytes);
            }

            private void EmitFormatSci()
            {
                var n = _isX64 ? _args[1] : _args[2];
                var flags = _isX64 ? _args[2] : _args[3];
                var mode = NewReg(4);
                Mov(mode, flags);
                AndI(mode, mode, 1);
                var lowerE = NewReg(4);
                Mov(lowerE, flags);
                Shr(lowerE, lowerE, 1);
                AndI(lowerE, lowerE, 1);
                var zeroLabel = NewLabel();
                var specialLabel = NewLabel();
                var nanLabel = NewLabel();
                var gZeroDone = NewLabel();
                var done = NewLabel();

                var b0 = NewReg(4);
                var b1 = NewReg(4);
                if (_isX64)
                {
                    var t = NewReg(8);
                    Mov(t, _args[0]);
                    Shr(t, t, 16);
                    Shr(t, t, 16);
                    Mov(b1, t);
                    Mov(b0, _args[0]);
                }
                else
                {
                    Mov(b0, _args[0]);
                    Mov(b1, _args[1]);
                }

                var sign = NewReg(4);
                var exp = NewReg(4);
                var m0 = NewReg(4);
                var m1 = NewReg(4);
                var e = NewReg(4);
                var isSpecial = NewReg(4);
                var isZero = NewReg(4);
                var isMantZero = NewReg(4);
                EmitSplitDouble(b0, b1, sign, exp, m0, m1, e, isSpecial, isZero, isMantZero);

                // D = mode==1(G) ? n : n+1（至少 1）—— 零/特殊分支也用到，须先算
                var D = NewReg(4);
                var dE = NewLabel();
                var dDone = NewLabel();
                Cmp(mode, 1);
                Jcc(IrCond.Equal, dE);
                Mov(D, n);
                AddI(D, D, 1);
                Jmp(dDone);
                Mark(dE);
                Mov(D, n);
                Mark(dDone);
                var dAtLeast = NewLabel();
                Cmp(D, 1);
                Jcc(IrCond.GreaterOrEqual, dAtLeast);
                Const(D, 1);
                Mark(dAtLeast);

                Cmp(isSpecial, 0);
                Jcc(IrCond.NotEqual, specialLabel);
                Cmp(isZero, 0);
                Jcc(IrCond.NotEqual, zeroLabel);

                // e10_est = trunc(e*30103/100000)（按 |e| 求，负号回代；误差 ≤1 由 S 裕量吸收）
                var eAbs = NewReg(4);
                Mov(eAbs, e);
                var negE2 = NewLabel();
                var absDone = NewLabel();
                Cmp(e, 0);
                Jcc(IrCond.Less, negE2);
                Jmp(absDone);
                Mark(negE2);
                Neg(eAbs);
                Mark(absDone);
                var qEst = NewReg(4);
                Mov(qEst, eAbs);
                Imul(qEst, qEst, C(4, 30103));
                var d100k = C(4, 100000);
                Udiv(qEst, d100k);
                var e10Ready = NewLabel();
                Cmp(e, 0);
                Jcc(IrCond.GreaterOrEqual, e10Ready);
                Neg(qEst);
                Mark(e10Ready);
                var e10est = NewReg(4);
                Mov(e10est, qEst);

                // S = (e10est<0 ? -e10est : 0) + D + 20
                var baseS = NewReg(4);
                Mov(baseS, e10est);
                Neg(baseS);
                var useBase = NewLabel();
                var baseDone = NewLabel();
                Cmp(e10est, 0);
                Jcc(IrCond.Less, useBase);
                Const(baseS, 0);
                Mark(useBase);
                var S = NewReg(4);
                Mov(S, baseS);
                Add(S, S, D);
                AddI(S, S, 20);

                var buf = NewReg(8);
                LeaData(buf, _fmtBigBuf);
                EmitBigSetZero(buf);
                EmitBigSetFromMantissa(buf, m0, m1);
                var i = NewReg(4);
                Const(i, 0);
                var mulLoop = NewLabel();
                var mulDone = NewLabel();
                Mark(mulLoop);
                Cmp(i, S);
                Jcc(IrCond.GreaterOrEqual, mulDone);
                EmitBigMulSmall(buf, 10);
                AddI(i, i, 1);
                Jmp(mulLoop);
                Mark(mulDone);

                var negShift = NewLabel();
                var shiftDone = NewLabel();
                var negE = NewReg(4);
                Cmp(e, 0);
                Jcc(IrCond.Less, negShift);
                EmitBigShl(buf, e);
                Jmp(shiftDone);
                Mark(negShift);
                Mov(negE, e);
                Neg(negE);
                EmitBigShrTrunc(buf, negE);
                Mark(shiftDone);

                var tail = NewReg(8);
                Mov(tail, buf);
                AddI(tail, tail, 1600);
                var d = NewReg(4);
                var tail2 = NewReg(8);
                EmitBigDigitsToTail(buf, tail, d, tail2);

                // e10 = d - S - 1（由位数精确定）
                var e10 = NewReg(4);
                Mov(e10, d);
                Sub(e10, e10, S);
                AddI(e10, e10, -1);

                var carryOut = NewReg(4);
                EmitRoundMantissa(tail2, d, D, carryOut);
                var noCarry = NewLabel();
                Cmp(carryOut, 0);
                Jcc(IrCond.Equal, noCarry);
                AddI(e10, e10, 1);
                Mark(noCarry);

                // G 定点/科学切换：-4 <= e10 < n → 定点
                var gSciLabel = NewLabel();
                Cmp(mode, 0);
                Jcc(IrCond.Equal, gSciLabel);
                Cmp(e10, -4);
                Jcc(IrCond.Less, gSciLabel);
                Cmp(e10, n);
                Jcc(IrCond.GreaterOrEqual, gSciLabel);
                var objG = NewReg(8);
                EmitAssembleSciG(objG, tail2, D, e10, sign, sci: false, trim: true, minPad: 2, lowerE);
                StoreRet(objG);
                Jmp(done);
                Mark(gSciLabel);
                var gTrim = NewLabel();
                var gNoTrim = NewLabel();
                var gTrimDone = NewLabel();
                var objS = NewReg(8);
                Cmp(mode, 1);
                Jcc(IrCond.Equal, gTrim);
                EmitAssembleSciG(objS, tail2, D, e10, sign, sci: true, trim: false, minPad: 3, lowerE);
                StoreRet(objS);
                Jmp(gTrimDone);
                Mark(gTrim);
                EmitAssembleSciG(objS, tail2, D, e10, sign, sci: true, trim: true, minPad: 2, lowerE);
                StoreRet(objS);
                Mark(gTrimDone);
                Jmp(done);

                Mark(zeroLabel);
                var gZero = NewLabel();
                Cmp(mode, 1);
                Jcc(IrCond.Equal, gZero);
                // E 零："0." + n 零 + "E+000"
                var zbuf = NewReg(8);
                LeaData(zbuf, _fmtBigBuf);
                var ztail = NewReg(8);
                Mov(ztail, zbuf);
                AddI(ztail, ztail, 1600);
                var zcnt = NewReg(4);
                Const(zcnt, 0);
                var zloop = NewLabel();
                var zdone = NewLabel();
                Mark(zloop);
                Cmp(zcnt, D);
                Jcc(IrCond.GreaterOrEqual, zdone);
                EmitStoreDigit(ztail, zcnt, C(4, '0'));
                AddI(zcnt, zcnt, 1);
                Jmp(zloop);
                Mark(zdone);
                var objZ = NewReg(8);
                EmitAssembleSciG(objZ, ztail, D, C(4, 0), C(4, 0), sci: true, trim: false, minPad: 3, lowerE);
                StoreRet(objZ);
                Jmp(gZeroDone);
                Mark(gZero);
                EmitReturnFixedString(C(4, 0), _zeroString, _zeroString);
                Mark(gZeroDone);
                Jmp(done);

                Mark(specialLabel);
                Cmp(isMantZero, 0);
                Jcc(IrCond.Equal, nanLabel);
                EmitReturnFixedString(sign, _infinityString, _negInfinityString);
                Jmp(done);
                Mark(nanLabel);
                EmitReturnFixedString(sign, _nanString, _nanString);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }


            /// <summary>复制 InternString 数据并返回新对象（sign 选择正/负串）。</summary>
            private void EmitReturnFixedString(IrVirtualRegister sign, string positiveKey, string negativeKey)
            {
                var oom = NewLabel();
                var done = NewLabel();
                var usePos = NewLabel();

                var s = NewReg(8);
                LeaData(s, positiveKey);
                Cmp(sign, 0);
                Jcc(IrCond.Equal, usePos);
                LeaData(s, negativeKey);
                Mark(usePos);

                var len = NewReg(4);
                Load(len, s, 0, 4);
                var size = NewReg(4);
                Mov(size, len);
                Shl(size, size, 1);
                AddI(size, size, 3);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);
                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                Store(obj, 0, len, 4);

                var count = NewReg(4);
                Mov(count, len);
                AddI(count, count, 1);
                Shr(count, count, 1);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                var src = NewReg(8);
                Lea(src, s, 4);
                CallRuntime(null, "CopyChars", dst, src, count);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
            }

        }
    }
}
