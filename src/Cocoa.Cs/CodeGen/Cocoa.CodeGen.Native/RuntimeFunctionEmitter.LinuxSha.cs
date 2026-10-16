using System;

using Cocoa.CodeGen.Native.Lir;

using Cocoa.CodeAnalysis;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// T3 N5b：Linux SHA-256（FIPS 180-4，单块 ≤56 字节输入）——纯 LIR，零依赖。
    /// C# 循环展开：W 衍生（16→63）+ 64 轮压缩；大端字读写；返回 32 字节 u8[]。
    /// 校验向量："abc" → BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD；
    /// 空串 → E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855。
    /// </summary>
    internal sealed partial class RuntimeFunctionEmitter
    {
        private static readonly uint[] Sha256K =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        private static readonly uint[] Sha256HInit =
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        };

        private string _linuxShaK = "";
        private string _linuxShaW = "";
        private string _linuxShaBlock = "";

        private void AddShaDataItems()
        {
            var kBytes = new byte[64 * 4];
            for (var i = 0; i < Sha256K.Length; i++)
            {
                PutU32LE(kBytes, i * 4, Sha256K[i]);
            }

            _linuxShaK = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxShaK", kBytes));
            _linuxShaW = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxShaW", new byte[64 * 4]));
            _linuxShaBlock = _program.AddData(LirDataItem.ByteArray(Prefix + "LinuxShaBlock", new byte[64]));
        }

        private static void PutU32LE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        // ------------------------------------------------------------------
        // Sha256Hash(data:8 u8[]) → u8[]（32 字节；输入 ≤56 字节，单 64B 块 + 填充）
        // ------------------------------------------------------------------

        private void EmitSha256HashLinux()
        {
            var data = _args[0];
            var fail = NewLabel();
            var done = NewLabel();
            var result = NewPtr();

            var len = NewReg(4);
            Load(len, data, NativeObjectModel.ArrayLengthOffset, 4);
            var src = NewPtr();
            Lea(src, data, NativeObjectModel.ArrayElementOffset);

            // ---- 构造 64B 块：清零 → 拷贝输入 → 0x80 → 长度(位, 大端 @56) ----
            var block = NewPtr();
            LeaData(block, _linuxShaBlock);
            var ci = NewReg(4);
            Const(ci, 0);
            var clearLoop = NewLabel();
            var clearDone = NewLabel();
            Mark(clearLoop);
            Cmp(ci, 64);
            Jcc(LirCond.GreaterOrEqual, clearDone);
            var cbAddr = NewPtr();
            Mov(cbAddr, block);
            Add(cbAddr, cbAddr, ci);
            Store(cbAddr, 0, C(4, 0), 1);
            AddI(ci, ci, 1);
            Jmp(clearLoop);
            Mark(clearDone);

            // 拷贝输入字节
            Const(ci, 0);
            var copyLoop = NewLabel();
            var copyDone = NewLabel();
            Mark(copyLoop);
            Cmp(ci, len);
            Jcc(LirCond.GreaterOrEqual, copyDone);
            var cbAddr2 = NewPtr();
            Mov(cbAddr2, block);
            Add(cbAddr2, cbAddr2, ci);
            var srcAddr = NewPtr();
            Mov(srcAddr, src);
            Add(srcAddr, srcAddr, ci);
            var inByte = NewReg(4);
            Load(inByte, srcAddr, 0, 1);
            Store(cbAddr2, 0, inByte, 1);
            AddI(ci, ci, 1);
            Jmp(copyLoop);
            Mark(copyDone);

            // 0x80 填充
            var padAddr = NewPtr();
            Mov(padAddr, block);
            Add(padAddr, padAddr, len);
            Store(padAddr, 0, C(4, 0x80), 1);

            // 长度位（len*8）大端 @56..63
            var bits = NewReg(4);
            Mov(bits, len);
            Shl(bits, bits, 3);
            Store(block, 56, C(4, 0), 1);
            Store(block, 57, C(4, 0), 1);
            Store(block, 58, C(4, 0), 1);
            Store(block, 59, C(4, 0), 1);
            var lenByte = NewReg(4);
            Mov(lenByte, bits);
            Shr(lenByte, lenByte, 24);
            Store(block, 60, lenByte, 1);
            Mov(lenByte, bits);
            Shr(lenByte, lenByte, 16);
            And(lenByte, lenByte, C(4, 0xFF));
            Store(block, 61, lenByte, 1);
            Mov(lenByte, bits);
            Shr(lenByte, lenByte, 8);
            And(lenByte, lenByte, C(4, 0xFF));
            Store(block, 62, lenByte, 1);
            Mov(lenByte, bits);
            And(lenByte, lenByte, C(4, 0xFF));
            Store(block, 63, lenByte, 1);

            // ---- W 槽：16 个大端字 + 衍生至 63 ----
            var wBase = NewPtr();
            LeaData(wBase, _linuxShaW);
            var W = new LirVirtualRegister[64];
            for (var i = 0; i < 16; i++)
            {
                W[i] = EmitShaBigEndianWord(block, i * 4);
                EmitShaStoreW(wBase, i, W[i]);
            }

            for (var i = 16; i < 64; i++)
            {
                // W[i] = σ1(W[i-2]) + W[i-7] + σ0(W[i-15]) + W[i-16]
                var s1 = EmitShaSigma1(W[i - 2]);
                var sum = NewReg(4);
                Add(sum, s1, W[i - 7]);
                var s0 = EmitShaSigma0(W[i - 15]);
                Add(sum, sum, s0);
                Add(sum, sum, W[i - 16]);
                W[i] = sum;
                EmitShaStoreW(wBase, i, W[i]);
            }

            // ---- 初始 H ----
            var H = new LirVirtualRegister[8];
            var init = new LirVirtualRegister[8];
            for (var i = 0; i < 8; i++)
            {
                H[i] = NewReg(4);
                init[i] = NewReg(4);
                Const(H[i], (long)Sha256HInit[i]);
                Mov(init[i], H[i]);
            }

            // ---- 64 轮压缩 ----
            var kBase = NewPtr();
            LeaData(kBase, _linuxShaK);
            var a = H[0]; var b = H[1]; var c = H[2]; var d = H[3];
            var e = H[4]; var f = H[5]; var g = H[6]; var h = H[7];
            for (var r = 0; r < 64; r++)
            {
                // Σ1(e) = ROTR6 ^ ROTR11 ^ ROTR25
                var sum1 = NewReg(4);
                Xor(sum1, EmitShaRotR(e, 6), EmitShaRotR(e, 11));
                Xor(sum1, sum1, EmitShaRotR(e, 25));
                // ch(e,f,g) = (e&f)^(~e&g)
                var notE = NewReg(4);
                Mov(notE, e);
                Xor(notE, notE, C(4, -1));
                var ch = NewReg(4);
                var ef = NewReg(4);
                And(ef, e, f);
                var negG = NewReg(4);
                And(negG, notE, g);
                Xor(ch, ef, negG);
                // t1 = h + Σ1 + ch + K[r] + W[r]
                var kVal = NewReg(4);
                Load(kVal, kBase, r * 4, 4);
                var t1 = NewReg(4);
                Add(t1, h, sum1);
                Add(t1, t1, ch);
                Add(t1, t1, kVal);
                Add(t1, t1, W[r]);
                // Σ0(a) = ROTR2 ^ ROTR13 ^ ROTR22
                var sum0 = NewReg(4);
                Xor(sum0, EmitShaRotR(a, 2), EmitShaRotR(a, 13));
                Xor(sum0, sum0, EmitShaRotR(a, 22));
                // maj(a,b,c) = (a&b)^(a&c)^(b&c)
                var ab = NewReg(4);
                And(ab, a, b);
                var ac = NewReg(4);
                And(ac, a, c);
                var bc = NewReg(4);
                And(bc, b, c);
                var maj = NewReg(4);
                Xor(maj, ab, ac);
                Xor(maj, maj, bc);
                // t2 = Σ0 + maj
                var t2 = NewReg(4);
                Add(t2, sum0, maj);
                // 新 e = d + t1；新 a = t1 + t2；其余顺移
                var newE = NewReg(4);
                Add(newE, d, t1);
                var newA = NewReg(4);
                Add(newA, t1, t2);
                h = g; g = f; f = e; e = newE; d = c; c = b; b = a; a = newA;
            }

            // ---- 结果 = 初始 H + 压缩状态；32 字节大端 ----
            var arrayVt = NewPtr();
            LeaData(arrayVt, NativeObjectModel.PseudoVTableKey("System.Array"));
            CallRuntime(result, "NewArray", C(4, 32), C(4, 1), arrayVt);
            Cmp(result, 0);
            Jcc(LirCond.Equal, fail);

            var outBase = NewPtr();
            Lea(outBase, result, NativeObjectModel.ArrayElementOffset);
            var final = new LirVirtualRegister[] { a, b, c, d, e, f, g, h };
            for (var i = 0; i < 8; i++)
            {
                var value = NewReg(4);
                Add(value, final[i], init[i]);
                EmitShaStoreBigEndian(outBase, i * 4, value);
            }

            Jmp(done);
            Mark(fail);
            CallRuntime(result, "NewArray", C(4, 0), C(4, 1), arrayVt);
            Mark(done);
            StoreRet(result);
            EndFunction(_currentFunction!, 8);
        }

        // ---- SHA-256 微原语（vreg 上展开 LIR）----

        private LirVirtualRegister EmitShaRotR(LirVirtualRegister x, int n)
        {
            var r = NewReg(4);
            var s1 = NewReg(4);
            Mov(s1, x);
            Shr(s1, s1, n);
            var s2 = NewReg(4);
            Mov(s2, x);
            Shl(s2, s2, 32 - n);
            Or(r, s1, s2);
            return r;
        }

        private LirVirtualRegister EmitShaSigma0(LirVirtualRegister x)
        {
            var t = NewReg(4);
            Xor(t, EmitShaRotR(x, 7), EmitShaRotR(x, 18));
            var sh = NewReg(4);
            Mov(sh, x);
            Shr(sh, sh, 3);
            Xor(t, t, sh);
            return t;
        }

        private LirVirtualRegister EmitShaSigma1(LirVirtualRegister x)
        {
            var t = NewReg(4);
            Xor(t, EmitShaRotR(x, 17), EmitShaRotR(x, 19));
            var sh = NewReg(4);
            Mov(sh, x);
            Shr(sh, sh, 10);
            Xor(t, t, sh);
            return t;
        }

        private LirVirtualRegister EmitShaBigEndianWord(LirVirtualRegister basePtr, int byteOff)
        {
            var w = NewReg(4);
            var t = NewReg(4);
            Load(t, basePtr, byteOff, 1);
            Shl(t, t, 24);
            Mov(w, t);
            Load(t, basePtr, byteOff + 1, 1);
            Shl(t, t, 16);
            Or(w, w, t);
            Load(t, basePtr, byteOff + 2, 1);
            Shl(t, t, 8);
            Or(w, w, t);
            Load(t, basePtr, byteOff + 3, 1);
            Or(w, w, t);
            return w;
        }

        private void EmitShaStoreW(LirVirtualRegister wBase, int index, LirVirtualRegister value)
        {
            var addr = NewPtr();
            Mov(addr, wBase);
            var off = NewReg(4);
            Const(off, index * 4);
            Add(addr, addr, off);
            Store(addr, 0, value, 4);
        }

        private void EmitShaStoreBigEndian(LirVirtualRegister outBase, int byteOff, LirVirtualRegister value)
        {
            var t = NewReg(4);
            Mov(t, value);
            Shr(t, t, 24);
            Store(outBase, byteOff, t, 1);
            Mov(t, value);
            Shr(t, t, 16);
            And(t, t, C(4, 0xFF));
            Store(outBase, byteOff + 1, t, 1);
            Mov(t, value);
            Shr(t, t, 8);
            And(t, t, C(4, 0xFF));
            Store(outBase, byteOff + 2, t, 1);
            Mov(t, value);
            And(t, t, C(4, 0xFF));
            Store(outBase, byteOff + 3, t, 1);
        }
    }
}