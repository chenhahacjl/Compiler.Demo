using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>
    /// 平台无关运行时 IR 生成：把原 x86/x64 双份硬编码运行时（Runtime.cs / Runtime.X86.cs）
    /// 合并为单一 IR 程序挂接。<br/>
    /// 平台差异收敛为：指针槽宽（8/4）、数据项宽度（Pointer）、导入名（GetTickCount64/GetTickCount）、
    /// 堆槽偏移（Ptr@8/End@16 vs Ptr@4/End@8）；调用约定（x64 fastcall+shadow / x86 stdcall）由 IrToAssembler.SysCall 负责。
    /// </summary>
    internal static partial class RuntimeEmitterIR
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
                var p = NewReg(8);
                Lea(p, s, 4);
                var acc = C(4, 0);
                var i = C(4, 0);
                var ten = C(4, 10);

                Mark(loop);
                Cmp(i, len);
                Jcc(IrCond.GreaterOrEqual, done);
                var ch = NewReg(4);
                Load(ch, p, 0, 2);
                var nextP = NewReg(8);
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
            // ParseBool(s:8) → bool
            // ------------------------------------------------------------------

            private void EmitParseBool()
            {
                var s = _args[0];
                var done = NewLabel();

                var result = C(4, 0);
                Cmp(s, 0);
                Jcc(IrCond.Equal, done);
                var len = NewReg(4);
                Load(len, s, 0, 4);
                Cmp(len, 0);
                Jcc(IrCond.Equal, done);
                Const(result, 1);

                Mark(done);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // Concat(a:8, b:8) → 新字符串对象
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
                Jcc(IrCond.Equal, lenADone);
                Load(lenA, a, 0, 4);
                Mark(lenADone);

                var lenB = NewReg(4);
                Mov(lenB, C(4, 0));
                Cmp(b, 0);
                Jcc(IrCond.Equal, lenBDone);
                Load(lenB, b, 0, 4);
                Mark(lenBDone);

                var size = NewReg(4);
                Mov(size, lenA);
                Add(size, size, lenB);
                AddI(size, size, 1);
                Shr(size, size, 1);
                Shl(size, size, 2);
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, fail);

                var total = NewReg(4);
                Mov(total, lenA);
                Add(total, total, lenB);
                Store(obj, 0, total, 4);

                // 拷贝 A（源为 null 时跳过——lenA 已按 0 计入总长与偏移）
                Cmp(a, 0);
                Jcc(IrCond.Equal, copiedA);
                var countA = NewReg(4);
                Mov(countA, lenA);
                AddI(countA, countA, 1);
                Shr(countA, countA, 1);
                var srcA = NewReg(8);
                Lea(srcA, a, 4);
                var dstA = NewReg(8);
                Lea(dstA, obj, 4);
                CallRuntime(null, "CopyChars", dstA, srcA, countA);
                Mark(copiedA);

                // 拷贝 B（目标偏移按 lenA 前进，null 时落头部）
                Cmp(b, 0);
                Jcc(IrCond.Equal, copiedB);
                var countB = NewReg(4);
                Mov(countB, lenB);
                AddI(countB, countB, 1);
                Shr(countB, countB, 1);
                var srcB = NewReg(8);
                Lea(srcB, b, 4);
                var dstB = NewReg(8);
                Lea(dstB, obj, 4);
                var lenAQuad = NewReg(8);
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
            // StrEquals(a:8, b:8) → bool
            // ------------------------------------------------------------------

            private void EmitStrEquals()
            {
                var a = _args[0];
                var b = _args[1];
                var loop = NewLabel();
                var isFalse = NewLabel();
                var isTrue = NewLabel();
                var done = NewLabel();

                // 指针相同（含双 null）→ 相等
                Cmp(a, b);
                Jcc(IrCond.Equal, isTrue);

                // 任一为 null → 不等
                Cmp(a, C(8, 0));
                Jcc(IrCond.Equal, isFalse);
                Cmp(b, C(8, 0));
                Jcc(IrCond.Equal, isFalse);

                var lenA = NewReg(4);
                Load(lenA, a, 0, 4);
                var lenB = NewReg(4);
                Load(lenB, b, 0, 4);
                Cmp(lenA, lenB);
                Jcc(IrCond.NotEqual, isFalse);

                // 逐 2 字节字符比较（非 dword：奇数长度末 dword 含堆/数据区填充垃圾，误判不等）
                var ap = NewReg(8);
                Lea(ap, a, 4);
                var bp = NewReg(8);
                Lea(bp, b, 4);
                var count = NewReg(4);
                Mov(count, lenA);

                Mark(loop);
                Cmp(count, 0);
                Jcc(IrCond.Equal, isTrue);
                var charA = NewReg(4);
                Load(charA, ap, 0, 2);
                var charB = NewReg(4);
                Load(charB, bp, 0, 2);
                Cmp(charA, charB);
                Jcc(IrCond.NotEqual, isFalse);
                var nextAp = NewReg(8);
                Lea(nextAp, ap, 2);
                Mov(ap, nextAp);
                var nextBp = NewReg(8);
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
            // Substring(s:8, start:4, count:4) → 字符串对象
            // 参数非法（start/count < 0 或 start+count > 长度）时打印错误并退出
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
                Jcc(IrCond.Less, invalid);
                Cmp(count, 0);
                Jcc(IrCond.Less, invalid);
                var end = NewReg(4);
                Mov(end, start);
                Add(end, end, count);
                Cmp(end, len);
                Jcc(IrCond.Greater, invalid);

                var size = NewReg(4);
                Mov(size, count);
                Shl(size, size, 1);
                AddI(size, size, 3);
                And(size, size, C(4, ~3));
                AddI(size, size, 4);

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

                Store(obj, 0, count, 4);

                var dst = NewReg(8);
                Lea(dst, obj, 4);
                var src = NewReg(8);
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
                var message = NewReg(8);
                LeaData(message, _substringMessage);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // CharToString(c:4) → 单字符字符串对象（[len:4][char:2]）
            // ------------------------------------------------------------------

            private void EmitCharToString()
            {                var c = _args[0];
                var oom = NewLabel();
                var done = NewLabel();

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", C(4, 8));
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);

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
            // FormatInt(value:4, packed:4, exp:4) → 字符串对象
            // packed = (width:16 有符号) << 16 | (n:8) << 8 | (code:4) << 4 | (kind:4)
            // kind: 0=int 1=byte 2=enum 3=bool 4=char（value 为字符码）
            // code: 0=无 1=D 2=X 3=F 4=G 5=E
            // n：D/X 零填充位数；F 小数位数；G 有效数字位数；E 小数位数
            // exp：G/E 的十进制指数（F/int 传 0）
            // 对齐 width（负=左对齐）。bool/char 忽略格式。
            // ------------------------------------------------------------------

            // x64：SetArg(0, value) 调 DoubleToString；x86：value 拆 low/high 两参数。
            private void EmitCallDoubleToString(IrVirtualRegister strObj, IrVirtualRegister value, IrVirtualRegister valueHigh)
            {
                SetArg(0, value);
                if (!_isX64)
                {
                    SetArg(1, valueHigh);
                }
                CallRuntime(strObj, "DoubleToString");
            }

            // x64：SetArg(0, value) SetArg(1, n) 调 DoubleFixed；x86：value 拆 low/high + n。
            private void EmitCallDoubleFixed(IrVirtualRegister scaled, IrVirtualRegister value, IrVirtualRegister valueHigh, IrVirtualRegister n)
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

            // x64：SetArg(0, value) SetArg(1, n) 调 FormatFixed；x86：value 拆 low/high + n。
            private void EmitCallFormatFixed(IrVirtualRegister strObj, IrVirtualRegister value, IrVirtualRegister valueHigh, IrVirtualRegister n)
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

            // x64：SetArg(0, value) SetArg(1, n) SetArg(2, flags) 调 FormatSci（flags = lowerE<<1 | mode）；x86：value 拆 low/high + n + flags。
            private void EmitCallFormatSci(IrVirtualRegister strObj, IrVirtualRegister value, IrVirtualRegister valueHigh, IrVirtualRegister n, IrVirtualRegister flags)
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

            // StringFormat(value:8, fmtPtr:8, packed:4) → 字符串对象
            //   value   ：原始值（int/byte/enum 的低 4 字节；double 的 8 字节；string/bool/char 按各自宽度）
            //   fmtPtr  ：格式串指针（UTF-16，来自 InternString；长度存于 [fmtPtr+0]）
            //   packed  ：低 4 位 typeKind（0=int/byte/enum，1=double，2=string，3=bool，4=char），
            //             高 16 位（位 4..19）为有符号对齐宽度（负=左对齐，0=不填充）
            // 运行时解析格式串（code/n/lowerCase），统一所有类型到单一入口。
            private void EmitStringFormat()
            {
                IrVirtualRegister value;
                IrVirtualRegister valueHigh;
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
                Jcc(IrCond.LessOrEqual, wExtDone);
                AddI(width, width, -0x10000);
                Mark(wExtDone);

                var fmtLen = NewReg(4);
                Load(fmtLen, fmtPtr, 0, 4);

                var code = NewReg(4);
                var n = NewReg(4);
                var lowerCase = NewReg(4);
                ParseFormat(fmtPtr, fmtLen, code, n, lowerCase);

                var strObj = NewReg(8);
                var strLen = NewReg(4);
                var stringKind = NewLabel();
                var boolKind = NewLabel();
                var charKind = NewLabel();
                var intKind = NewLabel();
                var doubleKind = NewLabel();
                var applyAlign = NewLabel();

                Cmp(typeKind, 0); Jcc(IrCond.Equal, intKind);
                Cmp(typeKind, 1); Jcc(IrCond.Equal, doubleKind);
                Cmp(typeKind, 2); Jcc(IrCond.Equal, stringKind);
                Cmp(typeKind, 3); Jcc(IrCond.Equal, boolKind);
                Jmp(charKind);

                // ---- string：原样（对齐在末尾统一处理）----
                Mark(stringKind);
                Mov(strObj, value);
                Load(strLen, value, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);

                // ---- bool：True/False ----
                Mark(boolKind);
                var isTrue = NewLabel();
                Cmp(value, 0);
                Jcc(IrCond.NotEqual, isTrue);
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

                // ---- int/byte/enum：D → 十进零填；X/x → 十六进制；其余 → 十进制 ----
                Mark(intKind);
                var intHex = NewLabel();
                var intDecPad = NewLabel();
                var intPlain = NewLabel();
                Cmp(code, 1); Jcc(IrCond.Equal, intDecPad);
                Cmp(code, 2); Jcc(IrCond.Equal, intHex);
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

                // ---- double：F(n) → 预缩放后 scale-and-print；特殊值/其他格式 → DoubleToString ----
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
                Cmp(dHiAnd, dExpMask); Jcc(IrCond.Equal, dIsSpecial);
                Cmp(code, 3); Jcc(IrCond.Equal, dFmt);
                Cmp(code, 4); Jcc(IrCond.Equal, dSci);
                Cmp(code, 5); Jcc(IrCond.Equal, dSci);
                Jmp(dPlain);
                Mark(dIsSpecial);
                EmitCallDoubleToString(strObj, value, valueHigh);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dFmt);
                var dFmtOk = NewLabel();
                Cmp(n, 24); Jcc(IrCond.Greater, dPlain);
                Mark(dFmtOk);
                EmitCallFormatFixed(strObj, value, valueHigh, n);
                Load(strLen, strObj, 0, 4);
                Shl(strLen, strLen, 1);
                Jmp(applyAlign);
                Mark(dSci);
                var dSciOk = NewLabel();
                Cmp(n, 24); Jcc(IrCond.Greater, dPlain);
                Mark(dSciOk);
                // flags = lowerCase<<1 | (code==4 ? 1 : 0)
                var sciFlags = NewReg(4);
                var sciIsG = NewLabel();
                var sciModeDone = NewLabel();
                Cmp(code, 4);
                Jcc(IrCond.Equal, sciIsG);
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
                var aligned = NewReg(8);
                CallRuntime(aligned, "ApplyAlignment", strObj, strLen, width);
                Mov(strObj, aligned);

                StoreRet(strObj);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // PadString(str:8, width:4) → 字符串对象（对齐填充；pad ≤ 0 恒等）
            // ------------------------------------------------------------------

            private void EmitPadString()
            {
                var str = _args[0];
                var width = _args[1];
                var len = NewReg(4);
                Load(len, str, 0, 4);
                Shl(len, len, 1);
                var aligned = NewReg(8);
                CallRuntime(aligned, "ApplyAlignment", str, len, width);
                Mov(str, aligned);
                StoreRet(str);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>若 |width|*2 &gt; baseLen：重建为带空格填充的字符串（width&gt;0 右对齐 / 左对齐）。返回最终字符串寄存器（所有路径已定义）。</summary>
            private void EmitApplyAlignment()
            {
                var baseObj = _args[0];
                var baseLen = _args[1];
                var width = _args[2];
                var result = NewReg(8);
                Mov(result, baseObj);
                var alignDone = NewLabel();
                Cmp(width, 0);
                Jcc(IrCond.Equal, alignDone);
                var absWidth = NewReg(4);
                Mov(absWidth, width);
                var wIsNeg = NewLabel();
                var wAbsDone = NewLabel();
                Cmp(width, 0);
                Jcc(IrCond.Less, wIsNeg);
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
                Jcc(IrCond.LessOrEqual, alignDone);

                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var bufPos = NewReg(8);
                Mov(bufPos, buf);
                var len2 = NewReg(4);
                Const(len2, 0);
                var rightAlign = NewLabel();
                Cmp(width, 0);
                Jcc(IrCond.Greater, rightAlign);

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
                var obj2 = NewReg(8);
                CallRuntime(obj2, "AllocStringFromBuf", buf, len2);
                Mov(result, obj2);
                Mark(alignDone);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>把 baseObj 的全部字符拷到 bufPos 并推进（len 增加对应字节）。</summary>
            private void EmitCopyStrChars(IrVirtualRegister bufPos, IrVirtualRegister len, IrVirtualRegister srcObj)
            {
                var chars = NewReg(4);
                Load(chars, srcObj, 0, 4);
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                Mark(loop);
                Cmp(i, chars);
                Jcc(IrCond.GreaterOrEqual, done);
                var srcPos = NewReg(8);
                Lea(srcPos, srcObj, 4);
                var off = NewReg(4);
                Mov(off, i);
                Shl(off, off, 1);
                var dstPos = NewReg(8);
                Add(dstPos, srcPos, off);
                var ch = NewReg(4);
                Load(ch, dstPos, 0, 2);
                Store(bufPos, 0, ch, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>写入 count 个 ch（2 字节）到 bufPos 并推进。</summary>
            private void EmitWriteRepeatedChar(IrVirtualRegister bufPos, IrVirtualRegister len, IrVirtualRegister count, char ch)
            {
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                var chReg = C(4, ch);
                Mark(loop);
                Cmp(i, count);
                Jcc(IrCond.GreaterOrEqual, done);
                Store(bufPos, 0, chReg, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                var ni = NewReg(4);
                AddI(ni, i, 1);
                Mov(i, ni);
                Jmp(loop);
                Mark(done);
            }

            /// <summary>把 _formatBuffer 中 lenBytes 字节的 UTF-16 内容分配为字符串对象。</summary>
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
                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);
                var chars = NewReg(4);
                Mov(chars, lenBytes);
                Shr(chars, chars, 1);
                Store(obj, 0, chars, 4);
                var count = NewReg(4);
                Mov(count, lenBytes);
                AddI(count, count, 2);
                Shr(count, count, 2);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, buf, count);
                Jmp(done);
                Mark(oom);
                Const(obj, 0);
                Mark(done);
                StoreRet(obj);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>十六进制（不足 n 位前补 '0'，大写 X / 小写 x）：写入 _formatBuffer 起始，返回字符串对象。</summary>
            private void EmitFormatHex()
            {
                var value = _args[0];
                var minDigits = _args[1];
                var lowerCase = _args[2];
                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var tail = NewReg(8);
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
                Jcc(IrCond.Greater, isLetter);
                var digitCh = NewReg(4);
                AddI(digitCh, digit, '0');
                Mov(ch, digitCh);
                Jmp(chReady);
                Mark(isLetter);
                var letterCh = NewReg(4);
                var lowerA = NewLabel();
                Cmp(lowerCase, 0); Jcc(IrCond.Equal, lowerA);
                AddI(letterCh, digit, 'a' - 10);
                Mov(ch, letterCh);
                Jmp(chReady);
                Mark(lowerA);
                AddI(letterCh, digit, 'A' - 10);
                Mov(ch, letterCh);
                Jmp(chReady);
                Mark(chReady);
                var nt = NewReg(8);
                Lea(nt, tail, -2);
                Store(nt, 0, ch, 2);
                Mov(tail, nt);
                Mov(v, quotient);
                var nc = NewReg(4);
                AddI(nc, count, 1);
                Mov(count, nc);
                Cmp(v, 0);
                Jcc(IrCond.NotEqual, loop);
                Mark(done);

                // 前补 '0' 至 minDigits
                var padLoop = NewLabel();
                var padDone = NewLabel();
                Cmp(count, minDigits);
                Jcc(IrCond.GreaterOrEqual, padDone);
                Mark(padLoop);
                var pnt = NewReg(8);
                Lea(pnt, tail, -2);
                Store(pnt, 0, C(4, '0'), 2);
                Mov(tail, pnt);
                var pc = NewReg(4);
                AddI(pc, count, 1);
                Mov(count, pc);
                Cmp(count, minDigits);
                Jcc(IrCond.Less, padLoop);
                Mark(padDone);

                var end = NewReg(8);
                Lea(end, buf, 24);
                var lenBytes = NewReg(4);
                Sub(lenBytes, end, tail);
                var copyCount = NewReg(4);
                Mov(copyCount, lenBytes);
                AddI(copyCount, copyCount, 2);
                Shr(copyCount, copyCount, 2);
                CallRuntime(null, "CopyChars", buf, tail, copyCount);
                var built = NewReg(8);
                CallRuntime(built, "AllocStringFromBuf", buf, lenBytes);
                StoreRet(built);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>十进制零填充：|value| 的数字不足 n 位前补 '0'（负号在首位）。</summary>
            private void EmitFormatDecPad()
            {
                var value = _args[0];
                var n = _args[1];
                var str = NewReg(8);
                CallRuntime(str, "IntToString", value);
                var lenChars = NewReg(4);
                Load(lenChars, str, 0, 4);
                var first = NewReg(4);
                Load(first, str, 4, 2);
                var sign = NewReg(4);
                var isNeg = NewLabel();
                var signDone = NewLabel();
                Cmp(first, '-');
                Jcc(IrCond.Equal, isNeg);
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
                Jcc(IrCond.LessOrEqual, noPad);

                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var bufPos = NewReg(8);
                Mov(bufPos, buf);
                var lenBytes = NewReg(4);
                Const(lenBytes, 0);
                var negSign = NewLabel();
                var signSkip = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negSign);
                Jmp(signSkip);
                Mark(negSign);
                Store(bufPos, 0, C(4, '-'), 2);
                var nl = NewReg(4);
                AddI(nl, lenBytes, 2);
                Mov(lenBytes, nl);
                var nb = NewReg(8);
                Lea(nb, bufPos, 2);
                Mov(bufPos, nb);
                Mark(signSkip);

                EmitWriteRepeatedChar(bufPos, lenBytes, pad, '0');
                var digitOffset = NewReg(4);
                Mov(digitOffset, sign);
                EmitCopyDigits(bufPos, lenBytes, str, digitOffset, digitsLen);
                var builtObj = NewReg(8);
                CallRuntime(builtObj, "AllocStringFromBuf", buf, lenBytes);
                var result = NewReg(8);
                Mov(result, builtObj);
                Jmp(buildDone);
                Mark(noPad);
                Mov(result, str);
                Mark(buildDone);
                StoreRet(result);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>scale-and-print：把 scaled（int32）按 code 组装（F：n 小数位；G：exp+1 整数位；E：1 整数位 + E±exp）。</summary>
            private void EmitScaleAssemble()
            {
                var value = _args[0];
                var code = _args[1];
                var n = _args[2];
                var exp = _args[3];
                var str = NewReg(8);
                CallRuntime(str, "IntToString", value);
                var lenChars = NewReg(4);
                Load(lenChars, str, 0, 4);
                var first = NewReg(4);
                Load(first, str, 4, 2);
                var sign = NewReg(4);
                var isNeg = NewLabel();
                var signDone = NewLabel();
                Cmp(first, '-');
                Jcc(IrCond.Equal, isNeg);
                Const(sign, 0);
                Jmp(signDone);
                Mark(isNeg);
                Const(sign, 1);
                Mark(signDone);
                var digitsLen = NewReg(4);
                Sub(digitsLen, lenChars, sign);

                // 整数位个数 K
                var K = NewReg(4);
                var isF = NewLabel();
                var isG = NewLabel();
                var kReady = NewLabel();
                Cmp(code, 3);
                Jcc(IrCond.Equal, isF);
                Cmp(code, 4);
                Jcc(IrCond.Equal, isG);
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

                var buf = NewReg(8);
                LeaData(buf, _formatBuffer);
                var bufPos = NewReg(8);
                Mov(bufPos, buf);
                var lenBytes = NewReg(4);
                Const(lenBytes, 0);
                var negSign = NewLabel();
                var signSkip = NewLabel();
                Cmp(sign, 0);
                Jcc(IrCond.NotEqual, negSign);
                Jmp(signSkip);
                Mark(negSign);
                Store(bufPos, 0, C(4, '-'), 2);
                var nl = NewReg(4);
                AddI(nl, lenBytes, 2);
                Mov(lenBytes, nl);
                var nb = NewReg(8);
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
                Jcc(IrCond.GreaterOrEqual, intBig);
                Cmp(K, 0);
                Jcc(IrCond.Greater, intOk);
                // K <= 0：整数 '0' + '.' + (-K) 个 '0' + 全部 digits
                Store(bufPos, 0, C(4, '0'), 2);
                var nl0 = NewReg(4);
                AddI(nl0, lenBytes, 2);
                Mov(lenBytes, nl0);
                var nb0 = NewReg(8);
                Lea(nb0, bufPos, 2);
                Mov(bufPos, nb0);
                Jmp(intZero);

                Mark(intBig);
                // 全部 digits + (K-digitsLen) 个 '0'
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
                var nbd = NewReg(8);
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
                var nbz = NewReg(8);
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
                Jcc(IrCond.NotEqual, eSuffixDone);
                Store(bufPos, 0, C(4, 'E'), 2);
                var nle = NewReg(4);
                AddI(nle, lenBytes, 2);
                Mov(lenBytes, nle);
                var nbe = NewReg(8);
                Lea(nbe, bufPos, 2);
                Mov(bufPos, nbe);
                var expNeg = NewLabel();
                var expSignDone = NewLabel();
                Cmp(exp, 0);
                Jcc(IrCond.Less, expNeg);
                Store(bufPos, 0, C(4, '+'), 2);
                Jmp(expSignDone);
                Mark(expNeg);
                Store(bufPos, 0, C(4, '-'), 2);
                Neg(exp);
                Mark(expSignDone);
                var nls = NewReg(4);
                AddI(nls, lenBytes, 2);
                Mov(lenBytes, nls);
                var nbs = NewReg(8);
                Lea(nbs, bufPos, 2);
                Mov(bufPos, nbs);
                var expStr = NewReg(8);
                CallRuntime(expStr, "IntToString", exp);
                var expZero = NewReg(4);
                Const(expZero, 0);
                var expLen = NewReg(4);
                Load(expLen, expStr, 0, 4);
                EmitCopyDigits(bufPos, lenBytes, expStr, expZero, expLen);
                Mark(eSuffixDone);

                var built = NewReg(8);
                CallRuntime(built, "AllocStringFromBuf", buf, lenBytes);
                StoreRet(built);
                EndFunction(_currentFunction!, 8);
            }

            /// <summary>把 srcObj 从 startChar 起 countChar 个字符拷到 bufPos 并推进。</summary>
            private void EmitCopyDigits(IrVirtualRegister bufPos, IrVirtualRegister len, IrVirtualRegister srcObj, IrVirtualRegister startChar, IrVirtualRegister countChar)
            {
                var loop = NewLabel();
                var done = NewLabel();
                var i = NewReg(4);
                Const(i, 0);
                Mark(loop);
                Cmp(i, countChar);
                Jcc(IrCond.GreaterOrEqual, done);
                var idx = NewReg(4);
                Add(idx, startChar, i);
                var srcPos = NewReg(8);
                Lea(srcPos, srcObj, 4);
                var off = NewReg(4);
                Mov(off, idx);
                Shl(off, off, 1);
                var dstPos = NewReg(8);
                Add(dstPos, srcPos, off);
                var ch = NewReg(4);
                Load(ch, dstPos, 0, 2);
                Store(bufPos, 0, ch, 2);
                var nl = NewReg(4);
                AddI(nl, len, 2);
                Mov(len, nl);
                var nb = NewReg(8);
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

                var handle = NewReg(8);
                SysCall(handle, "GetStdHandle", 1, C(4, -10));
                var fileType = NewReg(4);
                SysCall(fileType, "GetFileType", 1, handle);

                var buf = NewReg(8);
                LeaData(buf, _inputBuffer);
                var written = NewReg(4);
                var writtenAddr = NewReg(8);
                LeaSlot(writtenAddr, written);

                Cmp(fileType, 2);
                Jcc(IrCond.NotEqual, notConsole);

                var chars = NewReg(4);
                var ok = NewReg(4);
                SysCall(ok, "ReadConsoleW", 5, handle, buf, C(4, 0x1000), writtenAddr);
                Cmp(ok, 0);
                Jcc(IrCond.Equal, fail);
                Load(chars, writtenAddr, 0, 4);
                Jmp(haveCount);

                Mark(notConsole);
                var okFile = NewReg(4);
                SysCall(okFile, "ReadFile", 5, handle, buf, C(4, 0x2000), writtenAddr);
                Cmp(okFile, 0);
                Jcc(IrCond.Equal, fail);
                var bytes = NewReg(4);
                Load(bytes, writtenAddr, 0, 4);
                Mov(chars, bytes);
                Shr(chars, chars, 1);

                Mark(haveCount);
                // 去尾部 \r \n
                Mark(strip);
                Cmp(chars, 0);
                Jcc(IrCond.Equal, stripped);
                var idx = NewReg(4);
                Mov(idx, chars);
                Shl(idx, idx, 1);
                var addr = NewReg(8);
                Add(addr, buf, idx);
                var last = NewReg(4);
                Load(last, addr, -2, 2);
                Cmp(last, 0x0D);
                Jcc(IrCond.Equal, pop);
                Cmp(last, 0x0A);
                Jcc(IrCond.NotEqual, stripped);

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

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", size);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, fail);
                Store(obj, 0, chars, 4);

                var count = NewReg(4);
                Mov(count, chars);
                AddI(count, count, 1);
                Shr(count, count, 1);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                CallRuntime(null, "CopyChars", dst, buf, count);
                StoreRet(obj);
                Jmp(done);

                Mark(fail);
                var empty = NewReg(8);
                LeaData(empty, _emptyString);
                StoreRet(empty);

                Mark(done);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ReadKey(intercept:4) → char
            // 读取单键。intercept=0 时回显（WriteConsoleW）；=1 时不回显。
            // 用 ReadConsoleInputW 读 INPUT_RECORD，取 KEY_EVENT 且 bKeyDown 的 UnicodeChar。
            // ------------------------------------------------------------------

            private void EmitReadKey()
            {
                var intercept = _args[0];
                var inHandle = NewReg(8);
                SysCall(inHandle, "GetStdHandle", 1, C(4, -10));

                var buf = NewReg(8);
                LeaData(buf, _inputBuffer);
                var written = NewReg(4);
                var writtenAddr = NewReg(8);
                LeaSlot(writtenAddr, written);

                var loop = NewLabel();
                var gotKey = NewLabel();
                var done = NewLabel();

                Mark(loop);
                var ok = NewReg(4);
                SysCall(ok, "ReadConsoleInputW", 4, inHandle, buf, C(4, 1), writtenAddr);
                Cmp(ok, 0);
                Jcc(IrCond.Equal, loop);
                var count = NewReg(4);
                Load(count, writtenAddr, 0, 4);
                Cmp(count, 0);
                Jcc(IrCond.Equal, loop);

                var eventType = NewReg(4);
                Load(eventType, buf, 0, 2);
                Cmp(eventType, 1);
                Jcc(IrCond.NotEqual, loop);

                var keyDown = NewReg(4);
                Load(keyDown, buf, 4, 4);
                Cmp(keyDown, 0);
                Jcc(IrCond.Equal, loop);

                // 若 intercept=0，回显该字符（WriteConsoleW 到输出句柄）
                Cmp(intercept, 0);
                Jcc(IrCond.NotEqual, gotKey);
                var outHandle = NewReg(8);
                SysCall(outHandle, "GetStdHandle", 1, C(4, -11));
                var charAddr = NewReg(8);
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

                var state = NewReg(8);
                LeaData(state, _rngState);
                var seed = NewReg(4);
                Load(seed, state, 0, 4);
                Cmp(seed, 0);
                Jcc(IrCond.NotEqual, ready);

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
                Jcc(IrCond.LessOrEqual, zero);
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
                Setcc(result, IrCond.Equal);
                StoreRet(result);
                EndFunction(_currentFunction!, 4);
            }

            // ------------------------------------------------------------------
            // ObjectToString(obj:ptr) → str（6e-M19 M4：读对象头 vtable → 名字指针）
            // 对象布局 [0]=vtablePtr；vtable [8]=类型全名字符串指针（伪记录自引用使 Type 值同样成立）
            // ------------------------------------------------------------------

            private void EmitObjectToString()
            {
                var obj = _args[0];
                var vtable = NewReg(8);
                Load(vtable, obj, 0, _isX64 ? 8 : 4);
                var name = NewReg(8);
                Load(name, vtable, 8, _isX64 ? 8 : 4);
                StoreRet(name);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // ObjectGetHashCode(x:8) → int（指针/位模式散列：lo ^ hi 后乘黄金比例常数）
            // x86 指针宽 4，仅低 dword 参与散列
            // ------------------------------------------------------------------

            private void EmitObjectGetHashCode()
            {
                var value = _args[0];
                var lo = NewReg(4);
                LoadSlotField(lo, value, 0, 4);

                IrVirtualRegister mixed;
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
                var vtable = NewReg(8);
                Load(vtable, obj, 0, _isX64 ? 8 : 4);
                StoreRet(vtable);
                EndFunction(_currentFunction!, 8);
            }

            // ------------------------------------------------------------------
            // TypeSimpleName(s:str) → str（最后一个 '.' 之后的部分；无点回退原串。
            // 与 IL FullName.Substring(LastIndexOf('.')+1) 组合语义一致，三后端统一）
            // ------------------------------------------------------------------

            private void EmitTypeSimpleName()
            {
                var s = _args[0];
                var len = NewReg(4);
                Load(len, s, 0, 4);

                var chars = NewReg(8);
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
                Jcc(IrCond.AboveOrEqual, scanDone);
                var ch = NewReg(4);
                {
                    // chars[i]：基址寄存器可变偏移经 Lea+Add 组合
                    var offset = NewReg(4);
                    Mov(offset, index);
                    Shl(offset, offset, 1);
                    var address = NewReg(8);
                    Lea(address, chars, 0);
                    Add(address, address, offset);
                    Load(ch, address, 0, 2);
                }

                Cmp(ch, '.');
                Jcc(IrCond.NotEqual, scanContinue);
                Mov(lastDot, index);

                Mark(scanContinue);
                AddI(index, index, 1);
                Jmp(loop);

                Mark(scanDone);
                Cmp(lastDot, 0);
                Jcc(IrCond.Less, noDot);

                var start = NewReg(4);
                AddI(start, lastDot, 1);
                var count = NewReg(4);
                Sub(count, len, start);
                var result = NewReg(8);
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
            // NewArray(size:4, elementSize:4) → ptr:8
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

                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", total);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, oom);
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
            // ArrayBoundsCheck(index:4, length:4) — 越界时报错退出
            // ------------------------------------------------------------------

            private void EmitArrayBoundsCheck()
            {
                var index = _args[0];
                var length = _args[1];
                var error = NewLabel();

                Cmp(index, 0);
                Jcc(IrCond.Less, error);
                Cmp(index, length);
                Jcc(IrCond.GreaterOrEqual, error);
                EndFunction(_currentFunction!, 0);

                Mark(error);
                var message = NewReg(8);
                LeaData(message, _arrayBoundsMessage);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            private void EmitError(string messageKey)
            {
                var message = NewReg(8);
                LeaData(message, messageKey);
                CallRuntime(null, "PrintString", message);
                CallRuntime(null, "ExitProcess", C(4, 1));
                EndFunction(_currentFunction!, 0);
            }

            // ------------------------------------------------------------------
            // BuildArgs() → ptr（string[]）
            // 用 GetCommandLineW 读取命令行（UTF-16），跳过程序名，按 MS 风格解析
            // 剩余参数：空白（空格/制表符）分隔；引号包裹的空白不分割；引号本身从
            // 参数内容中剥离。构建 string[]（布局同 NewArray），失败（OOM）返回 0。
            // ------------------------------------------------------------------

            private void EmitBuildArgs()
            {
                var elementSize = _isX64 ? 8 : 4;

                var cmd = NewReg(8);
                SysCall(cmd, "GetCommandLineW", 0);

                var p = NewReg(8);
                Mov(p, cmd);
                var inQuotes = C(4, 0);
                var ch = NewReg(4);
                var count = C(4, 0);

                // ---- 定位程序名后的第一个参数位置（first）----
                var skipProg = NewLabel();
                var skipProgCheck = NewLabel();
                var skipProgNext = NewLabel();
                var skipProgFound = NewLabel();

                Mark(skipProg);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, skipProgFound);
                Cmp(ch, 34);
                Jcc(IrCond.NotEqual, skipProgCheck);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(skipProgNext);
                Mark(skipProgCheck);
                Cmp(inQuotes, 0);
                Jcc(IrCond.NotEqual, skipProgNext);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, skipProgFound);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, skipProgFound);
                Mark(skipProgNext);
                Lea(p, p, 2);
                Jmp(skipProg);

                Mark(skipProgFound);
                var first = NewReg(8);
                Mov(first, p);

                // ---- pass 1: 计数（count）----
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
                Jcc(IrCond.Equal, countWsNext);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, countWsNext);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, countDone);
                Jmp(countTok);
                Mark(countWsNext);
                Lea(p, p, 2);
                Jmp(countWs);

                Mark(countTok);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, countTokEnd);
                Cmp(ch, 34);
                Jcc(IrCond.NotEqual, countTokNoQuote);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(countTokNext);
                Mark(countTokNoQuote);
                Cmp(inQuotes, 0);
                Jcc(IrCond.NotEqual, countTokNext);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, countTokEnd);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, countTokEnd);
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
                var arr = NewReg(8);
                SetArg(0, count);
                SetArg(1, elementSizeReg);
                Add(IrOpCode.Call, arr, IrOperand.Runtime("NewArray"), IrOperand.Constant(0));

                var oom = NewLabel();
                var finish = NewLabel();
                var done = NewLabel();
                Cmp(arr, 0);
                Jcc(IrCond.Equal, oom);

                // ---- pass 2: 逐个参数构造 string 并写入数组 ----
                Mov(p, first);
                var slot = NewReg(8);
                var slotBase = NewReg(8);
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
                Jcc(IrCond.Equal, buildWsNext);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, buildWsNext);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, finish);
                Jmp(buildTok);
                Mark(buildWsNext);
                Lea(p, p, 2);
                Jmp(buildWs);

                Mark(buildTok);
                var start = NewReg(8);
                Mov(start, p);
                var lenChars = C(4, 0);
                Jmp(buildTokScan);

                Mark(buildTokNext);
                Lea(p, p, 2);

                Mark(buildTokScan);
                Load(ch, p, 0, 2);
                Cmp(ch, 0);
                Jcc(IrCond.Equal, buildStr);
                Cmp(ch, 34);
                Jcc(IrCond.NotEqual, buildTokNoQuote);
                Xor(inQuotes, inQuotes, C(4, 1));
                Jmp(buildTokNext);
                Mark(buildTokNoQuote);
                Cmp(inQuotes, 0);
                Jcc(IrCond.NotEqual, buildTokChar);
                Cmp(ch, 32);
                Jcc(IrCond.Equal, buildStr);
                Cmp(ch, 9);
                Jcc(IrCond.Equal, buildStr);
                Mark(buildTokChar);
                AddI(lenChars, lenChars, 1);
                Jmp(buildTokNext);

                // ---- 构造字符串：Alloc(lenChars*2+4 对齐 4)，剥离引号拷贝 ----
                Mark(buildStr);
                var bytes = NewReg(4);
                Mov(bytes, lenChars);
                Shl(bytes, bytes, 1);
                AddI(bytes, bytes, 4);
                AddI(bytes, bytes, 3);
                And(bytes, bytes, C(4, 0xFFFFFFFC));
                var obj = NewReg(8);
                CallRuntime(obj, "Alloc", bytes);
                Cmp(obj, 0);
                Jcc(IrCond.Equal, buildStrNext);

Store(obj, 0, lenChars, 4);
                var dst = NewReg(8);
                Lea(dst, obj, 4);
                var src = NewReg(8);
                Mov(src, start);
                var remaining = NewReg(4);
                Mov(remaining, lenChars);

                Mark(copyLoop);
                Cmp(remaining, 0);
                Jcc(IrCond.Equal, copyDone);
                Load(ch, src, 0, 2);
                Cmp(ch, 34);
                Jcc(IrCond.Equal, copySkip);
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
        }
    }
}
