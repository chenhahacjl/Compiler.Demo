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
        // StringFromChars(chars:8 char[]) 鈫?string锛坋-G7 鈶锛夛細
        // char[] 甯冨眬 [vtable][len@8][鍏冪礌鍖篅16锛? 瀛楄妭瀵归綈锛宑har 2 瀛楄妭锛塢 鈫?CO 涓?[vtable][len@8][chars@12]銆?
        // 鐩存帴澶嶅埗 UTF-16 鏁版嵁鍖猴紙AllocStringFromBuf 椤哄甫琛?null 缁撳熬锛夈€?
        private void EmitStringFromChars()
        {
            var arr = _args[0];
            var arrLen = NewReg(4);
            Load(arrLen, arr, NativeObjectModel.ArrayLengthOffset, 4);
            var lenBytes = NewReg(4);
            Mov(lenBytes, arrLen);
            Shl(lenBytes, lenBytes, 1);
            var src = NewPtr();
            Lea(src, arr, NativeObjectModel.ArrayElementOffset);
            var result = NewPtr();
            CallRuntime(result, "AllocStringFromBuf", src, lenBytes);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // FileExists(path:8) 鈫?bool锛欸etFileAttributesW != INVALID_FILE_ATTRIBUTES(0xFFFFFFFF)
        private void EmitFileExists()
        {
            var p = WidePtrZ(_args[0]);
            var attrs = NewReg(4);
            SysCall(attrs, "GetFileAttributesW", 1, p);
            var result = NewReg(4);
            Cmp(attrs, -1);
            Setcc(result, LirCond.NotEqual);
            StoreRet(result);
            EndFunction(_currentFunction!, 4);
        }

        // DirectoryExists(path:8) 鈫?bool锛歛ttrs != INVALID 涓斿甫 FILE_ATTRIBUTE_DIRECTORY(0x10)
        private void EmitDirectoryExists()
        {
            var p = WidePtrZ(_args[0]);
            var attrs = NewReg(4);
            SysCall(attrs, "GetFileAttributesW", 1, p);
            var isDir = NewReg(4);
            Cmp(attrs, -1);
            Setcc(isDir, LirCond.NotEqual);
            var dirFlag = NewReg(4);
            And(dirFlag, attrs, C(4, 0x10));
            Cmp(dirFlag, 0);
            var result = NewReg(4);
            Setcc(result, LirCond.NotEqual);
            And(result, isDir, result);
            StoreRet(result);
            EndFunction(_currentFunction!, 4);
        }

        // CreateDirectory(path:8) 鈫?void锛歐in32 CreateDirectoryW锛堟垚鍔?宸插瓨鍦ㄥ潎蹇界暐鈥斺€擝CL 骞傜瓑锛?
        private void EmitCreateDirectory()
        {
            var p = WidePtrZ(_args[0]);
            SysCallDll(null, PlatformDllResolver.Kernel32, "CreateDirectoryW", 2, false, p, NullPtr());
            EndFunction(_currentFunction!, 0);
        }

        // FileDelete(path:8) 鈫?void
        private void EmitFileDelete()
        {
            var p = WidePtrZ(_args[0]);
            SysCall(null, "DeleteFileW", 1, p);
            EndFunction(_currentFunction!, 0);
        }

        // FileCopy(src:8, dst:8) 鈫?void锛欳opyFileW锛堟亽瑕嗙洊鐩爣锛夛紱src/dst 鐢ㄤ笉鍚岀紦鍐查槻瑕嗙洊
        private void EmitFileCopy()
        {
            var src = WidePtrZ(_args[0]);
            var dst = WidePtrZInto(_args[1], _fileBuffer2);
            SysCall(null, "CopyFileW", 3, src, dst, C(4, 1));
            EndFunction(_currentFunction!, 0);
        }

        // GetEnvironmentVariable(name:8) 鈫?string锛欸etEnvironmentVariableW 涓ら樁娈碉紱鏈懡鍚?鈫?绌轰覆锛堝搴?Evaluator ?? ""锛夈€?
        private void EmitGetEnvironmentVariable()
        {
            var p = WidePtrZ(_args[0]);
            var missing = NewLabel();
            var done = NewLabel();
            var need = NewReg(4);
            SysCall(need, "GetEnvironmentVariableW", 3, p, NullPtr(), C(4, 0));
            Cmp(need, 0);
            Jcc(LirCond.Equal, missing);
            var needBytes = NewReg(4);
            Imul(needBytes, need, C(4, 2));
            var buf = NewPtr();
            CallRuntime(buf, "Alloc", needBytes);
            Cmp(buf, 0);
            Jcc(LirCond.Equal, missing);
            var actual = NewReg(4);
            SysCall(actual, "GetEnvironmentVariableW", 3, p, buf, need);
            Cmp(actual, 0);
            Jcc(LirCond.Equal, missing);
            var lenBytes = NewReg(4);
            Imul(lenBytes, actual, C(4, 2));
            var result = NewPtr();
            CallRuntime(result, "AllocStringFromBuf", buf, lenBytes);
            Jmp(done);
            Mark(missing);
            var empty = NewPtr();
            LeaData(empty, _emptyString);
            Mov(result, empty);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // GetCurrentDirectory() 鈫?string锛欸etCurrentDirectoryW 涓ら樁娈碉紱澶辫触 鈫?绌轰覆
        private void EmitGetCurrentDirectory()
        {
            var empty = NewLabel();
            var done = NewLabel();
            var need = NewReg(4);
            SysCall(need, "GetCurrentDirectoryW", 2, C(4, 0), NullPtr());
            Cmp(need, 0);
            Jcc(LirCond.Equal, empty);
            var buf = NewPtr();
            LeaData(buf, _fileBuffer);
            var actual = NewReg(4);
            SysCall(actual, "GetCurrentDirectoryW", 2, need, buf);
            Cmp(actual, 0);
            Jcc(LirCond.Equal, empty);
            var lenBytes = NewReg(4);
            Imul(lenBytes, actual, C(4, 2));
            var result = NewPtr();
            CallRuntime(result, "AllocStringFromBuf", buf, lenBytes);
            Jmp(done);
            Mark(empty);
            var emptyStr = NewPtr();
            LeaData(emptyStr, _emptyString);
            Mov(result, emptyStr);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // GetExecutablePath() 鈫?string锛欸etModuleFileNameW锛涚紦鍐叉埅鏂?澶辫触 鈫?绌轰覆
        private void EmitGetExecutablePath()
        {
            var empty = NewLabel();
            var done = NewLabel();
            var buf = NewPtr();
            LeaData(buf, _fileBuffer);
            var cap = C(4, 0x4000);
            var actual = NewReg(4);
            SysCall(actual, "GetModuleFileNameW", 3, NullPtr(), buf, cap);
            Cmp(actual, 0);
            Jcc(LirCond.Equal, empty);
            Cmp(actual, cap);
            Jcc(LirCond.GreaterOrEqual, empty);
            var lenBytes = NewReg(4);
            Imul(lenBytes, actual, C(4, 2));
            var result = NewPtr();
            CallRuntime(result, "AllocStringFromBuf", buf, lenBytes);
            Jmp(done);
            Mark(empty);
            var emptyStr = NewPtr();
            LeaData(emptyStr, _emptyString);
            Mov(result, emptyStr);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // SetCurrentDirectory(path:8) 鈫?void
        private void EmitSetCurrentDirectory()
        {
            var p = WidePtrZ(_args[0]);
            SysCall(null, "SetCurrentDirectoryW", 1, p);
            EndFunction(_currentFunction!, 0);
        }

        // FileReadAllBytes(path:8) 鈫?u8[]锛欳reateFile(GENERIC_READ, OPEN_EXISTING) 鈫?GetFileSizeEx 鈫?
        // NewArray(size, 1) 鈫?ReadFile 寰幆鎷峰叆 arr+8 鈫?CloseHandle銆傚け璐?缂烘枃浠?鈫?0 闀垮害鏁扮粍銆?
        private void EmitFileReadAllBytes()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var result = NewPtr();

            var pb = WidePtrZ(_args[0]);
            var fp = NewPtr();
            SysCallDll(fp, PlatformDllResolver.Kernel32, "CreateFileW", 7, false, pb, C(4, 0x80000000), C(4, 3), NullPtr(), C(4, 3), C(4, 0x80), NullPtr());
            EmitCheckInvalidHandle(fp, fail);

            var size = NewReg(8);
            Const(size, 0);
            var sizePtr = NewPtr();
            LeaSlot(sizePtr, size);
            SysCallDll(null, PlatformDllResolver.Kernel32, "GetFileSizeEx", 2, false, fp, sizePtr);
            var total = NewReg(4);
            Load(total, sizePtr, 0, 4); // 浣?32 浣嶅嵆鏂囦欢澶у皬锛?4GB锛?

            var arr = NewPtr();
            var readBytesVt = NewPtr();
            LeaData(readBytesVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            CallRuntime(arr, "NewArray", total, C(4, 1), readBytesVt);
            Cmp(arr, 0);
            Jcc(LirCond.Equal, fail);

            var arrBase = NewPtr();
            Lea(arrBase, arr, NativeObjectModel.ArrayElementOffset);
            var off = NewReg(4);
            Const(off, 0);
            var read = NewReg(4);
            Const(read, 0);
            var readPtr = NewPtr();
            LeaSlot(readPtr, read);
            var readLoop = NewLabel();
            var readDone = NewLabel();
            Mark(readLoop);
            Cmp(off, total);
            Jcc(LirCond.GreaterOrEqual, readDone);
            var remain = NewReg(4);
            Mov(remain, total);
            Sub(remain, remain, off);
            var dst = NewPtr();
            Mov(dst, arrBase);
            Add(dst, dst, off);
            SysCallDll(null, PlatformDllResolver.Kernel32, "ReadFile", 5, false, fp, dst, remain, readPtr, NullPtr());
            Load(read, readPtr, 0, 4); // 鍏变韩缂撳啿 鈫?read 瀵勫瓨鍣ㄦЫ
            Cmp(read, 0);
            Jcc(LirCond.LessOrEqual, readDone);
            Add(off, off, read);
            Jmp(readLoop);
            Mark(readDone);
            SysCallDll(null, PlatformDllResolver.Kernel32, "CloseHandle", 1, false, fp);
            Mov(result, arr);
            Jmp(done);

            Mark(fail);
            var emptyBytesVt = NewPtr();
            LeaData(emptyBytesVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            CallRuntime(result, "NewArray", C(4, 0), C(4, 1), emptyBytesVt);
            Jmp(done);

            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // FileWriteAllBytes(path:8, data:8) 鈫?void锛欳reateFile(GENERIC_WRITE, CREATE_ALWAYS) 鈫?WriteFile(data+8) 鈫?CloseHandle
        private void EmitFileWriteAllBytes()
        {
            var fail = NewLabel();
            var data = _args[1];
            var pb = WidePtrZ(_args[0]);
            var fp = NewPtr();
            SysCallDll(fp, PlatformDllResolver.Kernel32, "CreateFileW", 7, false, pb, C(4, 0x40000000), C(4, 3), NullPtr(), C(4, 2), C(4, 0x80), NullPtr());
            EmitCheckInvalidHandle(fp, fail);

            var len = NewReg(4);
            Load(len, data, NativeObjectModel.ArrayLengthOffset, 4);
            var src = NewPtr();
            Lea(src, data, NativeObjectModel.ArrayElementOffset);
            var written = NewReg(4);
            Const(written, 0);
            var writtenPtr = NewPtr();
            LeaSlot(writtenPtr, written);
            SysCallDll(null, PlatformDllResolver.Kernel32, "WriteFile", 5, false, fp, src, len, writtenPtr, NullPtr());
            SysCallDll(null, PlatformDllResolver.Kernel32, "CloseHandle", 1, false, fp);

            Mark(fail);
            EndFunction(_currentFunction!, 0);
        }

        // FileOpenHandle(path:8, mode:4, access:4, share:4) 鈫?i64锛歮ode 1..6 瀵归綈 .NET FileMode锛涘け璐?鈫?0
        // Win32 CreateFileW锛堝榻?.NET FileStream 搴曞眰锛夛紱GENERIC_READ|WRITE=0xC0000000, GENERIC_WRITE=0x40000000,
        // FILE_SHARE_READ|WRITE=3, OPEN_EXISTING=3, CREATE_ALWAYS=2, OPEN_ALWAYS=4, FILE_ATTRIBUTE_NORMAL=0x80
        private void EmitFileOpenHandle()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var result = NewReg(8);
            Const(result, 0);

            // mode(1-6) 鈫?disposition(1,2,3,4,5,4)锛涗粎 Append(6) 鈫?OPEN_ALWAYS(4)锛屽榻?.NET FileMode
            var mode = NewReg(4);
            Mov(mode, _args[1]);
            var disposition = NewReg(4);
            Mov(disposition, mode);
            var setAppend = NewLabel();
            var appendReady = NewLabel();
            Cmp(mode, 6);
            Jcc(LirCond.Equal, setAppend);
            Jmp(appendReady);
            Mark(setAppend);
            Const(disposition, 4);
            Mark(appendReady);

            // access(1-3) 鈫?fAccess锛欸ENERIC_READ=0x80000000 | GENERIC_WRITE=0x40000000锛堝榻?.NET FileAccess锛?
            var access = NewReg(4);
            Mov(access, _args[2]);
            var fAccess = NewReg(4);
            Const(fAccess, 0);
            var readBit = NewReg(4);
            And(readBit, access, C(4, 1));
            var skipRead = NewLabel();
            Cmp(readBit, 1);
            Jcc(LirCond.NotEqual, skipRead);
            var gRead = NewReg(4);
            Const(gRead, 0x80000000);
            Or(fAccess, fAccess, gRead);
            Mark(skipRead);
            var writeBit = NewReg(4);
            And(writeBit, access, C(4, 2));
            var skipWrite = NewLabel();
            Cmp(writeBit, 2);
            Jcc(LirCond.NotEqual, skipWrite);
            var gWrite = NewReg(4);
            Const(gWrite, 0x40000000);
            Or(fAccess, fAccess, gWrite);
            Mark(skipWrite);

            var pw = WidePtrZ(_args[0]);
            var shareMode = NewReg(4);
            Mov(shareMode, _args[3]);
            var fp = NewPtr();
            SysCallDll(fp, PlatformDllResolver.Kernel32, "CreateFileW", 7, false, pw, fAccess, shareMode, NullPtr(), disposition, C(4, 0x80), NullPtr());
            EmitCheckInvalidHandle(fp, fail); // INVALID_HANDLE_VALUE
            Mov(result, fp);

            // Append(6)锛氭墦寮€鍚庡畾浣嶅埌鏂囦欢灏撅紙瀵归綈 .NET FileStream锛?
            var seekEnd = NewLabel();
            var skipSeek = NewLabel();
            Cmp(mode, 6);
            Jcc(LirCond.Equal, seekEnd);
            Jmp(skipSeek);
            Mark(seekEnd);
            var zero = NewReg(8);
            Const(zero, 0);
            var origin2 = NewReg(4);
            Const(origin2, 2);
            SysCallDll(null, PlatformDllResolver.Kernel32, "SetFilePointerEx", 4, false, fp, zero, NullPtr(), origin2);
            Mark(skipSeek);

            Jmp(done);
            Mark(fail);
            Const(result, 0);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // FileSizeHandle(h) 鈫?鏂囦欢瀛楄妭鏁帮紙GetFileSizeEx锛涗笉鏀瑰姩鏂囦欢浣嶇疆鈥斺€斿榻?.NET FileStream.Length锛?
        private void EmitFileSizeHandle()
        {
            var fp = NewPtr();
            Mov(fp, _args[0]);
            var size = NewReg(8);
            Const(size, 0);
            var sizePtr = NewPtr();
            LeaSlot(sizePtr, size);
            SysCallDll(null, PlatformDllResolver.Kernel32, "GetFileSizeEx", 2, false, fp, sizePtr);
            Load(size, sizePtr, 0, 8); // 鍏变韩缂撳啿 鈫?size 瀵勫瓨鍣ㄦЫ
            StoreRet(size);
            EndFunction(_currentFunction!, 8);
        }

        // FileSeekHandle(h, offset:i64, origin) 鈫?瀹氫綅锛沷rigin 0=Begin 1=Current 2=End
        // x86锛欼64 鍙傛暟鍙紶 32 浣嶏紙EDX锛夛紝鍙栦綆 32 浣嶇敤 SetFilePointer锛?2 浣?dist锛?GB 鍐咃級锛泋64 鐢?SetFilePointerEx锛堝畬鏁?64 浣嶏級
        private void EmitFileSeekHandle()
        {
            if (_isX64)
            {
                SysCallDll(null, PlatformDllResolver.Kernel32, "SetFilePointerEx", 4, false, _args[0], _args[1], NullPtr(), _args[2]);
            }
            else
            {
                var offLo = NewReg(4);
                Mov(offLo, _args[1]); // offset 浣?32 浣嶏紙x86 鍙紶浣?32锛?
                SysCallDll(null, PlatformDllResolver.Kernel32, "SetFilePointer", 4, false, _args[0], offLo, NullPtr(), _args[2]);
            }
            EndFunction(_currentFunction!, 0);
        }

        // FileTellHandle(h) 鈫?褰撳墠浣嶇疆锛泋86 鐢?SetFilePointer锛?2 浣?pos锛? 楂?4 娓?0 鏋勯€?i64 杩斿洖
        private void EmitFileTellHandle()
        {
            var fp = NewPtr();
            Mov(fp, _args[0]);
            if (_isX64)
            {
                var pos = NewReg(8);
                Const(pos, 0);
                var posPtr = NewPtr();
                LeaSlot(posPtr, pos);
                SysCallDll(null, PlatformDllResolver.Kernel32, "SetFilePointerEx", 4, false, fp, C(8, 0), posPtr, C(4, 1));
                Load(pos, posPtr, 0, 8); // 鍏变韩缂撳啿 鈫?pos 瀵勫瓨鍣ㄦЫ
                StoreRet(pos);
            }
            else
            {
                // x86锛歋etFilePointer 杩斿洖 DWORD 鏂颁綅缃紙瑙勯伩鍏变韩缂撳啿杈撳嚭妲斤級
                var pos = NewReg(4);
                SysCallDll(pos, PlatformDllResolver.Kernel32, "SetFilePointer", 4, false, fp, C(4, 0), NullPtr(), C(4, 1));
                var res = NewReg(8);
                Const(res, 0); // 鍙屾Ы娓?0锛坸86锛?
                Mov(res, pos); // 浣?4 = pos锛岄珮 4 = 0
                StoreRet(res);
            }
            EndFunction(_currentFunction!, 8);
        }

        // FileReadHandle(h, data:u8[], start, count) 鈫?瀹為檯璇诲叆瀛楄妭锛圧eadFile锛?read 杈撳嚭妲斤級
        private void EmitFileReadHandle()
        {
            var fp = NewPtr();
            Mov(fp, _args[0]);
            var dst = NewPtr();
            Lea(dst, _args[1], NativeObjectModel.ArrayElementOffset);
            Add(dst, dst, _args[2]);
            var read = NewReg(4);
            Const(read, 0);
            var readPtr = NewPtr();
            LeaSlot(readPtr, read);
            SysCallDll(null, PlatformDllResolver.Kernel32, "ReadFile", 5, false, fp, dst, _args[3], readPtr, NullPtr());
            Load(read, readPtr, 0, 4); // 鍏变韩缂撳啿 鈫?read 瀵勫瓨鍣ㄦЫ
            StoreRet(read);
            EndFunction(_currentFunction!, 4);
        }

        // FileWriteHandle(h, data:u8[], start, count)锛圵riteFile锛?written 杈撳嚭妲斤級
        private void EmitFileWriteHandle()
        {
            var fp = NewPtr();
            Mov(fp, _args[0]);
            var src = NewPtr();
            Lea(src, _args[1], NativeObjectModel.ArrayElementOffset);
            Add(src, src, _args[2]);
            var written = NewReg(4);
            Const(written, 0);
            var writtenPtr = NewPtr();
            LeaSlot(writtenPtr, written);
            SysCallDll(null, PlatformDllResolver.Kernel32, "WriteFile", 5, false, fp, src, _args[3], writtenPtr, NullPtr());
            EndFunction(_currentFunction!, 0);
        }

        // Win32 INVALID_HANDLE_VALUE 鍒ゆ柇锛氬け璐ヨ繑鍥?-1锛泋86 32 浣?-1锛?xFFFFFFFF锛夊瓨 Addr 妲藉悗楂?4 瀛楄妭娓?0锛?
        // 8 瀛楄妭姣旇緝浼氳鍒ゆ垚鍔?鈫?鍙栦綆 4 瀛楄妭 == 0xFFFFFFFF锛坸64 澶辫触 64 浣?-1 鐨勪綆 4 瀛楄妭鍚屾牱涓哄叏 1锛夈€?
        private void EmitCheckInvalidHandle(LirVirtualRegister fp, int failLabel)
        {
            var lo = NewReg(4);
            Mov(lo, fp);
            Cmp(lo, C(4, -1));
            Jcc(LirCond.Equal, failLabel);
        }

        // 鎸囬拡绫诲瀷 NULL锛坸86 4 瀛楄妭 / x64 8 瀛楄妭锛夆€斺€斾笉寰楃敤 C(8,0)锛圛64锛夛細x86 鍘?8 瀛楄妭瀵艰嚧鍙傛暟閿欎綅/鏍堜笉骞宠　
        private LirVirtualRegister NullPtr()
        {
            var p = NewPtr();
            Const(p, 0);
            return p;
        }

    }
}
