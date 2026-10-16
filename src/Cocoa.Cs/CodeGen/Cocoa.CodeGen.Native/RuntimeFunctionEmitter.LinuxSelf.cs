using System;

using Cocoa.CodeGen.Native.Lir;

using Cocoa.CodeAnalysis;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// T3 N5b：Linux SelfIntrospect——扫描自身镜像（/proc/self/exe）ELF64 节表定位 .cocoa 并校验 "COCOA" 魔数，
    /// 返回 bool（对齐 Windows 内存自省语义；Linux 走文件读取，因节表区未映射进内存段）。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        // SelfIntrospect() → bool（1=自身 .cocoa 节存在且魔数正确）
        private void EmitSelfIntrospectLinux()
        {
            var fail = NewLabel();
            var done = NewLabel();
            var result = NewReg(4);
            Const(result, 0);

            // open /proc/self/exe
            var proc = NewPtr();
            LeaData(proc, _linuxProcExe);
            var fd = NewReg(8);
            LinuxSyscall(fd, __NR_open, 2, proc, C(8, O_RDONLY));
            Cmp(fd, 0);
            Jcc(LirCond.Less, done);

            // 读 ELF 头 0x40 → e_shoff(u64@0x28) e_shnum(u16@0x3C) e_shstrndx(u16@0x3E)
            var hdr = NewPtr();
            LeaData(hdr, _linuxStatBuf);
            LinuxSyscall(null, __NR_read, 3, fd, hdr, C(8, 0x40));
            var eShoff = NewReg(8);
            Load(eShoff, hdr, 0x28, 8);
            var eShnum = NewReg(4);
            Load(eShnum, hdr, 0x3C, 2);
            var shstrndx = NewReg(4);
            Load(shstrndx, hdr, 0x3E, 2);
            Cmp(eShnum, 0);
            Jcc(LirCond.LessOrEqual, done);

            // 定位 shstrtab 段头：lseek(e_shoff + shstrndx*64) → 读 64 → sh_offset(u64@0x18) sh_size(u64@0x20)
            var shstrHdrOff = NewReg(8);
            Mov(shstrHdrOff, shstrndx);
            Imul64(shstrHdrOff, shstrHdrOff, C(8, 64));
            Add(shstrHdrOff, shstrHdrOff, eShoff);
            LinuxSyscall(null, __NR_lseek, 3, fd, shstrHdrOff, C(8, 0));
            LinuxSyscall(null, __NR_read, 3, fd, hdr, C(8, 0x40));
            var shstrOff = NewReg(8);
            Load(shstrOff, hdr, 0x18, 8);
            var shstrSize = NewReg(8);
            Load(shstrSize, hdr, 0x20, 8);

            // 读 shstrtab 内容（_fileBuffer3）
            var shstrBuf = NewPtr();
            LeaData(shstrBuf, _fileBuffer3);
            LinuxSyscall(null, __NR_lseek, 3, fd, shstrOff, C(8, 0));
            LinuxSyscall(null, __NR_read, 3, fd, shstrBuf, shstrSize);

            // 遍历节表，按名找 .cocoa
            var i = NewReg(4);
            Const(i, 0);
            var loop = NewLabel();
            var next = NewLabel();
            var found = NewLabel();
            Mark(loop);
            Cmp(i, eShnum);
            Jcc(LirCond.GreaterOrEqual, fail);
            var secOff = NewReg(8);
            Mov(secOff, i);
            Imul64(secOff, secOff, C(8, 64));
            Add(secOff, secOff, eShoff);
            LinuxSyscall(null, __NR_lseek, 3, fd, secOff, C(8, 0));
            LinuxSyscall(null, __NR_read, 3, fd, hdr, C(8, 0x40));

            // 节名（sh_name→shstr 索引）比对 ".cocoa"（逐字节）
            var shName = NewReg(4);
            Load(shName, hdr, 0, 4);
            var nameAddr = NewPtr();
            Mov(nameAddr, shstrBuf);
            Add(nameAddr, nameAddr, shName);
            var c0 = NewReg(4);
            Load(c0, nameAddr, 0, 1);
            Cmp(c0, (int)'.');
            Jcc(LirCond.NotEqual, next);
            var c1 = NewReg(4);
            Load(c1, nameAddr, 1, 1);
            Cmp(c1, (int)'c');
            Jcc(LirCond.NotEqual, next);
            var c2 = NewReg(4);
            Load(c2, nameAddr, 2, 1);
            Cmp(c2, (int)'o');
            Jcc(LirCond.NotEqual, next);
            var c3 = NewReg(4);
            Load(c3, nameAddr, 3, 1);
            Cmp(c3, (int)'c');
            Jcc(LirCond.NotEqual, next);
            var c4 = NewReg(4);
            Load(c4, nameAddr, 4, 1);
            Cmp(c4, (int)'o');
            Jcc(LirCond.NotEqual, next);
            var c5 = NewReg(4);
            Load(c5, nameAddr, 5, 1);
            Cmp(c5, (int)'a');
            Jcc(LirCond.NotEqual, next);

            // 命中：读节前 5 字节校验 "COCOA"
            var secDataOff = NewReg(8);
            Load(secDataOff, hdr, 0x18, 8);
            var secSize = NewReg(8);
            Load(secSize, hdr, 0x20, 8);
            Cmp(secSize, 5);
            Jcc(LirCond.Less, next);
            LinuxSyscall(null, __NR_lseek, 3, fd, secDataOff, C(8, 0));
            var magicBuf = NewPtr();
            LeaData(magicBuf, _linuxInBuf);
            LinuxSyscall(null, __NR_read, 3, fd, magicBuf, C(8, 5));
            var m = NewReg(4);
            Load(m, magicBuf, 0, 4);
            Cmp(m, 0x4F434F43); // "COCO"
            Jcc(LirCond.NotEqual, next);
            var m5 = NewReg(4);
            Load(m5, magicBuf, 4, 1);
            Cmp(m5, (int)'A');
            Jcc(LirCond.NotEqual, next);
            Const(result, 1);
            Jmp(done);

            Mark(next);
            AddI(i, i, 1);
            Jmp(loop);

            Mark(fail);
            Const(result, 0);
            Mark(done);
            LinuxSyscall(null, __NR_close, 1, fd);
            StoreRet(result);
            EndFunction(_currentFunction!, 4);
        }
    }
}