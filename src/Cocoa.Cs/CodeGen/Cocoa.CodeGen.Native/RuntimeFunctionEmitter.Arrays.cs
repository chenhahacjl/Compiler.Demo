using System;
using Cocoa.Targeting;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;


using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    internal sealed partial class RuntimeFunctionEmitter
    {
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // NewArray(size:4, elementSize:4, vtable:ps) 鈫?ptr:8
        // 甯冨眬锛堝榻?.NET 鏁扮粍瀵硅薄锛夛細[0)=vtablePtr [4..8)=pad [8..12)=闀垮害 [12..16)=pad [16..)=鍏冪礌鍖?8瀵归綈)
        // ------------------------------------------------------------------

        private void EmitNewArray()
        {
            var size = _args[0];
            var elementSize = _args[1];
            var vtable = _args[2];
            var oom = NewLabel();
            var done = NewLabel();
            var ps = _isX64 ? 8 : 4;

            var payload = NewReg(4);
            Imul(payload, size, elementSize);
            var total = NewReg(4);
            Mov(total, payload);
            AddI(total, total, 7);
            Shr(total, total, 3);
            Shl(total, total, 3);
            AddI(total, total, NativeObjectModel.ArrayElementOffset);

            var obj = NewPtr();
            CallRuntime(obj, "Alloc", total);
            Cmp(obj, 0);
            Jcc(LirCond.Equal, oom);
            Store(obj, 0, vtable, ps);
            Store(obj, NativeObjectModel.ArrayLengthOffset, size, 4);
            StoreRet(obj);
            Jmp(done);

            Mark(oom);
            var zero = C(8, 0);
            StoreRet(zero);

            Mark(done);
            EndFunction(_currentFunction!, 8);
        }

        // ------------------------------------------------------------------
        // SliceArray(src:8, start:4, count:4, elemSize:4) 鈫?ptr:8
        // N1锛欳opyRange 鍘熻鈥斺€旀妸婧愭暟缁勫厓绱犲尯 [start, start+count) 澶嶅埗杩涙柊鏁扮粍銆?
        // Index/Range 鍒囩墖锛坆inder 闄嶇骇璺緞锛変笌鏃?SDK 鍥為€€鍏变韩锛涘瓧鑺傜矑搴︽嫹璐濋€傞厤鍏ㄩ儴鍏冪礌瀹藉害銆?
        // ------------------------------------------------------------------

        private void EmitSliceArray()
        {
            var src = _args[0];
            var start = _args[1];
            var count = _args[2];
            var elementSize = _args[3];

            // dst = NewArray(count, elemSize, srcVtable)鈥斺€斿垏鐗囦繚鎸佹簮鏁扮粍绫诲瀷锛堝鍒?vtable 澶达級
            var srcVtable = NewPtr();
            Load(srcVtable, src, 0, _isX64 ? 8 : 4);
            var dst = NewPtr();
            CallRuntime(dst, "NewArray", count, elementSize, srcVtable);

            // 瓒婄晫鍗抽€€鍑猴細start >= 0銆乧ount >= 0銆乻tart + count <= src.Length
            var srcLength = NewReg(4);
            Load(srcLength, src, NativeObjectModel.ArrayLengthOffset, 4);
            var error = NewLabel();
            var exit = NewLabel();
            Cmp(start, 0);
            Jcc(LirCond.Less, error);
            Cmp(count, 0);
            Jcc(LirCond.Less, error);
            var end = NewReg(4);
            Add(end, start, count);
            Cmp(end, srcLength);
            Jcc(LirCond.Greater, error);

            // 瀛楄妭绮掑害閫愬瓧鑺傛嫹璐濓細dst 瀛楄妭鍖篬i] = src 瀛楄妭鍖篬(start路elemSize) + i]
            var totalBytes = NewReg(4);
            Imul(totalBytes, count, elementSize);
            var srcByteBase = NewReg(4);
            Imul(srcByteBase, start, elementSize);

            var index = NewReg(4);
            Mov(index, C(4, 0));
            var loop = NewLabel();
            var done = NewLabel();

            Mark(loop);
            Cmp(index, totalBytes);
            Jcc(LirCond.GreaterOrEqual, done);

            var srcAddr = NewPtr();
            Lea(srcAddr, src, NativeObjectModel.ArrayElementOffset);
            Add(srcAddr, srcAddr, srcByteBase);
            Add(srcAddr, srcAddr, index);

            var byteValue = NewReg(4);
            Load(byteValue, srcAddr, 0, 1);

            var dstAddr = NewPtr();
            Lea(dstAddr, dst, NativeObjectModel.ArrayElementOffset);
            Add(dstAddr, dstAddr, index);
            Store(dstAddr, 0, byteValue, 1);

            AddI(index, index, 1);
            Jmp(loop);

            Mark(done);
            StoreRet(dst);
            Jmp(exit);

            Mark(error);
            var message = NewPtr();
            LeaData(message, _arrayBoundsMessage);
            CallRuntime(null, "PrintString", message);
            CallRuntime(null, "ExitProcess", C(4, 1));

            Mark(exit);
            EndFunction(_currentFunction!, 8);
        }

        // ------------------------------------------------------------------
        // ArrayCopyElements(src:8, dst:8, dstStart:4, elemSize:4) 鈫?void
        // C-闆嗗悎 spread锛氶泦鍚堣〃杈惧紡 `[..a, 4]` 杩愯鏃舵妸婧愭暟缁?a 鍏ㄩ儴鍏冪礌鎷疯礉鍒?
        // 鐩爣鏁扮粍鐨?[dstStart, dstStart+src.Length) 澶勩€俢ount 鐢?src.Length 鍐呴儴鎺ㄥ
        // 锛堥伩鍏嶇 5 鏍堝弬鈥斺€攛64 ABI 瀵勫瓨鍣ㄥ彧鍒?R9锛?+ 璧版爤锛? 鍙傚叏绋嬪瘎瀛樺櫒锛夈€?
        // 瀛楄妭绮掑害锛堝榻?SliceArray锛夛紝瓒婄晫鍗虫墦鍗?bounds 娑堟伅骞堕€€鍑恒€?
        // ------------------------------------------------------------------

        private void EmitArrayCopyElements()
        {
            var src = _args[0];
            var dst = _args[1];
            var dstStart = _args[2];
            var elementSize = _args[3];

            var srcLength = NewReg(4);
            Load(srcLength, src, NativeObjectModel.ArrayLengthOffset, 4);
            var dstLength = NewReg(4);
            Load(dstLength, dst, NativeObjectModel.ArrayLengthOffset, 4);

            var error = NewLabel();
            var done = NewLabel();
            var exit = NewLabel();

            // 瓒婄晫锛歞stStart >= 0 && dstStart + src.Length <= dst.Length
            Cmp(dstStart, 0);
            Jcc(LirCond.Less, error);
            var dstEnd = NewReg(4);
            Add(dstEnd, dstStart, srcLength);
            Cmp(dstEnd, dstLength);
            Jcc(LirCond.Greater, error);

            var totalBytes = NewReg(4);
            Imul(totalBytes, srcLength, elementSize);
            var dstByteBase = NewReg(4);
            Imul(dstByteBase, dstStart, elementSize);

            var index = NewReg(4);
            Mov(index, C(4, 0));
            var loop = NewLabel();

            Mark(loop);
            Cmp(index, totalBytes);
            Jcc(LirCond.GreaterOrEqual, done);

            var srcAddr = NewPtr();
            Lea(srcAddr, src, NativeObjectModel.ArrayElementOffset);
            Add(srcAddr, srcAddr, index);

            var byteValue = NewReg(4);
            Load(byteValue, srcAddr, 0, 1);

            var dstAddr = NewPtr();
            Lea(dstAddr, dst, NativeObjectModel.ArrayElementOffset);
            Add(dstAddr, dstAddr, dstByteBase);
            Add(dstAddr, dstAddr, index);
            Store(dstAddr, 0, byteValue, 1);

            AddI(index, index, 1);
            Jmp(loop);

            Mark(done);
            Jmp(exit);
            Mark(error);
            var message = NewPtr();
            LeaData(message, _arrayBoundsMessage);
            CallRuntime(null, "PrintString", message);
            CallRuntime(null, "ExitProcess", C(4, 1));

            Mark(exit);
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // ArrayBoundsCheck(index:4, length:4) 鈫?瓒婄晫鏃舵姤閿欓€€鍑?
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
        // BuildArgs() 鈫?ptr锛坰tring[]锛?
        // 缁?GetCommandLineW 璇诲彇鍛戒护琛岋紙UTF-16锛夛紝璺宠繃绋嬪簭鍚嶏紝鎸?MS 椋庢牸瑙ｆ瀽
        // 鍓╀綑鍙傛暟锛氱┖鐧斤紙绌烘牸/鍒惰〃绗︼級鍒嗛殧锛涘紩鍙峰寘瑁圭殑绌虹櫧涓嶅垎鍓诧紱寮曞彿鏈韩鍦?
        // 鍙傛暟鍐呭涓墺绂汇€傛瀯閫?string[]锛堝竷灞€鍚?NewArray锛夛紝澶辫触锛圤OM锛夎繑鍥?0銆?
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

            // ---- 瀹氫綅绋嬪簭鍚嶅悗鐨勭涓€涓弬鏁颁綅缃紙first锛?---
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

            // ---- pass 1: 璁℃暟锛坈ount锛?---
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

            // ---- 鍒嗛厤鏁扮粍 ----
            var elementSizeReg = C(4, elementSize);
            var arr = NewPtr();
            var splitArrayVt = NewPtr();
            LeaData(splitArrayVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            SetArg(0, count);
            SetArg(1, elementSizeReg);
            SetArg(2, splitArrayVt);
            Add(LirOpCode.Call, arr, LirOperand.Runtime("NewArray"), LirOperand.Constant(0));

            var oom = NewLabel();
            var finish = NewLabel();
            var done = NewLabel();
            Cmp(arr, 0);
            Jcc(LirCond.Equal, oom);

            // ---- pass 2: 閫愪釜鍙傛暟鏋勯€?string 骞跺啓鍏ユ暟缁?----
            Mov(p, first);
            var slot = NewPtr();
            var slotBase = NewPtr();
            Lea(slotBase, arr, NativeObjectModel.ArrayElementOffset);
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

            // ---- 鏋勯€犲瓧绗︿覆锛欰lloc(lenChars*2+12 瀵归綈 4)锛屽墺绂诲紩鍙锋嫹璐?----
            Mark(buildStr);
            var bytes = NewReg(4);
            Mov(bytes, lenChars);
            Shl(bytes, bytes, 1);
            AddI(bytes, bytes, NativeObjectModel.StringDataOffset);
            AddI(bytes, bytes, 3);
            And(bytes, bytes, C(4, 0xFFFFFFFC));
            var obj = NewPtr();
            CallRuntime(obj, "Alloc", bytes);
            Cmp(obj, 0);
            Jcc(LirCond.Equal, buildStrNext);

            var stringVt = NewPtr();
            LeaData(stringVt, NativeObjectModel.PseudoVTableKey("System.String"));
            Store(obj, 0, stringVt, _isX64 ? 8 : 4);
            Store(obj, NativeObjectModel.StringLengthOffset, lenChars, 4);
            var dst = NewPtr();
            Lea(dst, obj, NativeObjectModel.StringDataOffset);
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
        // Sha256Hash(data:8) 鈫?u8[] (32 bytes)
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
            Load(dataLen, data, NativeObjectModel.ArrayLengthOffset, 4);
            var dataPtr = NewPtr();
            Lea(dataPtr, data, NativeObjectModel.ArrayElementOffset);
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
            var u8ArrayVt = NewPtr();
            LeaData(u8ArrayVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            CallRuntime(arr, "NewArray", C(4, 32), C(4, 1), u8ArrayVt);
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
            Lea(arrDst, arr, NativeObjectModel.ArrayElementOffset);
            var arrOff = NewPtr();
            Mov(arrOff, ci);
            Add(arrDst, arrDst, arrOff);
            Store(arrDst, 0, tb, 1);
            AddI(buf32, buf32, 1);
            AddI(ci, ci, 1);
            Jmp(copyLoop);

            Mark(copyDone);

            // Free temp buffer
            SysCallDll(null, PlatformDllResolver.Kernel32, "VirtualFree", 3, false, buf32, zero32, C(4, 0x8000));

            StoreRet(arr);
            Jmp(doneLabel);

            Mark(errLabel);
            var zero = C(8, 0);
            StoreRet(zero);

            Mark(doneLabel);
            EndFunction(_currentFunction!, 8);
        }

        // LaunchProcess(path:8, args:8, workdir:8) 鈫?i32 exit code
        // CreateProcessW锛坘ernel32锛?0 鍙傦級+ WaitForSingleObject + GetExitCodeProcess锛泈orkdir 缁?lpCurrentDirectory銆?
        private void EmitLaunchProcess()
        {
            var errLabel = NewLabel();
            var doneLabel = NewLabel();

            var path = _args[0];
            var args = _args[1];
            var workdir = _args[2];

            // Build command line: path + " " + args
            var space = NewPtr();
            LeaData(space, _spaceString);
            var cmdConcat1 = NewPtr();
            CallRuntime(cmdConcat1, "Concat", path, space);
            var cmdLine = NewPtr();
            CallRuntime(cmdLine, "Concat", cmdConcat1, args);

            // Copy CO string chars to wchar buffer with null termination
            // CO string layout: [len:4][chars:2*len]
            var cmdWbuf = NewPtr();
            LeaData(cmdWbuf, _fileBuffer2);
            var strLen = NewReg(4);
            Load(strLen, cmdLine, NativeObjectModel.StringLengthOffset, 4);
            var di = NewReg(4);
            Const(di, 0);
            var copyLoop = NewLabel();
            var copyDone = NewLabel();
            Mark(copyLoop);
            Cmp(di, strLen);
            Jcc(LirCond.GreaterOrEqual, copyDone);
            // src = cmdLine + 12 + di*2
            var ch = NewReg(4);
            var srcOff = NewPtr();
            Mov(srcOff, di);
            Shl(srcOff, srcOff, 1);
            var srcAddr = NewPtr();
            Lea(srcAddr, cmdLine, NativeObjectModel.StringDataOffset);
            Add(srcAddr, srcAddr, srcOff);
            Load(ch, srcAddr, 0, 2);
            // dst = cmdWbuf + di*2
            var dstAddr = NewPtr();
            Lea(dstAddr, cmdWbuf, 0);
            var diBytes = NewPtr();
            Mov(diBytes, di);
            Shl(diBytes, diBytes, 1);
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

            // CreateProcessW锛歭pCurrentDirectory 鐩存帴缁?workdir锛堥潪绌衡啋WidePtrZ锛涚┖鈫扤ULL锛?
            var wdWide = NewPtr();
            var wdLen = NewReg(4);
            Load(wdLen, workdir, NativeObjectModel.StringLengthOffset, 4);
            var wdNull = NewLabel();
            var wdReady = NewLabel();
            Cmp(wdLen, 0);
            Jcc(LirCond.Equal, wdNull);
            Mov(wdWide, WidePtrZ(workdir));
            Jmp(wdReady);
            Mark(wdNull);
            Mov(wdWide, NullPtr());
            Mark(wdReady);

            // STARTUPINFOW锛坃fileBuffer3 鍩哄潃锛夛細娓呴浂鍐嶈 dwcbSize锛坸64=0x68銆亁86=0x44锛涚紦鍐查浂鍒濆鍖栦笉浣滃亣璁撅級
            var siPtr = NewPtr();
            LeaData(siPtr, _fileBuffer3);
            var siDwords = _isX64 ? 26 : 17;
            var clearI = NewReg(4);
            Const(clearI, 0);
            var clearLoop = NewLabel();
            var clearDone = NewLabel();
            Mark(clearLoop);
            Cmp(clearI, siDwords);
            Jcc(LirCond.GreaterOrEqual, clearDone);
            var clearOff = NewReg(4);
            Mov(clearOff, clearI);
            Shl(clearOff, clearOff, 2);
            var siDst = NewPtr();
            Mov(siDst, siPtr);
            Add(siDst, siDst, clearOff);
            Store(siDst, 0, C(4, 0), 4);
            AddI(clearI, clearI, 1);
            Jmp(clearLoop);
            Mark(clearDone);
            Store(siPtr, 0, C(4, _isX64 ? 0x68 : 0x44), 4);
            // PROCESS_INFORMATION锛堢揣闅?si锛涢€€鍑虹爜妲藉啀 + 0x10锛?
            var piPtr = NewPtr();
            LeaData(piPtr, _fileBuffer3);
            AddI(piPtr, piPtr, _isX64 ? 0x68 : 0x44);

            // CreateProcessW(NULL, cmdWbuf, NULL, NULL, FALSE, 0, NULL, wdWide, &si, &pi) 鈫?鍚屾绛夊緟 + 鍙栭€€鍑虹爜
            var ok = NewReg(4);
            SysCallDll(ok, PlatformDllResolver.Kernel32, "CreateProcessW", 10, false,
                NullPtr(), cmdWbuf, NullPtr(), NullPtr(), C(4, 0), C(4, 0), NullPtr(), wdWide, siPtr, piPtr);

            Cmp(ok, 0);
            Jcc(LirCond.Equal, errLabel);

            var ps = _isX64 ? 8 : 4;
            var hThread = NewPtr();
            Load(hThread, piPtr, ps, ps);
            SysCallDll(null, PlatformDllResolver.Kernel32, "CloseHandle", 1, false, hThread);

            var hProcess = NewPtr();
            Load(hProcess, piPtr, 0, ps);
            SysCallDll(null, PlatformDllResolver.Kernel32, "WaitForSingleObject", 2, false, hProcess, C(4, 0xFFFFFFFF));

            var exitPtr = NewPtr();
            LeaData(exitPtr, _fileBuffer3);
            AddI(exitPtr, exitPtr, _isX64 ? 0x78 : 0x54);
            SysCallDll(null, PlatformDllResolver.Kernel32, "GetExitCodeProcess", 2, false, hProcess, exitPtr);
            var exitCode = NewReg(4);
            Load(exitCode, exitPtr, 0, 4);
            SysCallDll(null, PlatformDllResolver.Kernel32, "CloseHandle", 1, false, hProcess);

            StoreRet(exitCode);
            Jmp(doneLabel);

            Mark(errLabel);
            var lastError = NewReg(4);
            SysCallDll(lastError, PlatformDllResolver.Kernel32, "GetLastError", 0, false);
            StoreRet(lastError);

            Mark(doneLabel);
            EndFunction(_currentFunction!, 4);
        }

    }
}
