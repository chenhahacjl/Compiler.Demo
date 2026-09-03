using System;
using System.Collections.Generic;
using System.Linq;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

    /// <summary>
    /// 平台无关运行�?IR 生成：把�?x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs�?
    /// 合并为单一 IR 程序挂接�?br/>
    /// 平台差异收敛为：指针槽宽�?/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）�?
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 LirToAssembler.SysCall 负责�?
    /// </summary>
namespace Cocoa.CodeGen.Native.Lir
{
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
        }
    }
}
