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
            // StringFromChars(chars:8 char[]) → string（e-G7 ③a）：
            // char[] 布局 [len:4][元素区@8（8 字节对齐，char 2 字节）] → CO 串 [len:4][chars:2×len]。
            // 直接复制 UTF-16 数据区（AllocStringFromBuf 顺带补 null 结尾）。
            private void EmitStringFromChars()
            {
                var arr = _args[0];
                var arrLen = NewReg(4);
                Load(arrLen, arr, 0, 4);
                var lenBytes = NewReg(4);
                Mov(lenBytes, arrLen);
                Shl(lenBytes, lenBytes, 1);
                var src = NewPtr();
                Lea(src, arr, 8);
                var result = NewPtr();
                CallRuntime(result, "AllocStringFromBuf", src, lenBytes);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            // FileExists(path:8) → bool：GetFileAttributesW != INVALID_FILE_ATTRIBUTES(0xFFFFFFFF)
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

            // DirectoryExists(path:8) → bool：attrs != INVALID 且带 FILE_ATTRIBUTE_DIRECTORY(0x10)
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

            // CreateDirectory(path:8) → void：Win32 CreateDirectoryW（成功/已存在均忽略——BCL 幂等）
            private void EmitCreateDirectory()
            {
                var p = WidePtrZ(_args[0]);
                SysCallDll(null, "kernel32.dll", "CreateDirectoryW", 2, false, p, NullPtr());
                EndFunction(_currentFunction!, 0);
            }

            // FileDelete(path:8) → void
            private void EmitFileDelete()
            {
                var p = WidePtrZ(_args[0]);
                SysCall(null, "DeleteFileW", 1, p);
                EndFunction(_currentFunction!, 0);
            }

            // FileCopy(src:8, dst:8) → void：CopyFileW（恒覆盖目标）；src/dst 用不同缓冲防覆盖
            private void EmitFileCopy()
            {
                var src = WidePtrZ(_args[0]);
                var dst = WidePtrZInto(_args[1], _fileBuffer2);
                SysCall(null, "CopyFileW", 3, src, dst, C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            // GetEnvironmentVariable(name:8) → string：GetEnvironmentVariableW 两阶段；未命名 → 空串（对应 Evaluator ?? ""）。
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

            // GetCurrentDirectory() → string：GetCurrentDirectoryW 两阶段；失败 → 空串
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

            // GetExecutablePath() → string：GetModuleFileNameW；缓冲截断/失败 → 空串
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

            // SetCurrentDirectory(path:8) → void
            private void EmitSetCurrentDirectory()
            {
                var p = WidePtrZ(_args[0]);
                SysCall(null, "SetCurrentDirectoryW", 1, p);
                EndFunction(_currentFunction!, 0);
            }

            // FileReadAllBytes(path:8) → u8[]：CreateFile(GENERIC_READ, OPEN_EXISTING) → GetFileSizeEx →
            // NewArray(size, 1) → ReadFile 循环拷入 arr+8 → CloseHandle。失败/缺文件 → 0 长度数组。
            private void EmitFileReadAllBytes()
            {
                var fail = NewLabel();
                var done = NewLabel();
                var result = NewPtr();

                var pb = WidePtrZ(_args[0]);
                var fp = NewPtr();
                SysCallDll(fp, "kernel32.dll", "CreateFileW", 7, false, pb, C(4, 0x80000000), C(4, 3), NullPtr(), C(4, 3), C(4, 0x80), NullPtr());
                EmitCheckInvalidHandle(fp, fail);

                var size = NewReg(8);
                Const(size, 0);
                var sizePtr = NewPtr();
                LeaSlot(sizePtr, size);
                SysCallDll(null, "kernel32.dll", "GetFileSizeEx", 2, false, fp, sizePtr);
                var total = NewReg(4);
                Load(total, sizePtr, 0, 4); // 低 32 位即文件大小（<4GB）

                var arr = NewPtr();
                CallRuntime(arr, "NewArray", total, C(4, 1));
                Cmp(arr, 0);
                Jcc(LirCond.Equal, fail);

                var arrBase = NewPtr();
                Lea(arrBase, arr, 8);
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
                SysCallDll(null, "kernel32.dll", "ReadFile", 5, false, fp, dst, remain, readPtr, NullPtr());
                Load(read, readPtr, 0, 4); // 共享缓冲 → read 寄存器槽
                Cmp(read, 0);
                Jcc(LirCond.LessOrEqual, readDone);
                Add(off, off, read);
                Jmp(readLoop);
                Mark(readDone);
                SysCallDll(null, "kernel32.dll", "CloseHandle", 1, false, fp);
                Mov(result, arr);
                Jmp(done);

                Mark(fail);
                CallRuntime(result, "NewArray", C(4, 0), C(4, 1));
                Jmp(done);

                Mark(done);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            // FileWriteAllBytes(path:8, data:8) → void：CreateFile(GENERIC_WRITE, CREATE_ALWAYS) → WriteFile(data+8) → CloseHandle
            private void EmitFileWriteAllBytes()
            {
                var fail = NewLabel();
                var data = _args[1];
                var pb = WidePtrZ(_args[0]);
                var fp = NewPtr();
                SysCallDll(fp, "kernel32.dll", "CreateFileW", 7, false, pb, C(4, 0x40000000), C(4, 3), NullPtr(), C(4, 2), C(4, 0x80), NullPtr());
                EmitCheckInvalidHandle(fp, fail);

                var len = NewReg(4);
                Load(len, data, 0, 4);
                var src = NewPtr();
                Lea(src, data, 8);
                var written = NewReg(4);
                Const(written, 0);
                var writtenPtr = NewPtr();
                LeaSlot(writtenPtr, written);
                SysCallDll(null, "kernel32.dll", "WriteFile", 5, false, fp, src, len, writtenPtr, NullPtr());
                SysCallDll(null, "kernel32.dll", "CloseHandle", 1, false, fp);

                Mark(fail);
                EndFunction(_currentFunction!, 0);
            }

            // FileOpenHandle(path:8, mode:4, access:4, share:4) → i64：mode 1..6 对齐 .NET FileMode；失败 → 0
            // Win32 CreateFileW（对齐 .NET FileStream 底层）；GENERIC_READ|WRITE=0xC0000000, GENERIC_WRITE=0x40000000,
            // FILE_SHARE_READ|WRITE=3, OPEN_EXISTING=3, CREATE_ALWAYS=2, OPEN_ALWAYS=4, FILE_ATTRIBUTE_NORMAL=0x80
            private void EmitFileOpenHandle()
            {
                var fail = NewLabel();
                var done = NewLabel();
                var result = NewReg(8);
                Const(result, 0);

                // mode(1-6) → disposition(1,2,3,4,5,4)；仅 Append(6) → OPEN_ALWAYS(4)，对齐 .NET FileMode
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

                // access(1-3) → fAccess：GENERIC_READ=0x80000000 | GENERIC_WRITE=0x40000000（对齐 .NET FileAccess）
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
                SysCallDll(fp, "kernel32.dll", "CreateFileW", 7, false, pw, fAccess, shareMode, NullPtr(), disposition, C(4, 0x80), NullPtr());
                EmitCheckInvalidHandle(fp, fail); // INVALID_HANDLE_VALUE
                Mov(result, fp);

                // Append(6)：打开后定位到文件尾（对齐 .NET FileStream）
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
                SysCallDll(null, "kernel32.dll", "SetFilePointerEx", 4, false, fp, zero, NullPtr(), origin2);
                Mark(skipSeek);

                Jmp(done);
                Mark(fail);
                Const(result, 0);
                Mark(done);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            // FileSizeHandle(h) → 文件字节数（GetFileSizeEx；不改动文件位置——对齐 .NET FileStream.Length）
            private void EmitFileSizeHandle()
            {
                var fp = NewPtr();
                Mov(fp, _args[0]);
                var size = NewReg(8);
                Const(size, 0);
                var sizePtr = NewPtr();
                LeaSlot(sizePtr, size);
                SysCallDll(null, "kernel32.dll", "GetFileSizeEx", 2, false, fp, sizePtr);
                Load(size, sizePtr, 0, 8); // 共享缓冲 → size 寄存器槽
                StoreRet(size);
                EndFunction(_currentFunction!, 8);
            }

            // FileSeekHandle(h, offset:i64, origin) → 定位；origin 0=Begin 1=Current 2=End
            // x86：I64 参数只传 32 位（EDX），取低 32 位用 SetFilePointer（32 位 dist，4GB 内）；x64 用 SetFilePointerEx（完整 64 位）
            private void EmitFileSeekHandle()
            {
                if (_isX64)
                {
                    SysCallDll(null, "kernel32.dll", "SetFilePointerEx", 4, false, _args[0], _args[1], NullPtr(), _args[2]);
                }
                else
                {
                    var offLo = NewReg(4);
                    Mov(offLo, _args[1]); // offset 低 32 位（x86 只传低 32）
                    SysCallDll(null, "kernel32.dll", "SetFilePointer", 4, false, _args[0], offLo, NullPtr(), _args[2]);
                }
                EndFunction(_currentFunction!, 0);
            }

            // FileTellHandle(h) → 当前位置；x86 用 SetFilePointer（32 位 pos）+ 高 4 清 0 构造 i64 返回
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
                    SysCallDll(null, "kernel32.dll", "SetFilePointerEx", 4, false, fp, C(8, 0), posPtr, C(4, 1));
                    Load(pos, posPtr, 0, 8); // 共享缓冲 → pos 寄存器槽
                    StoreRet(pos);
                }
                else
                {
                    // x86：SetFilePointer 返回 DWORD 新位置（规避共享缓冲输出槽）
                    var pos = NewReg(4);
                    SysCallDll(pos, "kernel32.dll", "SetFilePointer", 4, false, fp, C(4, 0), NullPtr(), C(4, 1));
                    var res = NewReg(8);
                    Const(res, 0); // 双槽清 0（x86）
                    Mov(res, pos); // 低 4 = pos，高 4 = 0
                    StoreRet(res);
                }
                EndFunction(_currentFunction!, 8);
            }

            // FileReadHandle(h, data:u8[], start, count) → 实际读入字节（ReadFile；&read 输出槽）
            private void EmitFileReadHandle()
            {
                var fp = NewPtr();
                Mov(fp, _args[0]);
                var dst = NewPtr();
                Lea(dst, _args[1], 8);
                Add(dst, dst, _args[2]);
                var read = NewReg(4);
                Const(read, 0);
                var readPtr = NewPtr();
                LeaSlot(readPtr, read);
                SysCallDll(null, "kernel32.dll", "ReadFile", 5, false, fp, dst, _args[3], readPtr, NullPtr());
                Load(read, readPtr, 0, 4); // 共享缓冲 → read 寄存器槽
                StoreRet(read);
                EndFunction(_currentFunction!, 4);
            }

            // FileWriteHandle(h, data:u8[], start, count)（WriteFile；&written 输出槽）
            private void EmitFileWriteHandle()
            {
                var fp = NewPtr();
                Mov(fp, _args[0]);
                var src = NewPtr();
                Lea(src, _args[1], 8);
                Add(src, src, _args[2]);
                var written = NewReg(4);
                Const(written, 0);
                var writtenPtr = NewPtr();
                LeaSlot(writtenPtr, written);
                SysCallDll(null, "kernel32.dll", "WriteFile", 5, false, fp, src, _args[3], writtenPtr, NullPtr());
                EndFunction(_currentFunction!, 0);
            }

            // Win32 INVALID_HANDLE_VALUE 判断：失败返回 -1；x86 32 位 -1（0xFFFFFFFF）存 Addr 槽后高 4 字节清 0，
            // 8 字节比较会误判成功 → 取低 4 字节 == 0xFFFFFFFF（x64 失败 64 位 -1 的低 4 字节同样为全 1）。
            private void EmitCheckInvalidHandle(LirVirtualRegister fp, int failLabel)
            {
                var lo = NewReg(4);
                Mov(lo, fp);
                Cmp(lo, C(4, -1));
                Jcc(LirCond.Equal, failLabel);
            }

            // 指针类型 NULL（x86 4 字节 / x64 8 字节）——不得用 C(8,0)（I64）：x86 压 8 字节导致参数错位/栈不平衡
            private LirVirtualRegister NullPtr()
            {
                var p = NewPtr();
                Const(p, 0);
                return p;
            }

        }
    }
}
