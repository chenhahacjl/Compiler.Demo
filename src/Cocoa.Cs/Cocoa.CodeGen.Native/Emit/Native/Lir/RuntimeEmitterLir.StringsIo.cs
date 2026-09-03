using System;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>
    /// 平台无关运行�?IR 生成：把�?x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs�?
    /// 合并为单一 IR 程序挂接�?br/>
    /// 平台差异收敛为：指针槽宽�?/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）�?
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 LirToAssembler.SysCall 负责�?
    /// </summary>
    internal static partial class RuntimeEmitterLir
    {
        private sealed partial class RuntimeFunctionEmitter
        {
            private void EmitParseInt()
            {
                var s = _args[0];
                var loop = NewLabel();
                var done = NewLabel();

                var len = NewReg(4);
                Load(len, s, 0, 4);
                var p = NewPtr();
                Lea(p, s, 4);
                var acc = C(4, 0);
                var i = C(4, 0);
                var ten = C(4, 10);

                Mark(loop);
                Cmp(i, len);
                Jcc(LirCond.GreaterOrEqual, done);
                var ch = NewReg(4);
                Load(ch, p, 0, 2);
                var nextP = NewPtr();
                Lea(nextP, p, 2);
                Mov(p, nextP);
                AddI(ch, ch, -'0');
                Imul(acc, acc, ten);
                Add(acc, acc, ch);
                AddI(i, i, 1);
                Jmp(loop);

                Mark(done);
                StoreRet(acc);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ParseBool(s:8) �?bool
            // ------------------------------------------------------------------

            private void EmitParseBool()
            {
                var s = _args[0];
                var done = NewLabel();

                var result = C(4, 0);
                Cmp(s, 0);
                Jcc(LirCond.Equal, done);
                var len = NewReg(4);
                Load(len, s, 0, 4);
                Cmp(len, 0);
                Jcc(LirCond.Equal, done);
                Const(result, 1);

                Mark(done);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Concat(a:8, b:8) �?新字符串对象
            // ------------------------------------------------------------------

            private void EmitConcat()
            {
                var a = _args[0];
                var b = _args[1];
                var fail = NewLabel();
                var copiedA = NewLabel();
                var copiedB = NewLabel();
                var done = NewLabel();
                var lenADone = NewLabel();
                var lenBDone = NewLabel();

                // 6e-M19 M5-a：null 字符串按空串参与拼接（与 IL String.Concat / Evaluator 语义一致）
                var lenA = NewReg(4);
                Mov(lenA, C(4, 0));
                Cmp(a, 0);
                Jcc(LirCond.Equal, lenADone);
                Load(lenA, a, 0, 4);
                Mark(lenADone);

                var lenB = NewReg(4);
                Mov(lenB, C(4, 0));
                Cmp(b, 0);
                Jcc(LirCond.Equal, lenBDone);
                Load(lenB, b, 0, 4);
                Mark(lenBDone);

                var size = NewReg(4);
                Mov(size, lenA);
                Add(size, size, lenB);
                AddI(size, size, 1);
                Shr(size, size, 1);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewPtr();
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, fail);

                var total = NewReg(4);
                Mov(total, lenA);
                Add(total, total, lenB);
                Store(obj, 0, total, 4);

                // 拷贝 A（源�?null 时跳过——lenA 已按 0 计入总长与偏移）
                Cmp(a, 0);
                Jcc(LirCond.Equal, copiedA);
                var countA = NewReg(4);
                Mov(countA, lenA);
                AddI(countA, countA, 1);
                Shr(countA, countA, 1);
                var srcA = NewPtr();
                Lea(srcA, a, 4);
                var dstA = NewPtr();
                Lea(dstA, obj, 4);
                CallRuntime(null, "CopyChars", dstA, srcA, countA);
                Mark(copiedA);

                // 拷贝 B（目标偏移按 lenA 前进，null 时落头部�?
                Cmp(b, 0);
                Jcc(LirCond.Equal, copiedB);
                var countB = NewReg(4);
                Mov(countB, lenB);
                AddI(countB, countB, 1);
                Shr(countB, countB, 1);
                var srcB = NewPtr();
                Lea(srcB, b, 4);
                var dstB = NewPtr();
                Lea(dstB, obj, 4);
                var lenAQuad = NewPtr();
                Mov(lenAQuad, lenA);
                Shl(lenAQuad, lenAQuad, 1);
                Add(dstB, dstB, lenAQuad);
                CallRuntime(null, "CopyChars", dstB, srcB, countB);
                Mark(copiedB);

                StoreRet(obj);
                Jmp(done);

                Mark(fail);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // StrEquals(a:8, b:8) �?bool
            // ------------------------------------------------------------------

            private void EmitStrEquals()
            {
                var a = _args[0];
                var b = _args[1];
                var loop = NewLabel();
                var isFalse = NewLabel();
                var isTrue = NewLabel();
                var done = NewLabel();

                // 指针相同（含�?null）→ 相等
                Cmp(a, b);
                Jcc(LirCond.Equal, isTrue);

                // 任一�?null �?不等
                Cmp(a, C(8, 0));
                Jcc(LirCond.Equal, isFalse);
                Cmp(b, C(8, 0));
                Jcc(LirCond.Equal, isFalse);

                var lenA = NewReg(4);
                Load(lenA, a, 0, 4);
                var lenB = NewReg(4);
                Load(lenB, b, 0, 4);
                Cmp(lenA, lenB);
                Jcc(LirCond.NotEqual, isFalse);

                // �?2 字节字符比较（非 dword：奇数长度末 dword 含堆/数据区填充垃圾，误判不等�?
                var ap = NewPtr();
                Lea(ap, a, 4);
                var bp = NewPtr();
                Lea(bp, b, 4);
                var count = NewReg(4);
                Mov(count, lenA);

                Mark(loop);
                Cmp(count, 0);
                Jcc(LirCond.Equal, isTrue);
                var charA = NewReg(4);
                Load(charA, ap, 0, 2);
                var charB = NewReg(4);
                Load(charB, bp, 0, 2);
                Cmp(charA, charB);
                Jcc(LirCond.NotEqual, isFalse);
                var nextAp = NewPtr();
                Lea(nextAp, ap, 2);
                Mov(ap, nextAp);
                var nextBp = NewPtr();
                Lea(nextBp, bp, 2);
                Mov(bp, nextBp);
                AddI(count, count, -1);
                Jmp(loop);

                Mark(isFalse);
                var zero = C(4, 0);
                StoreRet(zero);
                Jmp(done);

                Mark(isTrue);
                var one = C(4, 1);
                StoreRet(one);

                Mark(done);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Substring(s:8, start:4, count:4) �?字符串对�?
            // 参数非法（start/count < 0 �?start+count > 长度）时打印错误并退�?
            // ------------------------------------------------------------------

            private void EmitSubstring()
            {
                var s = _args[0];
                var start = _args[1];
                var count = _args[2];
                var invalid = NewLabel();
                var oom = NewLabel();
                var done = NewLabel();

                var len = NewReg(4);
                Load(len, s, 0, 4);

                Cmp(start, 0);
                Jcc(LirCond.Less, invalid);
                Cmp(count, 0);
                Jcc(LirCond.Less, invalid);
                var end = NewReg(4);
                Mov(end, start);
                Add(end, end, count);
                Cmp(end, len);
                Jcc(LirCond.Greater, invalid);

                var size = NewReg(4);
                Mov(size, count);
                Shl(size, size, 1);
                AddI(size, size, 3);
                And(size, size, C(4, ~3));
                AddI(size, size, 4);

                var obj = NewPtr();
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, oom);

                Store(obj, 0, count, 4);

                var dst = NewPtr();
                Lea(dst, obj, 4);
                var src = NewPtr();
                Lea(src, s, 4);
                var charOffset = NewReg(4);
                Mov(charOffset, start);
                Shl(charOffset, charOffset, 1);
                Add(src, src, charOffset);

                var words = NewReg(4);
                Mov(words, count);
                Shl(words, words, 1);
                AddI(words, words, 3);
                Shr(words, words, 2);
                CallRuntime(null, "CopyChars", dst, src, words);

                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);
                Jmp(done);

                Mark(invalid);
                var message = NewPtr();
                LeaData(message, _substringMessage);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // CharToString(c:4) �?单字符字符串对象（[len:4][char:2]�?
            // ------------------------------------------------------------------

            private void EmitCharToString()
            {                var c = _args[0];
                var oom = NewLabel();
                var done = NewLabel();

                var obj = NewPtr();
                CallRuntime(obj, "Alloc", C(4, 8));
                Cmp(obj, 0);
                Jcc(LirCond.Equal, oom);

                Store(obj, 0, C(4, 1), 4);
                Store(obj, 4, c, 2);
                StoreRet(obj);
                Jmp(done);

                Mark(oom);
                var zero = C(8, 0);
                StoreRet(zero);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // FormatInt(value:4, packed:4, exp:4) �?字符串对�?
            // packed = (width:16 有符�? << 16 | (n:8) << 8 | (code:4) << 4 | (kind:4)
            // kind: 0=int 1=byte 2=enum 3=bool 4=char（value 为字符码�?
            // code: 0=�?1=D 2=X 3=F 4=G 5=E
            // n：D/X 零填充位数；F 小数位数；G 有效数字位数；E 小数位数
            // exp：G/E 的十进制指数（F/int �?0�?
            // 对齐 width（负=左对齐）。bool/char 忽略格式�?
            // ------------------------------------------------------------------

            // x64：SetArg(0, value) �?DoubleToString；x86：value �?low/high 两参数�?
            private void EmitCallDoubleToString(LirVirtualRegister strObj, LirVirtualRegister value, LirVirtualRegister valueHigh)
            {
                SetArg(0, value);
                if (!_isX64)
                {
                    SetArg(1, valueHigh);
                }
                CallRuntime(strObj, "DoubleToString");
            }

            // x64：SetArg(0, value) SetArg(1, n) �?DoubleFixed；x86：value �?low/high + n�?
            private void EmitCallDoubleFixed(LirVirtualRegister scaled, LirVirtualRegister value, LirVirtualRegister valueHigh, LirVirtualRegister n)
            {
                SetArg(0, value);
                if (_isX64)
                {
                    SetArg(1, n);
                }
                else
                {
                    SetArg(1, valueHigh);
                    SetArg(2, n);
                }
                CallRuntime(scaled, "DoubleFixed");
            }

            // x64：SetArg(0, value) SetArg(1, n) �?FormatFixed；x86：value �?low/high + n�?
            private void EmitCallFormatFixed(LirVirtualRegister strObj, LirVirtualRegister value, LirVirtualRegister valueHigh, LirVirtualRegister n)
            {
                SetArg(0, value);
                if (_isX64)
                {
                    SetArg(1, n);
                }
                else
                {
                    SetArg(1, valueHigh);
                    SetArg(2, n);
                }
                CallRuntime(strObj, "FormatFixed");
            }

            // x64：SetArg(0, value) SetArg(1, n) SetArg(2, flags) �?FormatSci（flags = lowerE<<1 | mode）；x86：value �?low/high + n + flags�?
            private void EmitCallFormatSci(LirVirtualRegister strObj, LirVirtualRegister value, LirVirtualRegister valueHigh, LirVirtualRegister n, LirVirtualRegister flags)
            {
                SetArg(0, value);
                if (_isX64)
                {
                    SetArg(1, n);
                    SetArg(2, flags);
                }
                else
                {
                    SetArg(1, valueHigh);
                    SetArg(2, n);
                    SetArg(3, flags);
                }
                CallRuntime(strObj, "FormatSci");
            }

            // StringFormat(value:8, fmtPtr:8, packed:4) �?字符串对�?
            //   value   ：原始值（int/byte/enum 的低 4 字节；double �?8 字节；string/bool/char 按各自宽度）
            //   fmtPtr  ：格式串指针（UTF-16，来�?InternString；长度存�?[fmtPtr+0]�?
            //   packed  ：低 4 �?typeKind�?=int/byte/enum�?=double�?=string�?=bool�?=char），
            //             �?16 位（�?4..19）为有符号对齐宽度（�?左对齐，0=不填充）
            // 运行时解析格式串（code/n/lowerCase），统一所有类型到单一入口�?
            private void EmitStringFormat()
            {
                LirVirtualRegister value;
                LirVirtualRegister valueHigh;
                var fmtPtr = _isX64 ? _args[1] : _args[2];
                var packed = _isX64 ? _args[2] : _args[3];
                value = _args[0];
                valueHigh = _isX64 ? null! : _args[1];

                var typeKind = NewReg(4);
                AndI(typeKind, packed, 0xF);
                var width = NewReg(4);
                Mov(width, packed);
                Shr(width, width, 4);
                AndI(width, width, 0xFFFF);
                var wExtDone = NewLabel();
                Cmp(width, 0x7FFF);
                Jcc(LirCond.LessOrEqual, wExtDone);
                AddI(width, width, -0x10000);
                Mark(wExtDone);

                var fmtLen = NewReg(4);
                Load(fmtLen, fmtPtr, 0, 4);

                var code = NewReg(4);
                var n = NewReg(4);
                var lowerCase = NewReg(4);
                ParseFormat(fmtPtr, fmtLen, code, n, lowerCase);

                var strObj = NewPtr();
                var strLen = NewReg(4);
                var stringKind = NewLabel();
                var boolKind = NewLabel();
                var charKind = NewLabel();
                var intKind = NewLabel();
                var doubleKind = NewLabel();
                var applyAlign = NewLabel();

                Cmp(typeKind, 0); Jcc(LirCond.Equal, intKind);
                Cmp(typeKind, 1); Jcc(LirCond.Equal, doubleKind);
                Cmp(typeKind, 2); Jcc(LirCond.Equal, stringKind);
                Cmp(typeKind, 3); Jcc(LirCond.Equal, boolKind);
                Jmp(charKind);

                // ---- string：原样（对齐在末尾统一处理�?---
                Mark(stringKind);
                Mov(strObj, value);
                Load(strLen, value, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- bool：True/False ----
                Mark(boolKind);
                var isTrue = NewLabel();
                Cmp(value, 0);
                Jcc(LirCond.NotEqual, isTrue);
                LeaData(strObj, _formatFalse);
                Const(strLen, 10);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(isTrue);
                LeaData(strObj, _formatTrue);
                Const(strLen, 8);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- char：CharToString ----
                Mark(charKind);
                CallRuntime(strObj, "CharToString", value);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- int/byte/enum：D �?十进零填；X/x �?十六进制；其�?�?十进�?----
                Mark(intKind);
                var intHex = NewLabel();
                var intDecPad = NewLabel();
                var intPlain = NewLabel();
                Cmp(code, 1); Jcc(LirCond.Equal, intDecPad);
                Cmp(code, 2); Jcc(LirCond.Equal, intHex);
                Jmp(intPlain);
                Mark(intDecPad);
                CallRuntime(strObj, "FormatDecPad", value, n);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(intHex);
                CallRuntime(strObj, "FormatHex", value, n, lowerCase);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(intPlain);
                CallRuntime(strObj, "IntToString", value);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- double：F(n) �?预缩放后 scale-and-print；特殊�?其他格式 �?DoubleToString ----
                Mark(doubleKind);
                var dHi = NewReg(4);
                if (_isX64)
                {
                    LoadSlotField(dHi, value, 4, 4);
                }
                else
                {
                    Mov(dHi, valueHigh);
                }
                var dExpMask = C(4, 0x7FF00000);
                var dHiAnd = NewReg(4);
                And(dHiAnd, dHi, dExpMask);
                var dIsSpecial = NewLabel();
                var dFmt = NewLabel();
                var dSci = NewLabel();
                var dPlain = NewLabel();
                Cmp(dHiAnd, dExpMask); Jcc(LirCond.Equal, dIsSpecial);
                Cmp(code, 3); Jcc(LirCond.Equal, dFmt);
                Cmp(code, 4); Jcc(LirCond.Equal, dSci);
                Cmp(code, 5); Jcc(LirCond.Equal, dSci);
                Jmp(dPlain);
                Mark(dIsSpecial);
                EmitCallDoubleToString(strObj, value, valueHigh);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dFmt);
                var dFmtOk = NewLabel();
                Cmp(n, 24); Jcc(LirCond.Greater, dPlain);
                Mark(dFmtOk);
                EmitCallFormatFixed(strObj, value, valueHigh, n);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dSci);
                var dSciOk = NewLabel();
                Cmp(n, 24); Jcc(LirCond.Greater, dPlain);
                Mark(dSciOk);
                // flags = lowerCase<<1 | (code==4 ? 1 : 0)
                var sciFlags = NewReg(4);
                var sciIsG = NewLabel();
                var sciModeDone = NewLabel();
                Cmp(code, 4);
                Jcc(LirCond.Equal, sciIsG);
                Const(sciFlags, 0);
                Jmp(sciModeDone);
                Mark(sciIsG);
                Const(sciFlags, 1);
                Mark(sciModeDone);
                var sciLower = NewReg(4);
                Mov(sciLower, lowerCase);
                Shl(sciLower, sciLower, 1);
                Add(sciFlags, sciFlags, sciLower);
                EmitCallFormatSci(strObj, value, valueHigh, n, sciFlags);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dPlain);
                EmitCallDoubleToString(strObj, value, valueHigh);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                Mark(applyAlign);

                // ---- 对齐 ----
                var aligned = NewPtr();
                CallRuntime(aligned, "ApplyAlignment", strObj, strLen, width);
                Mov(strObj, aligned);

                StoreRet(strObj);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // PadString(str:8, width:4) �?字符串对象（对齐填充；pad �?0 恒等�?
            // ------------------------------------------------------------------

            private void EmitPadString()
            {
                var str = _args[0];
                var width = _args[1];
                var len = NewReg(4);
                Load(len, str, 0, 4);
                Shl(len, len, 1);
                var aligned = NewPtr();
                CallRuntime(aligned, "ApplyAlignment", str, len, width);
                Mov(str, aligned);
                StoreRet(str);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>�?|width|*2 &gt; baseLen：重建为带空格填充的字符串（width&gt;0 右对�?/ 左对齐）。返回最终字符串寄存器（所有路径已定义）�?/summary>
            private void EmitApplyAlignment()
            {
                var baseObj = _args[0];
                var baseLen = _args[1];
                var width = _args[2];
                var result = NewPtr();
                Mov(result, baseObj);
                var alignDone = NewLabel();
                Cmp(width, 0);
                Jcc(LirCond.Equal, alignDone);
                var absWidth = NewReg(4);
                Mov(absWidth, width);
                var wIsNeg = NewLabel();
                var wAbsDone = NewLabel();
                Cmp(width, 0);
                Jcc(LirCond.Less, wIsNeg);
                Jmp(wAbsDone);
                Mark(wIsNeg);
                Neg(absWidth);
                Mark(wAbsDone);
                var pad = NewReg(4);
                var baseChars = NewReg(4);
                Mov(baseChars, baseLen);
                Shr(baseChars, baseChars, 1);
                Sub(pad, absWidth, baseChars);
                Cmp(pad, 0);
                Jcc(LirCond.LessOrEqual, alignDone);

                var buf = NewPtr();
                LeaData(buf, _formatBuffer);
                var bufPos = NewPtr();
                Mov(bufPos, buf);
                var len2 = NewReg(4);
                Const(len2, 0);
                var rightAlign = NewLabel();
                Cmp(width, 0);
                Jcc(LirCond.Greater, rightAlign);

                // 左对齐：内容 + 空格
                EmitCopyStrChars(bufPos, len2, baseObj);
                EmitWriteRepeatedChar(bufPos, len2, pad, ' ');
                var built = NewLabel();
                Jmp(built);

                // 右对齐：空格 + 内容
                Mark(rightAlign);
                EmitWriteRepeatedChar(bufPos, len2, pad, ' ');
                EmitCopyStrChars(bufPos, len2, baseObj);

                Mark(built);
                var obj2 = NewPtr();
                CallRuntime(obj2, "AllocStringFromBuf", buf, len2);
                Mov(result, obj2);
                Mark(alignDone);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>�?baseObj 的全部字符拷�?bufPos 并推进（len 增加对应字节）�?/summary>
            private void EmitCopyStrChars(LirVirtualRegister bufPos, LirVirtualRegister len, LirVirtualRegister srcObj)
            {
                var chars = NewReg(4);
                Load(chars, srcObj, 0, 4);
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                Mark(loop);
                Cmp(i, chars);
                Jcc(LirCond.GreaterOrEqual, done);
                var srcPos = NewPtr();
                Lea(srcPos, srcObj, 4);
                var off = NewReg(4);
                Mov(off, i);
                Shl(off, off, 1);
                var dstPos = NewPtr();
                Add(dstPos, srcPos, off);
                var ch = NewReg(4);
                Load(ch, dstPos, 0, 2);
                Store(bufPos, 0, ch, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewPtr();
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>写入 count �?ch�? 字节）到 bufPos 并推进�?/summary>
            private void EmitWriteRepeatedChar(LirVirtualRegister bufPos, LirVirtualRegister len, LirVirtualRegister count, char ch)
            {
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                var chReg = C(4, ch);
                Mark(loop);
                Cmp(i, count);
                Jcc(LirCond.GreaterOrEqual, done);
                Store(bufPos, 0, chReg, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewPtr();
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>�?_formatBuffer �?lenBytes 字节�?UTF-16 内容分配为字符串对象�?/summary>
            private void EmitAllocStringFromBuf()
            {
                var buf = _args[0];
                var lenBytes = _args[1];
                var oom = NewLabel();
                var done = NewLabel();
                var size = NewReg(4);
                Mov(size, lenBytes);
                AddI(size, size, 2);
                Shr(size, size, 2);
                Shl(size, size, 2);
                AddI(size, size, 4);
                var obj = NewPtr();
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(LirCond.Equal, oom);
                var chars = NewReg(4);
                Mov(chars, lenBytes);
                Shr(chars, chars, 1);
                Store(obj, 0, chars, 4);
                var count = NewReg(4);
                Mov(count, lenBytes);
                AddI(count, count, 2);
                Shr(count, count, 2);
                var dst = NewPtr();
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, buf, count);
                Jmp(done);
                Mark(oom);
                Const(obj, 0);
                Mark(done);
                StoreRet(obj);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>十六进制（不�?n 位前�?'0'，大�?X / 小写 x）：写入 _formatBuffer 起始，返回字符串对象�?/summary>
            private void EmitFormatHex()
            {
                var value = _args[0];
                var minDigits = _args[1];
                var lowerCase = _args[2];
                var buf = NewPtr();
                LeaData(buf, _formatBuffer);
                var tail = NewPtr();
                Lea(tail, buf, 24);
                var v = NewReg(4);
                Mov(v, value);
                var count = NewReg(4);
                Const(count, 0);
                var sixteen = C(4, 16);
                var loop = NewLabel();
                var done = NewLabel();
                Mark(loop);
                var quotient = NewReg(4);
                Mov(quotient, v);
                Udiv(quotient, sixteen);
                var digit = NewReg(4);
                Imul(digit, quotient, sixteen);
                Sub(digit, v, digit);
                var ch = NewReg(4);
                var isLetter = NewLabel();
                var isDigit = NewLabel();
                var chReady = NewLabel();
                Cmp(digit, 9);
                Jcc(LirCond.Greater, isLetter);
                var digitCh = NewReg(4);
                AddI(digitCh, digit, '0');
                Mov(ch, digitCh);
                Jmp(chReady);
                Mark(isLetter);
                var letterCh = NewReg(4);
                var lowerA = NewLabel();
                Cmp(lowerCase, 0); Jcc(LirCond.Equal, lowerA);
                AddI(letterCh, digit, 'a' - 10);
                Mov(ch, letterCh);
                Jmp(chReady);
                Mark(lowerA);
                AddI(letterCh, digit, 'A' - 10);
                Mov(ch, letterCh);
                Jmp(chReady);
                Mark(chReady);
                var nt = NewPtr();
                Lea(nt, tail, -2);
                Store(nt, 0, ch, 2);
                Mov(tail, nt);
                Mov(v, quotient);
                var nc = NewReg(4);
                AddI(nc, count, 1);
                Mov(count, nc);
                Cmp(v, 0);
                Jcc(LirCond.NotEqual, loop);
                Mark(done);

                // 前补 '0' �?minDigits
                var padLoop = NewLabel();
                var padDone = NewLabel();
                Cmp(count, minDigits);
                Jcc(LirCond.GreaterOrEqual, padDone);
                Mark(padLoop);
                var pnt = NewPtr();
                Lea(pnt, tail, -2);
                Store(pnt, 0, C(4, '0'), 2);
                Mov(tail, pnt);
                var pc = NewReg(4);
                AddI(pc, count, 1);
                Mov(count, pc);
                Cmp(count, minDigits);
                Jcc(LirCond.Less, padLoop);
                Mark(padDone);

                var end = NewPtr();
                Lea(end, buf, 24);
                var lenBytes = NewReg(4);
                Sub(lenBytes, end, tail);
                var copyCount = NewReg(4);
                Mov(copyCount, lenBytes);
                AddI(copyCount, copyCount, 2);
                Shr(copyCount, copyCount, 2);
                CallRuntime(null, "CopyChars", buf, tail, copyCount);
                var built = NewPtr();
                CallRuntime(built, "AllocStringFromBuf", buf, lenBytes);
                StoreRet(built);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>十进制零填充：|value| 的数字不�?n 位前�?'0'（负号在首位）�?/summary>
            private void EmitFormatDecPad()
            {
                var value = _args[0];
                var n = _args[1];
                var str = NewPtr();
                CallRuntime(str, "IntToString", value);
                var lenChars = NewReg(4);
                Load(lenChars, str, 0, 4);
                var first = NewReg(4);
                Load(first, str, 4, 2);
                var sign = NewReg(4);
                var isNeg = NewLabel();
                var signDone = NewLabel();
                Cmp(first, '-');
                Jcc(LirCond.Equal, isNeg);
                Const(sign, 0);
                Jmp(signDone);
                Mark(isNeg);
                Const(sign, 1);
                Mark(signDone);
                var digitsLen = NewReg(4);
                Sub(digitsLen, lenChars, sign);

                var pad = NewReg(4);
                Sub(pad, n, digitsLen);
                var noPad = NewLabel();
                var buildDone = NewLabel();
                Cmp(pad, 0);
                Jcc(LirCond.LessOrEqual, noPad);

                var buf = NewPtr();
                LeaData(buf, _formatBuffer);
                var bufPos = NewPtr();
                Mov(bufPos, buf);
                var lenBytes = NewReg(4);
                Const(lenBytes, 0);
                var negSign = NewLabel();
                var signSkip = NewLabel();
                Cmp(sign, 0);
                Jcc(LirCond.NotEqual, negSign);
                Jmp(signSkip);
                Mark(negSign);
                Store(bufPos, 0, C(4, '-'), 2);
                var nl = NewReg(4);
                AddI(nl, lenBytes, 2);
                Mov(lenBytes, nl);
                var nb = NewPtr();
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                Mark(signSkip);

                EmitWriteRepeatedChar(bufPos, lenBytes, pad, '0');
                var digitOffset = NewReg(4);
                Mov(digitOffset, sign);
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                var builtObj = NewPtr();
                CallRuntime(builtObj, "AllocStringFromBuf", buf, lenBytes);
                var result = NewPtr();
                Mov(result, builtObj);
                Jmp(buildDone);
                Mark(noPad);
                Mov(result, str);
                Mark(buildDone);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>scale-and-print：把 scaled（int32）按 code 组装（F：n 小数位；G：exp+1 整数位；E�? 整数�?+ E±exp）�?/summary>
            private void EmitScaleAssemble()
            {
                var value = _args[0];
                var code = _args[1];
                var n = _args[2];
                var exp = _args[3];
                var str = NewPtr();
                CallRuntime(str, "IntToString", value);
                var lenChars = NewReg(4);
                Load(lenChars, str, 0, 4);
                var first = NewReg(4);
                Load(first, str, 4, 2);
                var sign = NewReg(4);
                var isNeg = NewLabel();
                var signDone = NewLabel();
                Cmp(first, '-');
                Jcc(LirCond.Equal, isNeg);
                Const(sign, 0);
                Jmp(signDone);
                Mark(isNeg);
                Const(sign, 1);
                Mark(signDone);
                var digitsLen = NewReg(4);
                Sub(digitsLen, lenChars, sign);

                // 整数位个�?K
                var K = NewReg(4);
                var isF = NewLabel();
                var isG = NewLabel();
                var kReady = NewLabel();
                Cmp(code, 3);
                Jcc(LirCond.Equal, isF);
                Cmp(code, 4);
                Jcc(LirCond.Equal, isG);
                // E
                Const(K, 1);
                Jmp(kReady);
                Mark(isF);
                var kf = NewReg(4);
                Sub(kf, digitsLen, n);
                Mov(K, kf);
                Jmp(kReady);
                Mark(isG);
                var kg = NewReg(4);
                AddI(kg, exp, 1);
                Mov(K, kg);
                Mark(kReady);

                var buf = NewPtr();
                LeaData(buf, _formatBuffer);
                var bufPos = NewPtr();
                Mov(bufPos, buf);
                var lenBytes = NewReg(4);
                Const(lenBytes, 0);
                var negSign = NewLabel();
                var signSkip = NewLabel();
                Cmp(sign, 0);
                Jcc(LirCond.NotEqual, negSign);
                Jmp(signSkip);
                Mark(negSign);
                Store(bufPos, 0, C(4, '-'), 2);
                var nl = NewReg(4);
                AddI(nl, lenBytes, 2);
                Mov(lenBytes, nl);
                var nb = NewPtr();
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                Mark(signSkip);

                // 整数/小数组装
                var intBig = NewLabel();
                var intOk = NewLabel();
                var intZero = NewLabel();
                var afterInt = NewLabel();
                var digitOffset = NewReg(4);
                Mov(digitOffset, sign);
                Cmp(K, digitsLen);
                Jcc(LirCond.GreaterOrEqual, intBig);
                Cmp(K, 0);
                Jcc(LirCond.Greater, intOk);
                // K <= 0：整�?'0' + '.' + (-K) �?'0' + 全部 digits
                Store(bufPos, 0, C(4, '0'), 2);
                var nl0 = NewReg(4);
                AddI(nl0, lenBytes, 2);
                Mov(lenBytes, nl0);
                var nb0 = NewPtr();
                Lea(nb0, bufPos, 2);
                Mov(bufPos, nb0);
                Jmp(intZero);

                Mark(intBig);
                // 全部 digits + (K-digitsLen) �?'0'
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                var extra = NewReg(4);
                Sub(extra, K, digitsLen);
                EmitWriteRepeatedChar(bufPos, lenBytes, extra, '0');
                Jmp(afterInt);

                Mark(intOk);
                // digits[0..K] + '.' + digits[K..]
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, K);
                Store(bufPos, 0, C(4, '.'), 2);
                var nld = NewReg(4);
                AddI(nld, lenBytes, 2);
                Mov(lenBytes, nld);
                var nbd = NewPtr();
                Lea(nbd, bufPos, 2);
                Mov(bufPos, nbd);
                var fracLen = NewReg(4);
                Sub(fracLen, digitsLen, K);
                var fracStart = NewReg(4);
                Add(fracStart, digitOffset, K);
                EmitCopyDigits(bufPos, lenBytes, str, fracStart, fracLen);
                Jmp(afterInt);

                Mark(intZero);
                Store(bufPos, 0, C(4, '.'), 2);
                var nlz = NewReg(4);
                AddI(nlz, lenBytes, 2);
                Mov(lenBytes, nlz);
                var nbz = NewPtr();
                Lea(nbz, bufPos, 2);
                Mov(bufPos, nbz);
                var negK = NewReg(4);
                Neg(K);
                Mov(negK, K);
                EmitWriteRepeatedChar(bufPos, lenBytes, negK, '0');
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                Jmp(afterInt);

                Mark(afterInt);

                // E 后缀：E ± exp（exp 非负化后十进制）
                var eSuffixDone = NewLabel();
                Cmp(code, 5);
                Jcc(LirCond.NotEqual, eSuffixDone);
                Store(bufPos, 0, C(4, 'E'), 2);
                var nle = NewReg(4);
                AddI(nle, lenBytes, 2);
                Mov(lenBytes, nle);
                var nbe = NewPtr();
                Lea(nbe, bufPos, 2);
                Mov(bufPos, nbe);
                var expNeg = NewLabel();
                var expSignDone = NewLabel();
                Cmp(exp, 0);
                Jcc(LirCond.Less, expNeg);
                Store(bufPos, 0, C(4, '+'), 2);
                Jmp(expSignDone);
                Mark(expNeg);
                Store(bufPos, 0, C(4, '-'), 2);
                Neg(exp);
                Mark(expSignDone);
                var nls = NewReg(4);
                AddI(nls, lenBytes, 2);
                Mov(lenBytes, nls);
                var nbs = NewPtr();
                Lea(nbs, bufPos, 2);
                Mov(bufPos, nbs);
                var expStr = NewPtr();
                CallRuntime(expStr, "IntToString", exp);
                var expZero = NewReg(4);
                Const(expZero, 0);
                var expLen = NewReg(4);
                Load(expLen, expStr, 0, 4);
                EmitCopyDigits(bufPos, lenBytes, expStr, expZero, expLen);
                Mark(eSuffixDone);

                var built = NewPtr();
                CallRuntime(built, "AllocStringFromBuf", buf, lenBytes);
                StoreRet(built);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>�?srcObj �?startChar �?countChar 个字符拷�?bufPos 并推进�?/summary>
            private void EmitCopyDigits(LirVirtualRegister bufPos, LirVirtualRegister len, LirVirtualRegister srcObj, LirVirtualRegister startChar, LirVirtualRegister countChar)
            {
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                Mark(loop);
                Cmp(i, countChar);
                Jcc(LirCond.GreaterOrEqual, done);
                var idx = NewReg(4);
                Add(idx, startChar, i);
                var srcPos = NewPtr();
                Lea(srcPos, srcObj, 4);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                var dstPos = NewPtr();
                Add(dstPos, srcPos, off);
                var ch = NewReg(4);
                Load(ch, dstPos, 0, 2);
                Store(bufPos, 0, ch, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewPtr();
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

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
                // 去尾�?\r \n
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
            // ReadKey(intercept:4) �?char
            // 读取单键。intercept=0 时回显（WriteConsoleW）；=1 时不回显�?
            // �?ReadConsoleInputW �?INPUT_RECORD，取 KEY_EVENT �?bKeyDown �?UnicodeChar�?
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

                // �?intercept=0，回显该字符（WriteConsoleW 到输出句柄）
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
            // Random(max:4) �?0..max-1（xorshift32�?
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
            // ObjectEquals(a:8, b:8) �?bool（指针比较）
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
            // ObjectToString(obj:ptr) �?str�?e-M19 M4：读对象�?vtable �?名字指针�?
            // 对象布局 [0]=vtablePtr；vtable [8]=类型全名字符串指针（伪记录自引用�?Type 值同样成立）
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
            // ObjectGetHashCode(x:8) �?int（指�?位模式散列：lo ^ hi 后乘黄金比例常数�?
            // x86 指针�?4，仅�?dword 参与散列
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
            // ObjectGetType(obj:ptr) �?vtablePtr（GetType 非虚，占�?3 保持统一发射路径�?
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
            // TypeSimpleName(s:str) �?str（最后一�?'.' 之后的部分；无点回退原串�?
            // �?IL FullName.Substring(LastIndexOf('.')+1) 组合语义一致，三后端统一�?
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
            // Beep(frequency:4, duration:4) �?void（kernel32 Beep：扬声器蜂鸣�?
            // ------------------------------------------------------------------

            private void EmitBeep()
            {
                var frequency = _args[0];
                var duration = _args[1];
                SysCall(null, "Beep", 2, frequency, duration);
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // Y-P0-1：文�?IO / 环境 syscall（G7-�?补齐�?
            // 字符串对�?= 堆指�?[len:4][chars:2×len]（无 null 结尾），Win32 宽字符串 API 需 LPCWSTR �?WidePtrZ 复制�?null�?
            // SysCall 上限 6 参：文件 IO �?ucrtbase 低参 API + 6 �?MultiByteToWideChar；WideCharToMultiByte(8 �? 用手动编码替代�?
            // ------------------------------------------------------------------

            /// <summary>�?CO 字符串对象的宽字符区指针（chars@4）�?/summary>
            private LirVirtualRegister WidePtr(LirVirtualRegister s)
            {
                var p = NewPtr();
                Lea(p, s, 4);
                return p;
            }

            /// <summary>
            /// 复制 CO 字符串到 <paramref name="bufferKey"/> 并补 null 结尾，返�?LPCWSTR 指针�?
            /// CO 串布局 [len:4][chars:2×len] �?null 结尾（尾 padding 可能含拷贝残留）�?
            /// 直接传给 Win32 宽字符串 API（_wfopen/GetFileAttributesW 等）会读越界 �?非确定性失败�?
            /// �?helper 内立即消费；两个参数（src/dst）须用不同缓冲，否则互相覆盖�?
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

            /// <summary>复制到主缓冲（单路径场景）�?/summary>
            private LirVirtualRegister WidePtrZ(LirVirtualRegister s) => WidePtrZInto(s, _fileBuffer);

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

            // ------------------------------------------------------------------
        }
    }
}
