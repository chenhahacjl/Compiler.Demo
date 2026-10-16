using System;

using Cocoa.CodeGen.Native.Lir;

using Cocoa.CodeAnalysis;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// T3 N2：Linux 文件/目录/环境运行时（裸 syscall）——UTF-8 路径/值经共享 marshal 往返，
    /// 对齐 Windows 实现的语义（FileMode/Access 映射、失败→空串/空数组/fd=0）。
    /// syscall 号与标志为 x86-64（open=2/stat=4/fstat=5/lseek=8/getcwd=79/chdir=80/mkdir=83/unlink=87/readlink=89）。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        private const int __NR_open = 2;
        private const int __NR_close = 3;
        private const int __NR_stat = 4;
        private const int __NR_fstat = 5;
        private const int __NR_lseek = 8;
        private const int __NR_getcwd = 79;
        private const int __NR_chdir = 80;
        private const int __NR_mkdir = 83;
        private const int __NR_unlink = 87;
        private const int __NR_readlink = 89;
        private const int __NR_fork = 57;
        private const int __NR_execve = 59;
        private const int __NR_waitpid = 61;

        // open 标志（x86-64）
        private const int O_RDONLY = 0;
        private const int O_WRONLY = 1;
        private const int O_RDWR = 2;
        private const int O_CREAT = 0x40;
        private const int O_EXCL = 0x80;
        private const int O_TRUNC = 0x200;
        private const int O_APPEND = 0x400;
        private const int Mode0644 = 0x1A4; // 0o644
        private const int Mode0777 = 0x1FF; // 0o777

        // struct stat（x86-64）关键字段偏移
        private const int StatModeOffset = 24; // mode_t
        private const int StatSizeOffset = 48; // off_t
        private const int S_IFMT = 0xF000;
        private const int S_IFDIR = 0x4000;

        // ------------------------------------------------------------------
        // 共享 marshal：CO 字符串（UTF-16 长前缀）→ NUL 结尾 UTF-8（BMP），返回 (缓冲指针, 字节数不含 NUL)
        // ------------------------------------------------------------------

        private (LirVirtualRegister Ptr, LirVirtualRegister ByteLen) EmitUtf8Z(LirVirtualRegister s, string bufferKey)
        {
            var dst = NewPtr();
            LeaData(dst, bufferKey);
            var src = NewPtr();
            Lea(src, s, NativeObjectModel.StringDataOffset);
            var len = NewReg(4);
            Load(len, s, NativeObjectModel.StringLengthOffset, 4);
            var byteLen = NewReg(4);
            Const(byteLen, 0);
            var i = NewReg(4);
            Const(i, 0);

            var loop = NewLabel();
            var multi = NewLabel();
            var three = NewLabel();
            var next = NewLabel();
            var done = NewLabel();

            Mark(loop);
            Cmp(i, len);
            Jcc(LirCond.GreaterOrEqual, done);
            var ch = NewReg(4);
            Load(ch, src, 0, 2);

            Cmp(ch, 0x80);
            Jcc(LirCond.AboveOrEqual, multi);
            Store(dst, 0, ch, 1);
            AddI(dst, dst, 1);
            AddI(byteLen, byteLen, 1);
            Jmp(next);

            Mark(multi);
            Cmp(ch, 0x800);
            Jcc(LirCond.AboveOrEqual, three);
            var b0 = NewReg(4);
            Mov(b0, ch);
            Shr(b0, b0, 6);
            Or(b0, b0, C(4, 0xC0));
            var b1 = NewReg(4);
            Mov(b1, ch);
            And(b1, b1, C(4, 0x3F));
            Or(b1, b1, C(4, 0x80));
            Store(dst, 0, b0, 1);
            Store(dst, 1, b1, 1);
            AddI(dst, dst, 2);
            AddI(byteLen, byteLen, 2);
            Jmp(next);

            Mark(three);
            var t0 = NewReg(4);
            Mov(t0, ch);
            Shr(t0, t0, 12);
            Or(t0, t0, C(4, 0xE0));
            var t1 = NewReg(4);
            Mov(t1, ch);
            Shr(t1, t1, 6);
            And(t1, t1, C(4, 0x3F));
            Or(t1, t1, C(4, 0x80));
            var t2 = NewReg(4);
            Mov(t2, ch);
            And(t2, t2, C(4, 0x3F));
            Or(t2, t2, C(4, 0x80));
            Store(dst, 0, t0, 1);
            Store(dst, 1, t1, 1);
            Store(dst, 2, t2, 1);
            AddI(dst, dst, 3);
            AddI(byteLen, byteLen, 3);

            Mark(next);
            AddI(src, src, 2);
            AddI(i, i, 1);
            Jmp(loop);

            Mark(done);
            var nul = NewReg(4);
            Const(nul, 0);
            Store(dst, 0, nul, 1);
            var bufPtr = NewPtr();
            LeaData(bufPtr, bufferKey);
            return (bufPtr, byteLen);
        }

        /// <summary>UTF-8 字节区 → CO 字符串（解码进 _inputBuffer 后构建对象）。Alloc 失败跳 failLabel。</summary>
        private LirVirtualRegister EmitStringFromUtf8Bytes(LirVirtualRegister buf, LirVirtualRegister byteLen, int failLabel)
        {
            var chars = NewReg(4);
            Const(chars, 0);
            var pos = NewReg(4);
            Const(pos, 0);
            var loop = NewLabel();
            var dOne = NewLabel();
            var dTwo = NewLabel();
            var dEnd = NewLabel();

            Mark(loop);
            Cmp(pos, byteLen);
            Jcc(LirCond.GreaterOrEqual, dEnd);
            var bp = NewPtr();
            Mov(bp, buf);
            Add(bp, bp, pos);
            var b = NewReg(4);
            Load(b, bp, 0, 1);
            Cmp(b, 0x80);
            Jcc(LirCond.Below, dOne);
            Cmp(b, 0xE0);
            Jcc(LirCond.Below, dTwo);

            var c3 = NewReg(4);
            Mov(c3, b);
            And(c3, c3, C(4, 0x0F));
            Shl(c3, c3, 12);
            var e1 = NewReg(4);
            Load(e1, bp, 1, 1);
            And(e1, e1, C(4, 0x3F));
            Shl(e1, e1, 6);
            Or(c3, c3, e1);
            var e2 = NewReg(4);
            Load(e2, bp, 2, 1);
            And(e2, e2, C(4, 0x3F));
            Or(c3, c3, e2);
            StoreInputCharLinux(c3, chars);
            AddI(pos, pos, 3);
            Jmp(loop);

            Mark(dTwo);
            var c2 = NewReg(4);
            Mov(c2, b);
            And(c2, c2, C(4, 0x1F));
            Shl(c2, c2, 6);
            var d1 = NewReg(4);
            Load(d1, bp, 1, 1);
            And(d1, d1, C(4, 0x3F));
            Or(c2, c2, d1);
            StoreInputCharLinux(c2, chars);
            AddI(pos, pos, 2);
            Jmp(loop);

            Mark(dOne);
            StoreInputCharLinux(b, chars);
            AddI(pos, pos, 1);
            Jmp(loop);

            Mark(dEnd);
            return EmitBuildStringObject(chars, failLabel);
        }

        /// <summary>基于 _inputBuffer 内的 UTF-16 单元数构建 CO String 对象（AlignWindows 尾部；无 strip）。</summary>
        private LirVirtualRegister EmitBuildStringObject(LirVirtualRegister chars, int failLabel)
        {
            var size = NewReg(4);
            Mov(size, chars);
            AddI(size, size, 1);
            Shr(size, size, 1);
            Shl(size, size, 2);
            AddI(size, size, NativeObjectModel.StringDataOffset);

            var obj = NewPtr();
            CallRuntime(obj, "Alloc", size);
            Cmp(obj, 0);
            Jcc(LirCond.Equal, failLabel);
            var stringVt = NewPtr();
            LeaData(stringVt, NativeObjectModel.PseudoVTableKey("System.String"));
            Store(obj, 0, stringVt, _isX64 ? 8 : 4);
            Store(obj, NativeObjectModel.StringLengthOffset, chars, 4);

            var count = NewReg(4);
            Mov(count, chars);
            AddI(count, count, 1);
            Shr(count, count, 1);
            var dst = NewPtr();
            Lea(dst, obj, NativeObjectModel.StringDataOffset);
            var buf = NewPtr();
            LeaData(buf, _inputBuffer);
            CallRuntime(null, "CopyChars", dst, buf, count);
            return obj;
        }

        // ------------------------------------------------------------------
        // FileExists / DirectoryExists（stat）
        // ------------------------------------------------------------------

        private void EmitFileExistsLinux()
        {
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            var sb = NewPtr();
            LeaData(sb, _linuxStatBuf);
            var r = NewReg(4);
            LinuxSyscall(r, __NR_stat, 2, pp, sb);
            var res = NewReg(4);
            Cmp(r, 0);
            Setcc(res, LirCond.Equal);
            StoreRet(res);
            EndFunction(_currentFunction!, 4);
        }

        private void EmitDirectoryExistsLinux()
        {
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            var sb = NewPtr();
            LeaData(sb, _linuxStatBuf);
            var r = NewReg(4);
            LinuxSyscall(r, __NR_stat, 2, pp, sb);
            var res = NewReg(4);
            Const(res, 0);
            var done = NewLabel();
            Cmp(r, 0);
            Jcc(LirCond.NotEqual, done);
            var mode = NewReg(4);
            Load(mode, sb, StatModeOffset, 4);
            And(mode, mode, C(4, S_IFMT));
            Cmp(mode, C(4, S_IFDIR));
            Setcc(res, LirCond.Equal);
            Mark(done);
            StoreRet(res);
            EndFunction(_currentFunction!, 4);
        }

        private void EmitCreateDirectoryLinux()
        {
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            LinuxSyscall(null, __NR_mkdir, 2, pp, C(8, Mode0777));
            EndFunction(_currentFunction!, 0);
        }

        private void EmitFileDeleteLinux()
        {
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            LinuxSyscall(null, __NR_unlink, 1, pp);
            EndFunction(_currentFunction!, 0);
        }

        private void EmitSetCurrentDirectoryLinux()
        {
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            LinuxSyscall(null, __NR_chdir, 1, pp);
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // FileCopy（open src → create dst → 循环拷贝）
        // ------------------------------------------------------------------

        private void EmitFileCopyLinux()
        {
            var done = NewLabel();
            var doneCopy = NewLabel();
            var closeSrcDst = NewLabel();
            var closeSrc = NewLabel();

            var (src, _) = EmitUtf8Z(_args[0], _fileBuffer);
            var (dst, _) = EmitUtf8Z(_args[1], _fileBuffer2);

            var fds = NewReg(8);
            LinuxSyscall(fds, __NR_open, 2, src, C(8, O_RDONLY));
            Cmp(fds, 0);
            Jcc(LirCond.Less, done);

            var fdd = NewReg(8);
            LinuxSyscall(fdd, __NR_open, 3, dst, C(8, O_WRONLY | O_CREAT | O_TRUNC), C(8, Mode0644));
            Cmp(fdd, 0);
            Jcc(LirCond.Less, closeSrc);

            var ioBuf = NewPtr();
            LeaData(ioBuf, _linuxOutBuf);
            var n = NewReg(4);
            var copyLoop = NewLabel();
            Mark(copyLoop);
            LinuxSyscall(n, __NR_read, 3, fds, ioBuf, C(8, 0x4000));
            Cmp(n, 0);
            Jcc(LirCond.LessOrEqual, doneCopy);
            LinuxSyscall(null, __NR_write, 3, fdd, ioBuf, n);
            Jmp(copyLoop);

            Mark(doneCopy);
            LinuxSyscall(null, __NR_close, 1, fdd);
            Mark(closeSrc);
            LinuxSyscall(null, __NR_close, 1, fds);
            Mark(done);
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // FileReadAllBytes / FileWriteAllBytes
        // ------------------------------------------------------------------

        private void EmitFileReadAllBytesLinux()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var closeFail = NewLabel();
            var doneRead = NewLabel();
            var result = NewPtr();

            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            var fd = NewReg(8);
            LinuxSyscall(fd, __NR_open, 2, pp, C(8, O_RDONLY));
            Cmp(fd, 0);
            Jcc(LirCond.Less, fail);

            var sb = NewPtr();
            LeaData(sb, _linuxStatBuf);
            LinuxSyscall(null, __NR_fstat, 2, fd, sb);
            var total = NewReg(4);
            Load(total, sb, StatSizeOffset, 4);

            var arrayVt = NewPtr();
            LeaData(arrayVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            var arr = NewPtr();
            CallRuntime(arr, "NewArray", total, C(4, 1), arrayVt);
            Cmp(arr, 0);
            Jcc(LirCond.Equal, closeFail);

            var arrBase = NewPtr();
            Lea(arrBase, arr, NativeObjectModel.ArrayElementOffset);
            var off = NewReg(4);
            Const(off, 0);
            var read = NewReg(4);
            var readLoop = NewLabel();
            Mark(readLoop);
            Cmp(off, total);
            Jcc(LirCond.GreaterOrEqual, doneRead);
            var remain = NewReg(4);
            Mov(remain, total);
            Sub(remain, remain, off);
            var dst = NewPtr();
            Mov(dst, arrBase);
            Add(dst, dst, off);
            LinuxSyscall(read, __NR_read, 3, fd, dst, remain);
            Cmp(read, 0);
            Jcc(LirCond.LessOrEqual, doneRead);
            Add(off, off, read);
            Jmp(readLoop);

            Mark(doneRead);
            LinuxSyscall(null, __NR_close, 1, fd);
            Mov(result, arr);
            Jmp(done);

            Mark(closeFail);
            LinuxSyscall(null, __NR_close, 1, fd);
            Mark(fail);
            var emptyVt = NewPtr();
            LeaData(emptyVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            CallRuntime(result, "NewArray", C(4, 0), C(4, 1), emptyVt);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        private void EmitFileWriteAllBytesLinux()
        {
            var done = NewLabel();
            var data = _args[1];
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            var fd = NewReg(8);
            LinuxSyscall(fd, __NR_open, 3, pp, C(8, O_WRONLY | O_CREAT | O_TRUNC), C(8, Mode0644));
            Cmp(fd, 0);
            Jcc(LirCond.Less, done);
            var len = NewReg(4);
            Load(len, data, NativeObjectModel.ArrayLengthOffset, 4);
            var src = NewPtr();
            Lea(src, data, NativeObjectModel.ArrayElementOffset);
            LinuxSyscall(null, __NR_write, 3, fd, src, len);
            LinuxSyscall(null, __NR_close, 1, fd);
            Mark(done);
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // Handle 族：open/Size/Seek/Tell/Close/Read/Write
        // ------------------------------------------------------------------

        private void EmitFileOpenHandleLinux()
        {
            var done = NewLabel();
            var ok = NewLabel();
            var (pp, _) = EmitUtf8Z(_args[0], _fileBuffer);
            var mode = NewReg(4);
            Mov(mode, _args[1]);
            var mf = NewReg(4);
            Const(mf, 0);

            // mode 1..6 → open 标志（对齐 .NET FileMode）
            var m2 = NewLabel();
            var m3 = NewLabel();
            var m4 = NewLabel();
            var m5 = NewLabel();
            var m6 = NewLabel();
            var modeReady = NewLabel();
            Cmp(mode, 1);
            Jcc(LirCond.NotEqual, m2);
            Const(mf, O_CREAT | O_EXCL);
            Jmp(modeReady);
            Mark(m2);
            Cmp(mode, 2);
            Jcc(LirCond.NotEqual, m3);
            Const(mf, O_CREAT | O_TRUNC);
            Jmp(modeReady);
            Mark(m3);
            Cmp(mode, 4);
            Jcc(LirCond.NotEqual, m4);
            Const(mf, O_CREAT);
            Jmp(modeReady);
            Mark(m4);
            Cmp(mode, 5);
            Jcc(LirCond.NotEqual, m5);
            Const(mf, O_TRUNC);
            Jmp(modeReady);
            Mark(m5);
            Cmp(mode, 6);
            Jcc(LirCond.NotEqual, m6);
            Const(mf, O_WRONLY | O_CREAT | O_APPEND);
            Mark(m6);
            Mark(modeReady);

            var access = NewReg(4);
            Mov(access, _args[2]);
            var af = NewReg(4);
            Const(af, O_RDONLY);
            var a2 = NewLabel();
            var a3 = NewLabel();
            var accReady = NewLabel();
            Cmp(access, 1);
            Jcc(LirCond.NotEqual, a2);
            Const(af, O_RDONLY);
            Jmp(accReady);
            Mark(a2);
            Cmp(access, 2);
            Jcc(LirCond.NotEqual, a3);
            Const(af, O_WRONLY);
            Jmp(accReady);
            Mark(a3);
            Const(af, O_RDWR);
            Mark(accReady);

            var flags = NewReg(4);
            Or(flags, mf, af);
            var fd = NewReg(8);
            LinuxSyscall(fd, __NR_open, 3, pp, flags, C(8, Mode0644));

            // Append(6)：打开后定位到文件尾（对齐 .NET FileStream）
            Cmp(mode, 6);
            Jcc(LirCond.NotEqual, ok);
            LinuxSyscall(null, __NR_lseek, 3, fd, C(8, 0), C(8, 2));
            Mark(ok);

            // 失败 → 0（对齐 Windows INVALID_HANDLE → 0）
            Cmp(fd, 0);
            Jcc(LirCond.GreaterOrEqual, done);
            var zero = NewReg(8);
            Const(zero, 0);
            Mov(fd, zero);
            Mark(done);
            StoreRet(fd);
            EndFunction(_currentFunction!, 8);
        }

        private void EmitFileSizeHandleLinux()
        {
            var sb = NewPtr();
            LeaData(sb, _linuxStatBuf);
            LinuxSyscall(null, __NR_fstat, 2, _args[0], sb);
            var sz = NewReg(8);
            Load(sz, sb, StatSizeOffset, 8);
            StoreRet(sz);
            EndFunction(_currentFunction!, 8);
        }

        private void EmitFileSeekHandleLinux()
        {
            LinuxSyscall(null, __NR_lseek, 3, _args[0], _args[1], _args[2]);
            EndFunction(_currentFunction!, 0);
        }

        private void EmitFileTellHandleLinux()
        {
            var pos = NewReg(8);
            LinuxSyscall(pos, __NR_lseek, 3, _args[0], C(8, 0), C(8, 1));
            StoreRet(pos);
            EndFunction(_currentFunction!, 8);
        }

        private void EmitFileCloseHandleLinux()
        {
            LinuxSyscall(null, __NR_close, 1, _args[0]);
            EndFunction(_currentFunction!, 0);
        }

        private void EmitFileReadHandleLinux()
        {
            var dst = NewPtr();
            Lea(dst, _args[1], NativeObjectModel.ArrayElementOffset);
            Add(dst, dst, _args[2]);
            var read = NewReg(4);
            LinuxSyscall(read, __NR_read, 3, _args[0], dst, _args[3]);
            var ok = NewLabel();
            Cmp(read, 0);
            Jcc(LirCond.GreaterOrEqual, ok);
            Const(read, 0);
            Mark(ok);
            StoreRet(read);
            EndFunction(_currentFunction!, 4);
        }

        private void EmitFileWriteHandleLinux()
        {
            var src = NewPtr();
            Lea(src, _args[1], NativeObjectModel.ArrayElementOffset);
            Add(src, src, _args[2]);
            LinuxSyscall(null, __NR_write, 3, _args[0], src, _args[3]);
            EndFunction(_currentFunction!, 0);
        }

        // ------------------------------------------------------------------
        // 环境/路径：GetEnvironmentVariable / GetCurrentDirectory / GetExecutablePath
        // ------------------------------------------------------------------

        private void EmitGetCurrentDirectoryLinux()
        {
            var missing = NewLabel();
            var done = NewLabel();
            var result = NewPtr();
            var buf = NewPtr();
            LeaData(buf, _fileBuffer);
            var n = NewReg(4);
            LinuxSyscall(n, __NR_getcwd, 2, buf, C(8, 0x4000));
            Cmp(n, 1);
            Jcc(LirCond.LessOrEqual, missing);
            var byteLen = NewReg(4);
            Mov(byteLen, n);
            AddI(byteLen, byteLen, -1);
            var obj = EmitStringFromUtf8Bytes(buf, byteLen, missing);
            Mov(result, obj);
            Jmp(done);
            Mark(missing);
            var empty = NewPtr();
            LeaData(empty, _emptyString);
            Mov(result, empty);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        private void EmitGetExecutablePathLinux()
        {
            var missing = NewLabel();
            var done = NewLabel();
            var result = NewPtr();
            var proc = NewPtr();
            LeaData(proc, _linuxProcExe);
            var buf = NewPtr();
            LeaData(buf, _fileBuffer);
            var n = NewReg(4);
            LinuxSyscall(n, __NR_readlink, 3, proc, buf, C(8, 0x4000));
            Cmp(n, 1);
            Jcc(LirCond.LessOrEqual, missing);
            var byteLen = NewReg(4);
            Mov(byteLen, n);
            AddI(byteLen, byteLen, -1);
            var obj = EmitStringFromUtf8Bytes(buf, byteLen, missing);
            Mov(result, obj);
            Jmp(done);
            Mark(missing);
            var empty = NewPtr();
            LeaData(empty, _emptyString);
            Mov(result, empty);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        private void EmitGetEnvironmentVariableLinux()
        {
            var missing = NewLabel();
            var done = NewLabel();
            var result = NewPtr();

            var (nameBuf, nameLen) = EmitUtf8Z(_args[0], _fileBuffer2);

            var envPath = NewPtr();
            LeaData(envPath, _linuxProcEnviron);
            var fd = NewReg(8);
            LinuxSyscall(fd, __NR_open, 2, envPath, C(8, O_RDONLY));
            Cmp(fd, 0);
            Jcc(LirCond.Less, missing);

            var envBuf = NewPtr();
            LeaData(envBuf, _fileBuffer);
            var en = NewReg(4);
            LinuxSyscall(en, __NR_read, 3, fd, envBuf, C(8, 0x8000));
            LinuxSyscall(null, __NR_close, 1, fd);
            Cmp(en, 0);
            Jcc(LirCond.LessOrEqual, missing);

            // 扫描 entry（NUL 分隔 "NAME=VALUE"）
            var pos = NewReg(4);
            Const(pos, 0);
            var scanLoop = NewLabel();
            var findDone = NewLabel();
            var noEq = NewLabel();
            var cmpFail = NewLabel();
            var matchName = NewLabel();
            var scanNext = NewLabel();

            Mark(scanLoop);
            Cmp(pos, en);
            Jcc(LirCond.GreaterOrEqual, missing);

            var i = NewReg(4);
            Mov(i, pos);
            var eq = NewReg(4);
            Const(eq, -1);
            var findEq = NewLabel();
            Mark(findEq);
            Cmp(i, en);
            Jcc(LirCond.GreaterOrEqual, findDone);
            var ch = NewReg(4);
            var chPtr = NewPtr();
            Mov(chPtr, envBuf);
            Add(chPtr, chPtr, i);
            Load(ch, chPtr, 0, 1);
            var findNul = NewLabel();
            Cmp(ch, 0);
            Jcc(LirCond.Equal, findDone);
            Cmp(ch, 0x3D);
            Jcc(LirCond.NotEqual, findNul);
            Mov(eq, i);
            Mark(findNul);
            AddI(i, i, 1);
            Jmp(findEq);

            Mark(findDone);
            Cmp(eq, 0);
            Jcc(LirCond.Less, noEq);
            // 名字长度匹配
            Cmp(eq, nameLen);
            Jcc(LirCond.NotEqual, noEq);
            // 逐字节比较名字
            var ci = NewReg(4);
            Const(ci, 0);
            var cmpLoop = NewLabel();
            var cmpOk = NewLabel();
            Mark(cmpLoop);
            Cmp(ci, eq);
            Jcc(LirCond.GreaterOrEqual, cmpOk);
            var eb = NewReg(4);
            var ebPtr = NewPtr();
            Mov(ebPtr, envBuf);
            Add(ebPtr, ebPtr, pos);
            Add(ebPtr, ebPtr, ci);
            Load(eb, ebPtr, 0, 1);
            var nb = NewReg(4);
            var nbPtr = NewPtr();
            Mov(nbPtr, nameBuf);
            Add(nbPtr, nbPtr, ci);
            Load(nb, nbPtr, 0, 1);
            Cmp(eb, nb);
            Jcc(LirCond.NotEqual, cmpFail);
            AddI(ci, ci, 1);
            Jmp(cmpLoop);
            Mark(cmpOk);
            Jmp(matchName);

            Mark(cmpFail);
            Mark(noEq);
            // 跳至下一 entry（i = NUL 位置，pos = i+1）
            Mov(pos, i);
            AddI(pos, pos, 1);
            Jmp(scanLoop);

            Mark(matchName);
            // value = envBuf[pos + eq + 1 .. i)
            var valueLen = NewReg(4);
            Mov(valueLen, i);
            Sub(valueLen, valueLen, pos);
            AddI(valueLen, valueLen, -1);
            Sub(valueLen, valueLen, eq);
            var vp = NewPtr();
            Mov(vp, envBuf);
            Add(vp, vp, pos);
            Add(vp, vp, eq);
            AddI(vp, vp, 1);
            var obj = EmitStringFromUtf8Bytes(vp, valueLen, missing);
            Mov(result, obj);
            Jmp(done);

            Mark(missing);
            var empty = NewPtr();
            LeaData(empty, _emptyString);
            Mov(result, empty);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // ------------------------------------------------------------------
        // BuildArgs() → string[]：/proc/self/cmdline（NUL 分隔）跳过程序名，构造参数数组
        // ------------------------------------------------------------------

        private void EmitBuildArgsLinux()
        {
            var oom = NewLabel();
            var done = NewLabel();
            var countDone = NewLabel();
            var finish = NewLabel();

            var cmdPath = NewPtr();
            LeaData(cmdPath, _linuxProcCmdline);
            var fd = NewReg(8);
            LinuxSyscall(fd, __NR_open, 2, cmdPath, C(8, O_RDONLY));
            Cmp(fd, 0);
            Jcc(LirCond.Less, oom);

            var buf = NewPtr();
            LeaData(buf, _fileBuffer);
            var en = NewReg(4);
            LinuxSyscall(en, __NR_read, 3, fd, buf, C(8, 0x8000));
            LinuxSyscall(null, __NR_close, 1, fd);
            Cmp(en, 0);
            Jcc(LirCond.LessOrEqual, oom);

            // 计数（NUL 分隔段；首个为程序名，跳过）
            var pos = NewReg(4);
            Const(pos, 0);
            var count = NewReg(4);
            Const(count, 0);
            var firstSeg = NewReg(4);
            Const(firstSeg, 1);

            var countLoop = NewLabel();
            var countSeg = NewLabel();
            var countNext = NewLabel();
            var countArg = NewLabel();
            Mark(countLoop);
            Cmp(pos, en);
            Jcc(LirCond.GreaterOrEqual, countDone);
            var i = NewReg(4);
            Mov(i, pos);
            var advance = NewLabel();
            Mark(advance);
            Cmp(i, en);
            Jcc(LirCond.GreaterOrEqual, countSeg);
            var cb = NewReg(4);
            var cbPtr = NewPtr();
            Mov(cbPtr, buf);
            Add(cbPtr, cbPtr, i);
            Load(cb, cbPtr, 0, 1);
            Cmp(cb, 0);
            Jcc(LirCond.Equal, countSeg);
            AddI(i, i, 1);
            Jmp(advance);

            Mark(countSeg);
            var segLen = NewReg(4);
            Mov(segLen, i);
            Sub(segLen, segLen, pos);
            Cmp(segLen, 0);
            Jcc(LirCond.LessOrEqual, countNext);
            Cmp(firstSeg, 0);
            Jcc(LirCond.Equal, countArg);
            Const(firstSeg, 0);
            Jmp(countNext);
            Mark(countArg);
            AddI(count, count, 1);
            Mark(countNext);
            Mov(pos, i);
            AddI(pos, pos, 1);
            Jmp(countLoop);

            Mark(countDone);
            var arrayVt = NewPtr();
            LeaData(arrayVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            var arr = NewPtr();
            CallRuntime(arr, "NewArray", count, C(4, 8), arrayVt);
            Cmp(arr, 0);
            Jcc(LirCond.Equal, oom);

            // 第二遍：构建各参数 String 并写入数组槽
            Const(pos, 0);
            Const(firstSeg, 1);
            var slot = NewPtr();
            Lea(slot, arr, NativeObjectModel.ArrayElementOffset);

            var buildLoop = NewLabel();
            var buildSeg = NewLabel();
            var buildNext = NewLabel();
            var buildArg = NewLabel();
            var objDone = NewLabel();
            var skipStore = NewLabel();
            Mark(buildLoop);
            Cmp(pos, en);
            Jcc(LirCond.GreaterOrEqual, finish);
            Mov(i, pos);
            var buildAdv = NewLabel();
            Mark(buildAdv);
            Cmp(i, en);
            Jcc(LirCond.GreaterOrEqual, buildSeg);
            var bb = NewReg(4);
            var bbPtr = NewPtr();
            Mov(bbPtr, buf);
            Add(bbPtr, bbPtr, i);
            Load(bb, bbPtr, 0, 1);
            Cmp(bb, 0);
            Jcc(LirCond.Equal, buildSeg);
            AddI(i, i, 1);
            Jmp(buildAdv);

            Mark(buildSeg);
            var bLen = NewReg(4);
            Mov(bLen, i);
            Sub(bLen, bLen, pos);
            Cmp(bLen, 0);
            Jcc(LirCond.LessOrEqual, buildNext);
            Cmp(firstSeg, 0);
            Jcc(LirCond.Equal, buildArg);
            Const(firstSeg, 0);
            Jmp(buildNext);
            Mark(buildArg);
            var vp = NewPtr();
            Mov(vp, buf);
            Add(vp, vp, pos);
            var obj = EmitStringFromUtf8Bytes(vp, bLen, skipStore);
            Jmp(objDone);
            Mark(skipStore);
            Const(obj, 0);
            Mark(objDone);
            Store(slot, 0, obj, 8);
            Lea(slot, slot, 8);
            Mark(buildNext);
            Mov(pos, i);
            AddI(pos, pos, 1);
            Jmp(buildLoop);

            Mark(finish);
            StoreRet(arr);
            Jmp(done);

            Mark(oom);
            var zero = C(8, 0);
            StoreRet(zero);
            Mark(done);
            EndFunction(_currentFunction!, 8);
        }

        // ------------------------------------------------------------------
        // LaunchProcess(path, args, workdir) → i32：拼命令行 → 空格分词 argv →
        // fork → 子进程 chdir+execve；父进程 waitpid 取 WEXITSTATUS（对齐 Windows 同步等待语义）
        // ------------------------------------------------------------------

        private void EmitLaunchProcessLinux()
        {
            var childLabel = NewLabel();
            var forkFail = NewLabel();
            var noChdir = NewLabel();
            var done = NewLabel();

            // 命令行 = path + " " + args
            var space = NewPtr();
            LeaData(space, _spaceString);
            var cmdConcat = NewPtr();
            CallRuntime(cmdConcat, "Concat", _args[0], space);
            var cmdLine = NewPtr();
            CallRuntime(cmdLine, "Concat", cmdConcat, _args[1]);

            // cmdLine → UTF-8 C 串（tokenBuf=_fileBuffer3，tokenLen=字节数）；workdir → UTF-8（_fileBuffer2）
            var (tokenBuf, tokenLen) = EmitUtf8Z(cmdLine, _fileBuffer3);
            var wdLen = NewReg(4);
            Load(wdLen, _args[2], NativeObjectModel.StringLengthOffset, 4);
            var wd = NewPtr();
            var wdSkip = NewLabel();
            var wdReady = NewLabel();
            Cmp(wdLen, 0);
            Jcc(LirCond.Equal, wdSkip);
            var (wdBuf, _) = EmitUtf8Z(_args[2], _fileBuffer2);
            Mov(wd, wdBuf);
            Jmp(wdReady);
            Mark(wdSkip);
            Const(wd, 0);
            Mark(wdReady);

            // 分词：空格/Tab 分隔，NUL 替换定界符 → argv[]（指针数组 @ _fileBuffer）
            var arrBuf = NewPtr();
            LeaData(arrBuf, _fileBuffer);
            var arrPtr = NewPtr();
            Mov(arrPtr, arrBuf);
            var pos = NewReg(4);
            Const(pos, 0);
var scanLoop = NewLabel();
            var scanDone = NewLabel();
            var scanNext = NewLabel();
            var word = NewLabel();
            var wordEnd = NewLabel();
            var notSpace = NewLabel();

            Mark(scanLoop);
            Cmp(pos, tokenLen);
            Jcc(LirCond.GreaterOrEqual, scanDone);
            var chb = NewReg(4);
            var bp = NewPtr();
            Mov(bp, tokenBuf);
            Add(bp, bp, pos);
            Load(chb, bp, 0, 1);
            Cmp(chb, 0);
            Jcc(LirCond.Equal, scanDone);
            Cmp(chb, 0x20);
            Jcc(LirCond.Equal, scanNext);
            Cmp(chb, 0x09);
            Jcc(LirCond.Equal, scanNext);
            Jmp(word);
            Mark(scanNext);
            AddI(pos, pos, 1);
            Jmp(scanLoop);

            Mark(word);
            var wordAddr = NewPtr();
            Mov(wordAddr, tokenBuf);
            Add(wordAddr, wordAddr, pos);
            Store(arrPtr, 0, wordAddr, 8);
            Lea(arrPtr, arrPtr, 8);
            Mark(wordEnd);
            Cmp(pos, tokenLen);
            Jcc(LirCond.GreaterOrEqual, scanDone);
            var nbp = NewPtr();
            Mov(nbp, tokenBuf);
            Add(nbp, nbp, pos);
            Load(chb, nbp, 0, 1);
            Cmp(chb, 0);
            Jcc(LirCond.Equal, scanDone);
            Cmp(chb, 0x20);
            Jcc(LirCond.NotEqual, notSpace);
            Cmp(chb, 0x09);
            Jcc(LirCond.NotEqual, notSpace);
            // 空格/Tab 定界符 → NUL，并越过它继续扫描
            var nulByte = NewReg(4);
            Const(nulByte, 0);
            Store(nbp, 0, nulByte, 1);
            AddI(pos, pos, 1);
            Jmp(scanLoop);
            Mark(notSpace);
            AddI(pos, pos, 1);
            Jmp(wordEnd);

            Mark(scanDone);
            // argv 终止 NULL 指针
            var nulPtr = NewReg(8);
            Const(nulPtr, 0);
            Store(arrPtr, 0, nulPtr, 8);

            // fork
            var pid = NewReg(8);
            LinuxSyscall(pid, __NR_fork, 0);
            Cmp(pid, 0);
            Jcc(LirCond.Equal, childLabel);
            Cmp(pid, 0);
            Jcc(LirCond.Less, forkFail);

            // 父进程：waitpid → WEXITSTATUS
            var statusReg = NewReg(4);
            var statusPtr = NewPtr();
            LeaSlot(statusPtr, statusReg);
            LinuxSyscall(null, __NR_waitpid, 3, pid, statusPtr, C(8, 0));
            Load(statusReg, statusPtr, 0, 4);
            And(statusReg, statusReg, C(4, 0xFF00));
            Shr(statusReg, statusReg, 8);
            StoreRet(statusReg);
            Jmp(done);

            // 子进程：chdir(workdir) + execve(argv[0], argv, NULL)；失败 _exit(127)
            Mark(childLabel);
            Cmp(wd, 0);
            Jcc(LirCond.Equal, noChdir);
            LinuxSyscall(null, __NR_chdir, 1, wd);
            Mark(noChdir);
            LinuxSyscall(null, __NR_execve, 3, tokenBuf, arrBuf, C(8, 0));
            LinuxSyscall(null, __NR_exit_group, 1, C(8, 127));

            Mark(forkFail);
            var failCode = C(8, -1);
            StoreRet(failCode);
            Mark(done);
            EndFunction(_currentFunction!, 4);
        }
    }
}