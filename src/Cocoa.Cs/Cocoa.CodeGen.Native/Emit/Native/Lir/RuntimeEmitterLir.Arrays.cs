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
            // ------------------------------------------------------------------

            // ------------------------------------------------------------------
            // NewArray(size:4, elementSize:4) �?ptr:8
            // 布局：[0..4) 长度；[8..) 元素区（8 字节对齐，内存零初始化）
            // ------------------------------------------------------------------

            private void EmitNewArray()
            {
                var size = _args[0];
                var elementSize = _args[1];
                var oom = NewLabel();
                var done = NewLabel();

                var total = NewReg(4);
                Imul(total, size, elementSize);
                AddI(total, total, 7);
                Shr(total, total, 3);
                Shl(total, total, 3);
                AddI(total, total, 8);

                var obj = NewPtr();
                CallRuntime(obj, "Alloc", total);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, oom);
                Store(obj, 0, size, 4);
                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ArrayBoundsCheck(index:4, length:4) �?越界时报错退�?
            // ------------------------------------------------------------------

            private void EmitArrayBoundsCheck()
            {
                var index = _args[0];
                var length = _args[1];
                var error = NewLabel();

                Cmp(index, 0);
                Jcc(LirCond.Less, error);
                Cmp(index, length);
                Jcc(LirCond.GreaterOrEqual, error);
                EndFunction(_currentFunction!, 0);

                Mark(error);
                var message = NewPtr();
                LeaData(message, _arrayBoundsMessage);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            private void EmitError(string messageKey)
            {
                var message = NewPtr();
                LeaData(message, messageKey);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // BuildArgs() �?ptr（string[]�?
            // �?GetCommandLineW 读取命令行（UTF-16），跳过程序名，�?MS 风格解析
            // 剩余参数：空白（空格/制表符）分隔；引号包裹的空白不分割；引号本身�?
            // 参数内容中剥离。构�?string[]（布局�?NewArray），失败（OOM）返�?0�?
            // ------------------------------------------------------------------

            private void EmitBuildArgs()
            {
                var elementSize = _isX64 ? 8 : 4;

                var cmd = NewPtr();
                SysCall(cmd, "GetCommandLineW", 0);

                var p = NewPtr();
                Mov(p, cmd);
                var inQuotes = C(4, 0);
                var ch = NewReg(4);
                var count = C(4, 0);

                // ---- 定位程序名后的第一个参数位置（first�?---
                var skipProg = NewLabel();
                var skipProgCheck = NewLabel();
                var skipProgNext = NewLabel();
                var skipProgFound = NewLabel();

                Mark(skipProg);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(LirCond.Equal, skipProgFound);
                Cmp(ch, 34);
                Jcc(LirCond.NotEqual, skipProgCheck);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(skipProgNext);
                Mark(skipProgCheck);
                Cmp(inQuotes, 0);
                Jcc(LirCond.NotEqual, skipProgNext);
                Cmp(ch, 32);
                Jcc(LirCond.Equal, skipProgFound);
                Cmp(ch, 9);
                Jcc(LirCond.Equal, skipProgFound);
                Mark(skipProgNext);
                Lea(p, p, 2);
                Jmp(skipProg);

                Mark(skipProgFound);
                var first = NewPtr();
                Mov(first, p);

                // ---- pass 1: 计数（count�?---
                var countWs = NewLabel();
                var countWsNext = NewLabel();
                var countDone = NewLabel();
                var countTok = NewLabel();
                var countTokNoQuote = NewLabel();
                var countTokEnd = NewLabel();
                var countTokNext = NewLabel();

                Mark(countWs);
                Load(ch, p, 0, 2);
                Cmp(ch, 32);
                Jcc(LirCond.Equal, countWsNext);
                Cmp(ch, 9);
                Jcc(LirCond.Equal, countWsNext);
                Cmp(ch, 0);
                Jcc(LirCond.Equal, countDone);
                Jmp(countTok);
                Mark(countWsNext);
                Lea(p, p, 2);
                Jmp(countWs);

                Mark(countTok);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(LirCond.Equal, countTokEnd);
                Cmp(ch, 34);
                Jcc(LirCond.NotEqual, countTokNoQuote);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(countTokNext);
                Mark(countTokNoQuote);
                Cmp(inQuotes, 0);
                Jcc(LirCond.NotEqual, countTokNext);
                Cmp(ch, 32);
                Jcc(LirCond.Equal, countTokEnd);
                Cmp(ch, 9);
                Jcc(LirCond.Equal, countTokEnd);
                Jmp(countTokNext);
                Mark(countTokEnd);
                AddI(count, count, 1);
                Jmp(countWs);
                Mark(countTokNext);
                Lea(p, p, 2);
                Jmp(countTok);

                Mark(countDone);

                // ---- 分配数组 ----
                var elementSizeReg = C(4, elementSize);
                var arr = NewPtr();
                SetArg(0, count);
                SetArg(1, elementSizeReg);
                Add(LirOpCode.Call, arr, LirOperand.Runtime("NewArray"), LirOperand.Constant(0));

                var oom = NewLabel();
                var finish = NewLabel();
                var done = NewLabel();
                Cmp(arr, 0);
                Jcc(LirCond.Equal, oom);

                // ---- pass 2: 逐个参数构�?string 并写入数�?----
                Mov(p, first);
                var slot = NewPtr();
                var slotBase = NewPtr();
                Lea(slotBase, arr, 8);
                Mov(slot, slotBase);

                var buildWs = NewLabel();
                var buildWsNext = NewLabel();
                var buildTok = NewLabel();
                var buildTokNoQuote = NewLabel();
                var buildTokChar = NewLabel();
                var buildTokScan = NewLabel();
                var buildStr = NewLabel();
                var buildTokNext = NewLabel();
                var buildStrNext = NewLabel();
                var copyLoop = NewLabel();
                var copySkip = NewLabel();
                var copyDone = NewLabel();

                Mark(buildWs);
                Load(ch, p, 0, 2);
                Cmp(ch, 32);
                Jcc(LirCond.Equal, buildWsNext);
                Cmp(ch, 9);
                Jcc(LirCond.Equal, buildWsNext);
                Cmp(ch, 0);
                Jcc(LirCond.Equal, finish);
                Jmp(buildTok);
                Mark(buildWsNext);
                Lea(p, p, 2);
                Jmp(buildWs);

                Mark(buildTok);
                var start = NewPtr();
                Mov(start, p);
                var lenChars = C(4, 0);
                Jmp(buildTokScan);

                Mark(buildTokNext);
                Lea(p, p, 2);

                Mark(buildTokScan);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(LirCond.Equal, buildStr);
                Cmp(ch, 34);
                Jcc(LirCond.NotEqual, buildTokNoQuote);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(buildTokNext);
                Mark(buildTokNoQuote);
                Cmp(inQuotes, 0);
                Jcc(LirCond.NotEqual, buildTokChar);
                Cmp(ch, 32);
                Jcc(LirCond.Equal, buildStr);
                Cmp(ch, 9);
                Jcc(LirCond.Equal, buildStr);
                Mark(buildTokChar);
                AddI(lenChars, lenChars, 1);
                Jmp(buildTokNext);

                // ---- 构造字符串：Alloc(lenChars*2+4 对齐 4)，剥离引号拷�?----
                Mark(buildStr);
                var bytes = NewReg(4);
                Mov(bytes, lenChars);
                Shl(bytes, bytes, 1);
                AddI(bytes, bytes, 4);
                AddI(bytes, bytes, 3);
                And(bytes, bytes, C(4, 0xFFFFFFFC));
                var obj = NewPtr();
                CallRuntime(obj, "Alloc", bytes);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, buildStrNext);

Store(obj, 0, lenChars, 4);
                var dst = NewPtr();
                Lea(dst, obj, 4);
                var src = NewPtr();
                Mov(src, start);
                var remaining = NewReg(4);
                Mov(remaining, lenChars);

                Mark(copyLoop);
                Cmp(remaining, 0);
                Jcc(LirCond.Equal, copyDone);
                Load(ch, src, 0, 2);
                Cmp(ch, 34);
                Jcc(LirCond.Equal, copySkip);
                Store(dst, 0, ch, 2);
                Lea(dst, dst, 2);
                AddI(remaining, remaining, -1);
                Mark(copySkip);
                Lea(src, src, 2);
                Jmp(copyLoop);

                Mark(copyDone);
                Store(slot, 0, obj, elementSize);

                Mark(buildStrNext);
                Lea(slot, slot, elementSize);
                Jmp(buildWs);

                Mark(finish);
                StoreRet(arr);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);
                Jmp(done);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // Sha256Hash(data:8) �?u8[] (32 bytes)
            // Uses one-shot BCryptHash (Win10 1803+): BCryptHash(alg, NULL, 0, pbData, cbData, pbOutput, cbOutput)
            // Array layout: [0..4) length; [8..) element data (8-byte aligned)
            // ------------------------------------------------------------------

            private void EmitSha256Hash()
            {
                var data = _args[0];
                var errLabel = NewLabel();
                var doneLabel = NewLabel();
                var zero32 = C(4, 0);

                // ---- BCryptOpenAlgorithmProvider (using LeaSlot for output param) ----
                var algoStrData = _program.AddData(LirDataItem.ByteArray(Prefix + "Sha256Algo", new byte[] {
                    0x53, 0x00, 0x48, 0x00, 0x41, 0x00, 0x32, 0x00, 0x35, 0x00, 0x36, 0x00, 0x00, 0x00 }));
                var algoStr = NewPtr();
                LeaData(algoStr, algoStrData);

                var algCache = NewPtr();
                LeaData(algCache, _bcryptAlg);
                var cachedAlg = NewReg(_isX64 ? 8 : 4);
                Load(cachedAlg, algCache, 0, _isX64 ? 8 : 4);
                var algCached = NewLabel();
                Cmp(cachedAlg, 0);
                Jcc(LirCond.NotEqual, algCached);

                // Use LeaSlot buffer for output param (same pattern as ReadConsoleW's writtenAddr)
                var algSlot = NewReg(_isX64 ? 8 : 4);
                var algAddr = NewPtr();
                LeaSlot(algAddr, algSlot);
                var nullPtr = NewReg(_isX64 ? 8 : 4);
                Const(nullPtr, 0);
                SysCallDll(null, "bcrypt.dll", "BCryptOpenAlgorithmProvider",
                    4, false, algAddr, algoStr, nullPtr, zero32);
                Load(cachedAlg, algAddr, 0, _isX64 ? 8 : 4);
                Store(algCache, 0, cachedAlg, _isX64 ? 8 : 4);

                Mark(algCached);

                // ---- BCryptCreateHash ----
                var hashSlot = NewReg(_isX64 ? 8 : 4);
                var hashAddr = NewPtr();
                LeaSlot(hashAddr, hashSlot);
                var nullPtr2 = NewReg(_isX64 ? 8 : 4);
                Const(nullPtr2, 0);
                SysCallDll(null, "bcrypt.dll", "BCryptCreateHash",
                    6, false, cachedAlg, hashAddr, nullPtr2, zero32, nullPtr2, zero32);

                var hashVal = NewReg(_isX64 ? 8 : 4);
                Load(hashVal, hashAddr, 0, _isX64 ? 8 : 4);
                Cmp(hashVal, 0);
                Jcc(LirCond.Equal, errLabel);

                // ---- BCryptHashData ----
                var dataLen = NewReg(4);
                Load(dataLen, data, 0, 4);
                var dataPtr = NewPtr();
                Lea(dataPtr, data, 8);
                SysCallDll(null, "bcrypt.dll", "BCryptHashData",
                    4, false, hashVal, dataPtr, dataLen, zero32);

                // ---- VirtualAlloc 32 bytes ----
                var buf32 = NewPtr();
                SysCall(buf32, "VirtualAlloc", 4, C(4, 0), C(4, 32), C(4, 0x3000), C(4, 0x04));
                Cmp(buf32, 0);
                Jcc(LirCond.Equal, errLabel);

                // ---- BCryptFinishHash ----
                SysCallDll(null, "bcrypt.dll", "BCryptFinishHash",
                    4, false, hashVal, buf32, C(4, 32), zero32);

                // ---- BCryptDestroyHash ----
                SysCallDll(null, "bcrypt.dll", "BCryptDestroyHash",
                    1, false, hashVal);

                // ---- Copy hash to u8[32] array ----
                var arr = NewPtr();
                CallRuntime(arr, "NewArray", C(4, 32), C(4, 1));
                Cmp(arr, 0);
                Jcc(LirCond.Equal, errLabel);

                var ci = NewReg(4);
                Const(ci, 0);
                var copyLoop = NewLabel();
                var copyDone = NewLabel();
                Mark(copyLoop);
                Cmp(ci, C(4, 32));
                Jcc(LirCond.GreaterOrEqual, copyDone);
                var tb = NewReg(4);
                Load(tb, buf32, 0, 1);
                var arrDst = NewPtr();
                Lea(arrDst, arr, 8);
                var arrOff = NewPtr();
                Mov(arrOff, ci);
                Add(arrDst, arrDst, arrOff);
                Store(arrDst, 0, tb, 1);
                AddI(buf32, buf32, 1);
                AddI(ci, ci, 1);
                Jmp(copyLoop);

                Mark(copyDone);

                // Free temp buffer
                SysCallDll(null, "kernel32.dll", "VirtualFree", 3, false, buf32, zero32, C(4, 0x8000));

                StoreRet(arr);
                Jmp(doneLabel);

                Mark(errLabel);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(doneLabel);
                EndFunction(_currentFunction!, 8);
            }

            // LaunchProcess(path:8, args:8) �?i32 exit code
            // Uses _wsystem(path + " " + args) from ucrtbase.dll
            private void EmitLaunchProcess()
            {
                var errLabel = NewLabel();
                var doneLabel = NewLabel();

                var path = _args[0];
                var args = _args[1];

                // Build command line: path + " " + args
                var space = NewPtr();
                LeaData(space, _emptyString);
                var cmdConcat1 = NewPtr();
                CallRuntime(cmdConcat1, "Concat", path, space);
                var cmdLine = NewPtr();
                CallRuntime(cmdLine, "Concat", cmdConcat1, args);

                // Copy CO string chars to wchar buffer with null termination
                // CO string layout: [len:4][chars:2*len]
                var cmdWbuf = NewPtr();
                LeaData(cmdWbuf, _fileBuffer2);
                var strLen = NewReg(4);
                Load(strLen, cmdLine, 0, 4);
                var di = NewReg(4);
                Const(di, 0);
                var copyLoop = NewLabel();
                var copyDone = NewLabel();
                Mark(copyLoop);
                Cmp(di, strLen);
                Jcc(LirCond.GreaterOrEqual, copyDone);
                // src = cmdLine + 4 + di*2
                var ch = NewReg(4);
                var srcOff = NewPtr();
                Mov(srcOff, di);
                AddI(srcOff, srcOff, 2);
                var srcAddr = NewPtr();
                Lea(srcAddr, cmdLine, 4);
                Add(srcAddr, srcAddr, srcOff);
                Load(ch, srcAddr, 0, 2);
                // dst = cmdWbuf + di*2
                var dstAddr = NewPtr();
                Lea(dstAddr, cmdWbuf, 0);
                var diBytes = NewPtr();
                Mov(diBytes, di);
                AddI(diBytes, diBytes, 2);
                Add(dstAddr, dstAddr, diBytes);
                Store(dstAddr, 0, ch, 2);
                AddI(di, di, 1);
                Jmp(copyLoop);
                Mark(copyDone);
                // null-terminate
                var nullCh = C(4, 0);
                var termAddr = NewPtr();
                Lea(termAddr, cmdWbuf, 0);
                var termBytes = NewPtr();
                Mov(termBytes, di);
                Add(termAddr, termAddr, termBytes);
                Add(termAddr, termAddr, termBytes);
                Store(termAddr, 0, nullCh, 2);

                // _wsystem(cmdWbuf) �?synchronous, returns exit code
                var exitCode = NewReg(4);
                SysCallDll(exitCode, "ucrtbase.dll", "_wsystem", 1, true, cmdWbuf);

                StoreRet(exitCode);
                Jmp(doneLabel);

                Mark(errLabel);
                StoreRet(C(4, -1));

                Mark(doneLabel);
                EndFunction(_currentFunction!, 8);
            }

        }
    }
}
