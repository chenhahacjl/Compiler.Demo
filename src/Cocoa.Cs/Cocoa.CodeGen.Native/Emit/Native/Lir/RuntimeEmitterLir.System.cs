using System;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Lir
{
    internal static partial class RuntimeEmitterLir
    {
        private sealed partial class RuntimeFunctionEmitter
        {
            private void EmitInput()
            {
                var strip = NewLabel();
                var pop = NewLabel();
                var stripped = NewLabel();
                var fail = NewLabel();
                var done = NewLabel();
                var notConsole = NewLabel();
                var haveCount = NewLabel();

                var handle = NewPtr();
                SysCall(handle, "GetStdHandle", 1, C(4, -10));
                var fileType = NewReg(4);
                SysCall(fileType, "GetFileType", 1, handle);

                var buf = NewPtr();
                LeaData(buf, _inputBuffer);
                var written = NewReg(4);
                var writtenAddr = NewPtr();
                LeaSlot(writtenAddr, written);

                Cmp(fileType, 2);
                Jcc(LirCond.NotEqual, notConsole);

                var chars = NewReg(4);
                var ok = NewReg(4);
                SysCall(ok, "ReadConsoleW", 5, handle, buf, C(4, 0x1000), writtenAddr);
                Cmp(ok, 0);
                Jcc(LirCond.Equal, fail);
                Load(chars, writtenAddr, 0, 4);
                Jmp(haveCount);

                Mark(notConsole);
                var okFile = NewReg(4);
                SysCall(okFile, "ReadFile", 5, handle, buf, C(4, 0x2000), writtenAddr);
                Cmp(okFile, 0);
                Jcc(LirCond.Equal, fail);
                var bytes = NewReg(4);
                Load(bytes, writtenAddr, 0, 4);
                Mov(chars, bytes);
                Shr(chars, chars, 1);

                Mark(haveCount);
                // 去尾部 \r \n
                Mark(strip);
                Cmp(chars, 0);
                Jcc(LirCond.Equal, stripped);
                var idx = NewReg(4);
                Mov(idx, chars);
                Shl(idx, idx, 1);
                var addr = NewPtr();
                Add(addr, buf, idx);
                var last = NewReg(4);
                Load(last, addr, -2, 2);
                Cmp(last, 0x0D);
                Jcc(LirCond.Equal, pop);
                Cmp(last, 0x0A);
                Jcc(LirCond.NotEqual, stripped);

                Mark(pop);
                AddI(chars, chars, -1);
                Jmp(strip);

                Mark(stripped);
                var size = NewReg(4);
                Mov(size, chars);
                AddI(size, size, 1);
                Shr(size, size, 1);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewPtr();
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, fail);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, chars);
                AddI(count, count, 1);
                Shr(count, count, 1);
                var dst = NewPtr();
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, buf, count);
                StoreRet(obj);
                Jmp(done);

                Mark(fail);
                var empty = NewPtr();
                LeaData(empty, _emptyString);
                StoreRet(empty);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ReadKey(intercept:4) → char
            // 读取单键。intercept=0 时回显（WriteConsoleW）；=1 时不回显。
            // 经 ReadConsoleInputW 读 INPUT_RECORD，取 KEY_EVENT 的 bKeyDown 与 UnicodeChar。
            // ------------------------------------------------------------------

            private void EmitReadKey()
            {
                var intercept = _args[0];
                var inHandle = NewPtr();
                SysCall(inHandle, "GetStdHandle", 1, C(4, -10));

                var buf = NewPtr();
                LeaData(buf, _inputBuffer);
                var written = NewReg(4);
                var writtenAddr = NewPtr();
                LeaSlot(writtenAddr, written);

                var loop = NewLabel();
                var gotKey = NewLabel();
                var done = NewLabel();

                Mark(loop);
                var ok = NewReg(4);
                SysCall(ok, "ReadConsoleInputW", 4, inHandle, buf, C(4, 1), writtenAddr);
                Cmp(ok, 0);
                Jcc(LirCond.Equal, loop);
                var count = NewReg(4);
                Load(count, writtenAddr, 0, 4);
                Cmp(count, 0);
                Jcc(LirCond.Equal, loop);

                var eventType = NewReg(4);
                Load(eventType, buf, 0, 2);
                Cmp(eventType, 1);
                Jcc(LirCond.NotEqual, loop);

                var keyDown = NewReg(4);
                Load(keyDown, buf, 4, 4);
                Cmp(keyDown, 0);
                Jcc(LirCond.Equal, loop);

                // 若 intercept=0，回显该字符（WriteConsoleW 到输出句柄）
                Cmp(intercept, 0);
                Jcc(LirCond.NotEqual, gotKey);
                var outHandle = NewPtr();
                SysCall(outHandle, "GetStdHandle", 1, C(4, -11));
                var charAddr = NewPtr();
                Lea(charAddr, buf, 14);
                var echoOk = NewReg(4);
                SysCall(echoOk, "WriteConsoleW", 5, outHandle, charAddr, C(4, 1), writtenAddr);

                Mark(gotKey);
                var ch = NewReg(4);
                Load(ch, buf, 14, 2);
                StoreRet(ch);

                Mark(done);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Random(max:4) → 0..max-1（xorshift32）
            // ------------------------------------------------------------------

            private void EmitRandom()
            {
                var max = _args[0];
                var ready = NewLabel();
                var zero = NewLabel();
                var done = NewLabel();

                var state = NewPtr();
                LeaData(state, _rngState);
                var seed = NewReg(4);
                Load(seed, state, 0, 4);
                Cmp(seed, 0);
                Jcc(LirCond.NotEqual, ready);

                var tick = NewReg(4);
                SysCall(tick, _tickCountImport, 0);
                var warmed = NewReg(4);
                And(warmed, tick, C(4, 0x7FFFFFFF));
                Or(warmed, warmed, C(4, 1));
                Store(state, 0, warmed, 4);

                Mark(ready);
                var x = NewReg(4);
                Load(x, state, 0, 4);
                var y1 = NewReg(4);
                Mov(y1, x);
                Shl(y1, y1, 13);
                Xor(x, x, y1);
                var y2 = NewReg(4);
                Mov(y2, x);
                Shr(y2, y2, 17);
                Xor(x, x, y2);
                var y3 = NewReg(4);
                Mov(y3, x);
                Shl(y3, y3, 5);
                Xor(x, x, y3);
                Store(state, 0, x, 4);

                Cmp(max, 0);
                Jcc(LirCond.LessOrEqual, zero);
                var r = NewReg(4);
                Mov(r, x);
                Urem(r, max);
                StoreRet(r);
                Jmp(done);

                Mark(zero);
                var zeroResult = C(4, 0);
                StoreRet(zeroResult);

                Mark(done);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ObjectEquals(a:8, b:8) → bool（指针比较）
            // ------------------------------------------------------------------

            private void EmitObjectEquals()
            {
                var a = _args[0];
                var b = _args[1];
                Cmp(a, b);
                var result = NewReg(4);
                Setcc(result, LirCond.Equal);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ObjectToString(obj:ptr) → str（e-M19 M4）：读对象的 vtable 与名字指针。
            // 对象布局 [0]=vtablePtr；vtable [8]=类型全名字符串指针（伪记录自引用的 Type 值同样成立）
            // ------------------------------------------------------------------

            private void EmitObjectToString()
            {
                var obj = _args[0];
                var vtable = NewPtr();
                Load(vtable, obj, 0, _isX64 ? 8 : 4);
                var name = NewPtr();
                Load(name, vtable, 8, _isX64 ? 8 : 4);
                StoreRet(name);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ObjectGetHashCode(x:8) → int（指针位模式散列：lo ^ hi 后乘黄金比例常数）
            // x86 指针为 4，仅低 dword 参与散列
            // ------------------------------------------------------------------

            private void EmitObjectGetHashCode()
            {
                var value = _args[0];
                var lo = NewReg(4);
                LoadSlotField(lo, value, 0, 4);

                LirVirtualRegister mixed;
                if (_isX64)
                {
                    var hi = NewReg(4);
                    LoadSlotField(hi, value, 4, 4);
                    mixed = NewReg(4);
                    Xor(mixed, lo, hi);
                }
                else
                {
                    mixed = lo;
                }

                var hashed = NewReg(4);
                Imul(hashed, mixed, C(4, unchecked((int)0x9E3779B1)));
                StoreRet(hashed);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ObjectGetType(obj:ptr) → vtablePtr（GetType 非虚，占槽 3 保持统一发射路径）
            // ------------------------------------------------------------------

            private void EmitObjectGetType()
            {
                var obj = _args[0];
                var vtable = NewPtr();
                Load(vtable, obj, 0, _isX64 ? 8 : 4);
                StoreRet(vtable);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // TypeSimpleName(s:str) → str（最后一个 '.' 之后的部分；无点回退原串）
            // 与 IL FullName.Substring(LastIndexOf('.')+1) 组合语义一致，三后端统一。
            // ------------------------------------------------------------------

            private void EmitTypeSimpleName()
            {
                var s = _args[0];
                var len = NewReg(4);
                Load(len, s, 0, 4);

                var chars = NewPtr();
                Lea(chars, s, 4);

                var index = NewReg(4);
                Const(index, 0);
                var lastDot = NewReg(4);
                Const(lastDot, -1);
                var loop = NewLabel();
                var scanContinue = NewLabel();
                var scanDone = NewLabel();
                var noDot = NewLabel();
                var done = NewLabel();

                Mark(loop);
                Cmp(index, len);
                Jcc(LirCond.AboveOrEqual, scanDone);
                var ch = NewReg(4);
                {
                    // chars[i]：基址寄存器可变偏移经 Lea+Add 组合
                    var offset = NewReg(4);
                    Mov(offset, index);
                    Shl(offset, offset, 1);
                    var address = NewPtr();
                    Lea(address, chars, 0);
                    Add(address, address, offset);
                    Load(ch, address, 0, 2);
                }

                Cmp(ch, '.');
                Jcc(LirCond.NotEqual, scanContinue);
                Mov(lastDot, index);

                Mark(scanContinue);
                AddI(index, index, 1);
                Jmp(loop);

                Mark(scanDone);
                Cmp(lastDot, 0);
                Jcc(LirCond.Less, noDot);

                var start = NewReg(4);
                AddI(start, lastDot, 1);
                var count = NewReg(4);
                Sub(count, len, start);
                var result = NewPtr();
                CallRuntime(result, "Substring", s, start, count);
                StoreRet(result);
                Jmp(done);

                Mark(noDot);
                StoreRet(s);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ExitProcess(code:4)
            // ------------------------------------------------------------------

            private void EmitExitProcess()
            {
                var code = _args[0];
                SysCall(null, "ExitProcess", 1, code);
                EndFunction(_currentFunction!, 0);
            }

            // Now() -> tick:4
            private void EmitNow()
            {
                var tick = NewReg(4);
                SysCall(tick, _tickCountImport, 0);
                StoreRet(tick);
                EndFunction(_currentFunction!, 4);
            }

            // Sleep(ms:4)
            private void EmitSleep()
            {
                var ms = _args[0];
                SysCall(null, "Sleep", 1, ms);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // Beep(frequency:4, duration:4) → void（kernel32 Beep：扬声器蜂鸣）
            // ------------------------------------------------------------------

            private void EmitBeep()
            {
                var frequency = _args[0];
                var duration = _args[1];
                SysCall(null, "Beep", 2, frequency, duration);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // Y-P0-1：文件 IO / 环境 syscall（G7-③补齐）
            // 字符串对象 = 堆指针 [len:4][chars:2×len]（无 null 结尾），Win32 宽字符串 API 需 LPCWSTR 经 WidePtrZ 复制补 null。
            // SysCall 上限 6 参：文件 IO 用 ucrtbase 低参 API + 6 参 MultiByteToWideChar；WideCharToMultiByte(8 参) 用手动编码替代。
            // ------------------------------------------------------------------

            /// <summary>取 CO 字符串对象的宽字符区指针（chars@4）。</summary>
            private LirVirtualRegister WidePtr(LirVirtualRegister s)
            {
                var p = NewPtr();
                Lea(p, s, 4);
                return p;
            }

            /// <summary>
            /// 复制 CO 字符串到 <paramref name="bufferKey"/> 并补 null 结尾，返回 LPCWSTR 指针。
            /// CO 串布局 [len:4][chars:2×len] 无 null 结尾（尾 padding 可能含拷贝残留）。
            /// 直接传给 Win32 宽字符串 API（_wfopen/GetFileAttributesW 等）会读越界 → 非确定性失败。
            /// 在 helper 内立即消费；两个参数（src/dst）须用不同缓冲，否则互相覆盖。
            /// </summary>
            private LirVirtualRegister WidePtrZInto(LirVirtualRegister s, string bufferKey)
            {
                var path = WidePtr(s);
                var pathLen = NewReg(4);
                Load(pathLen, s, 0, 4);
                var pb = NewPtr();
                LeaData(pb, bufferKey);
                var pSrc = NewPtr();
                Mov(pSrc, path);
                var pDst = NewPtr();
                Mov(pDst, pb);
                var pathLoop = NewLabel();
                var pathDone = NewLabel();
                Mark(pathLoop);
                Cmp(pathLen, 0);
                Jcc(LirCond.Equal, pathDone);
                var pch = NewReg(4);
                Load(pch, pSrc, 0, 2);
                Store(pDst, 0, pch, 2);
                AddI(pDst, pDst, 2);
                AddI(pSrc, pSrc, 2);
                AddI(pathLen, pathLen, -1);
                Jmp(pathLoop);
                Mark(pathDone);
                Const(pch, 0);
                Store(pDst, 0, pch, 2);
                return pb;
            }

            /// <summary>复制到主缓冲（单路径场景）。</summary>
            private LirVirtualRegister WidePtrZ(LirVirtualRegister s) => WidePtrZInto(s, _fileBuffer);
        }
    }
}
