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
            // StringFromChars(chars:8 char[]) �?string�?e-G7 ③a）：
            // char[] 布局 [len:4][元素区@8�? 字节对齐，char 2 字节）] �?CO �?[len:4][chars:2×len]�?
            // 直接复制 UTF-16 数据区（AllocStringFromBuf 顺带�?null 结尾）�?
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

            // FileExists(path:8) �?bool：GetFileAttributesW != INVALID_FILE_ATTRIBUTES(0xFFFFFFFF)
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

            // DirectoryExists(path:8) �?bool：attrs != INVALID 且带 FILE_ATTRIBUTE_DIRECTORY(0x10)
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

            // FileDelete(path:8) �?void
            private void EmitFileDelete()
            {
                var p = WidePtrZ(_args[0]);
                SysCall(null, "DeleteFileW", 1, p);
                EndFunction(_currentFunction!, 0);
            }

            // FileCopy(src:8, dst:8) �?void：CopyFileW（恒覆盖目标）；src/dst 用不同缓冲防覆盖
            private void EmitFileCopy()
            {
                var src = WidePtrZ(_args[0]);
                var dst = WidePtrZInto(_args[1], _fileBuffer2);
                SysCall(null, "CopyFileW", 3, src, dst, C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            // GetEnvironmentVariable(name:8) �?string：GetEnvironmentVariableW 两阶段；未命�?�?空串（对�?Evaluator ?? ""�?
            private void EmitGetEnvironmentVariable()
            {
                var p = WidePtrZ(_args[0]);
                var missing = NewLabel();
                var done = NewLabel();
                var need = NewReg(4);
                SysCall(need, "GetEnvironmentVariableW", 3, p, C(8, 0), C(4, 0));
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

            // GetCurrentDirectory() �?string：GetCurrentDirectoryW 两阶段；�?�?空串
            private void EmitGetCurrentDirectory()
            {
                var empty = NewLabel();
                var done = NewLabel();
                var need = NewReg(4);
                SysCall(need, "GetCurrentDirectoryW", 2, C(4, 0), C(8, 0));
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

            // GetExecutablePath() �?string：GetModuleFileNameW；缓冲截�?失败 �?空串
            private void EmitGetExecutablePath()
            {
                var empty = NewLabel();
                var done = NewLabel();
                var buf = NewPtr();
                LeaData(buf, _fileBuffer);
                var cap = C(4, 0x4000);
                var actual = NewReg(4);
                SysCall(actual, "GetModuleFileNameW", 3, C(8, 0), buf, cap);
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

            // SetCurrentDirectory(path:8) �?void
            private void EmitSetCurrentDirectory()
            {
                var p = WidePtrZ(_args[0]);
                SysCall(null, "SetCurrentDirectoryW", 1, p);
                EndFunction(_currentFunction!, 0);
            }

            // FileReadAllText(path:8) �?string�?
            // 复制路径+null 结尾 �?ucrtbase _wfopen(rb) �?fread 一次读满（以实际字节数为准，免 seek）→ fclose
            // �?MultiByteToWideChar(CP_UTF8) 两阶段直写串对象 chars 区。读缓冲动�?Alloc�?2KB），失败 �?空串�?
            private void EmitFileReadAllText()
            {
                var fail = NewLabel();
                var done = NewLabel();
                var pb = WidePtrZ(_args[0]);
                var rb = NewPtr();
                LeaData(rb, _rbMode);
                var fp = NewPtr();
                var obj = NewPtr();
                Const(obj, 0);
                SysCallDll(fp, "ucrtbase.dll", "_wfopen", 2, true, pb, rb);
                Cmp(fp, 0);
                Jcc(LirCond.Equal, fail);
                var buf = NewPtr();
                CallRuntime(buf, "Alloc", C(4, 0x8000));
                Cmp(buf, 0);
                Jcc(LirCond.Equal, fail);
                var read = NewReg(4);
                SysCallDll(read, "ucrtbase.dll", "fread", 4, true, buf, C(4, 1), C(4, 0x8000), fp);
                SysCallDll(null, "ucrtbase.dll", "fclose", 1, true, fp);
                Cmp(read, 0);
                Jcc(LirCond.Equal, fail);
                var wc = NewReg(4);
                SysCall(wc, "MultiByteToWideChar", 6, C(4, 65001), C(4, 0), buf, read, C(8, 0), C(4, 0));
                Cmp(wc, 0);
                Jcc(LirCond.Equal, fail);
                var objSize = NewReg(4);
                Mov(objSize, wc);
                AddI(objSize, objSize, 1);
                Shr(objSize, objSize, 1);
                Shl(objSize, objSize, 2);
                AddI(objSize, objSize, 4);
                CallRuntime(obj, "Alloc", objSize);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, fail);
                Store(obj, 0, wc, 4);
                var dst = NewPtr();
                Lea(dst, obj, 4);
                var wc2 = NewReg(4);
                SysCall(wc2, "MultiByteToWideChar", 6, C(4, 65001), C(4, 0), buf, read, dst, wc);
                Cmp(wc2, 0);
                Jcc(LirCond.Equal, fail);
                Jmp(done);
                Mark(fail);
                var emptyStr = NewPtr();
                LeaData(emptyStr, _emptyString);
                Mov(obj, emptyStr);
                Mark(done);
                StoreRet(obj);
                EndFunction(_currentFunction!, 8);
            }

            // FileWriteAllText(path:8, text:8) �?void�?
            // 手动 UTF-16→UTF-8 编码（两阶段：计数→写入 Alloc 缓冲）→ 复制路径+null 结尾 �?ucrtbase _wfopen(wb) �?fwrite �?fclose�?
            // 编码器：ASCII 1 字节�?x80-0x7FF 2 字节、BMP(0x800-0xD7FF/0xE000-0xFFFF) 3 字节、高位代�?0xD800-0xDBFF) 4 字节�?
            private void EmitFileWriteAllText()
            {
                var fail = NewLabel();
                var countDone = NewLabel();
                var encodeDone = NewLabel();
                var s = _args[1];
                var len = NewReg(4);
                Load(len, s, 0, 4);
                var sp = NewPtr();
                Lea(sp, s, 4);

                // Phase 1：统�?UTF-8 字节�?
                var count = C(4, 0);
                var q = NewPtr();
                Mov(q, sp);
                var end = NewPtr();
                Mov(end, sp);
                var len2 = NewReg(4);
                Shl(len2, len, 1);
                Add(end, end, len2);
                var loop1 = NewLabel();
                var two = NewLabel();
                var three = NewLabel();
                var threeByte = NewLabel();
                Mark(loop1);
                Cmp(q, end);
                Jcc(LirCond.GreaterOrEqual, countDone);
                var c = NewReg(4);
                Load(c, q, 0, 2);
                Cmp(c, 0x80);
                Jcc(LirCond.AboveOrEqual, two);
                AddI(count, count, 1);
                AddI(q, q, 2);
                Jmp(loop1);
                Mark(two);
                Cmp(c, 0x800);
                Jcc(LirCond.AboveOrEqual, three);
                AddI(count, count, 2);
                AddI(q, q, 2);
                Jmp(loop1);
                Mark(three);
                Cmp(c, 0xD800);
                Jcc(LirCond.Less, threeByte);
                Cmp(c, 0xDC00);
                Jcc(LirCond.AboveOrEqual, threeByte);
                AddI(count, count, 4);
                AddI(q, q, 4);
                Jmp(loop1);
                Mark(threeByte);
                AddI(count, count, 3);
                AddI(q, q, 2);
                Jmp(loop1);

                Mark(countDone);
                Cmp(count, 0x8000);
                Jcc(LirCond.Above, fail);
                var buf = NewPtr();
                CallRuntime(buf, "Alloc", count);
                Cmp(buf, 0);
                Jcc(LirCond.Equal, fail);

                // Phase 2：编码写入缓�?
                var q2 = NewPtr();
                Mov(q2, sp);
                var outp = NewPtr();
                Mov(outp, buf);
                var loop2 = NewLabel();
                var t2 = NewLabel();
                var t3 = NewLabel();
                var t4 = NewLabel();
                var t3enc = NewLabel();
                Mark(loop2);
                Cmp(q2, end);
                Jcc(LirCond.GreaterOrEqual, encodeDone);
                var ch = NewReg(4);
                Load(ch, q2, 0, 2);
                Cmp(ch, 0x80);
                Jcc(LirCond.AboveOrEqual, t2);
                Store(outp, 0, ch, 1);
                AddI(outp, outp, 1);
                AddI(q2, q2, 2);
                Jmp(loop2);
                Mark(t2);
                Cmp(ch, 0x800);
                Jcc(LirCond.AboveOrEqual, t3);
                var b1 = NewReg(4);
                Shr(b1, ch, 6);
                Or(b1, b1, C(4, 0xC0));
                Store(outp, 0, b1, 1);
                var b2 = NewReg(4);
                And(b2, ch, C(4, 0x3F));
                Or(b2, b2, C(4, 0x80));
                Store(outp, 1, b2, 1);
                AddI(outp, outp, 2);
                AddI(q2, q2, 2);
                Jmp(loop2);
                Mark(t3);
                Cmp(ch, 0xD800);
                Jcc(LirCond.Less, t3enc);
                Cmp(ch, 0xDC00);
                Jcc(LirCond.AboveOrEqual, t3enc);
                Jmp(t4);
                Mark(t3enc);
                var e1 = NewReg(4);
                Shr(e1, ch, 12);
                Or(e1, e1, C(4, 0xE0));
                Store(outp, 0, e1, 1);
                var e2 = NewReg(4);
                Shr(e2, ch, 6);
                And(e2, e2, C(4, 0x3F));
                Or(e2, e2, C(4, 0x80));
                Store(outp, 1, e2, 1);
                var e3 = NewReg(4);
                And(e3, ch, C(4, 0x3F));
                Or(e3, e3, C(4, 0x80));
                Store(outp, 2, e3, 1);
                AddI(outp, outp, 3);
                AddI(q2, q2, 2);
                Jmp(loop2);
                Mark(t4);
                // 代理对：cp = ((hi-0xD800)<<10) | (lo-0xDC00) + 0x10000
                var lo = NewReg(4);
                Load(lo, q2, 2, 2);
                var cp = NewReg(4);
                Mov(cp, ch);
                SubI(cp, cp, 0xD800);
                Shl(cp, cp, 10);
                var loOff = NewReg(4);
                Mov(loOff, lo);
                SubI(loOff, loOff, 0xDC00);
                Or(cp, cp, loOff);
                AddI(cp, cp, 0x10000);
                var f1 = NewReg(4);
                Shr(f1, cp, 18);
                Or(f1, f1, C(4, 0xF0));
                Store(outp, 0, f1, 1);
                var f2 = NewReg(4);
                Shr(f2, cp, 12);
                And(f2, f2, C(4, 0x3F));
                Or(f2, f2, C(4, 0x80));
                Store(outp, 1, f2, 1);
                var f3 = NewReg(4);
                Shr(f3, cp, 6);
                And(f3, f3, C(4, 0x3F));
                Or(f3, f3, C(4, 0x80));
                Store(outp, 2, f3, 1);
                var f4 = NewReg(4);
                And(f4, cp, C(4, 0x3F));
                Or(f4, f4, C(4, 0x80));
                Store(outp, 3, f4, 1);
                AddI(outp, outp, 4);
                AddI(q2, q2, 4);
                Jmp(loop2);

                Mark(encodeDone);
                // 复制路径�?_fileBuffer 并补 null（CO 串无 null 结尾�?2KB 缓冲足够，超长路径截断属文档限制�?
                var pb = WidePtrZ(_args[0]);
                var wb = NewPtr();
                LeaData(wb, _wbMode);
                var fp = NewPtr();
                SysCallDll(fp, "ucrtbase.dll", "_wfopen", 2, true, pb, wb);
                Cmp(fp, 0);
                Jcc(LirCond.Equal, fail);
                var written = NewReg(4);
                SysCallDll(written, "ucrtbase.dll", "fwrite", 4, true, buf, C(4, 1), count, fp);
                SysCallDll(null, "ucrtbase.dll", "fclose", 1, true, fp);
                Cmp(written, count);
                Jcc(LirCond.NotEqual, fail);
                Mark(fail);
                EndFunction(_currentFunction!, 0);
            }

        }
    }
}
