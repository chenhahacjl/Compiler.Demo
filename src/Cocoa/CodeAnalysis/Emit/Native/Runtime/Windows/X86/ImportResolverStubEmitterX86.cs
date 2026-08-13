using System;
using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X86
{
    /// <summary>
    /// Self-bootstrapping entry stub (x86): PEB -> find kernel32 -> resolve export names -> fill IAT slots -> jump to user entry.
    /// 与 x64 版逻辑镜像，区别：FS 段访问 TEB（+0x30 = PEB）、PEB 结构偏移、PE32 导出目录偏移（+0x78）、4 字节 IAT 槽。
    /// </summary>
    internal static class ImportResolverStubEmitterX86
    {
        public static void Emit(IAssembler a, int entryPointLabel, IReadOnlyList<PefileImport> imports, int dataRva)
        {
            a.Sub(X64Size.Dword, X64Register.ESP, 4); // [esp] = ANSI 小写折叠 mask
            a.Mov(X64Size.Dword, X64Register.EAX, 0x20202020);
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.ESP, 0), X64Register.EAX);

            a.MovGs(X64Register.EAX, 0x30); // PEB
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0x0C)); // Ldr
            a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.EAX, 0x0C)); // InLoadOrderModuleList.Flink

            var findLoop = a.CreateLabel();
            var findNext = a.CreateLabel();
            var fail = a.CreateLabel();

            a.MarkLabel(findLoop);
            a.Mov(X64Size.Dword, X64Register.EBX, new X64MemoryOperand(X64Register.ESI, 0x18)); // DllBase
            a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.ESI, 0x30)); // BaseDllName.Buffer
            a.Test(X64Size.Dword, X64Register.EDX, X64Register.EDX);
            a.Jcc(X64CondCode.Equal, findNext);

            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 0));
            a.Or(X64Size.Dword, X64Register.EAX, 0x00200020);
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x0065006B); // "KE" (folded)
            a.Jcc(X64CondCode.NotEqual, findNext);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 4));
            a.Or(X64Size.Dword, X64Register.EAX, 0x00200020);
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x006E0072); // "RN"
            a.Jcc(X64CondCode.NotEqual, findNext);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 8));
            a.Or(X64Size.Dword, X64Register.EAX, 0x00200020);
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x006C0065); // "EL"
            a.Jcc(X64CondCode.NotEqual, findNext);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 12));
            a.Or(X64Size.Dword, X64Register.EAX, 0x00200020);
            a.Cmp(X64Size.Dword, X64Register.EAX, 0x00320033); // "32" (digits unchanged by OR 0x20)
            a.Jcc(X64CondCode.NotEqual, findNext);

            // "KERNEL32.DLL" in UTF-16: 'D' at byte 18, 'L' at byte 20
            a.Movzx(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 18));
            a.Or(X64Size.Dword, X64Register.EAX, 0x20);
            a.Cmp(X64Size.Dword, X64Register.EAX, 'd');
            a.Jcc(X64CondCode.NotEqual, findNext);
            a.Movzx(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 20));
            a.Or(X64Size.Dword, X64Register.EAX, 0x20);
            a.Cmp(X64Size.Dword, X64Register.EAX, 'l');
            a.Jcc(X64CondCode.NotEqual, findNext);

            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0x3C)); // e_lfanew
            a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX);
            a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0x78)); // export dir RVA (PE hdr + 0x18 optional hdr + 0x60 DataDirectory[0])
            a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // export dir VA
            a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX);

            a.Mov(X64Size.Dword, X64Register.EBP, new X64MemoryOperand(X64Register.EDI, 0x1C)); // AddressOfFunctions
            a.Add(X64Size.Dword, X64Register.EBP, X64Register.EBX);

            var setupLabels = new int[imports.Count];
            for (var i = 0; i < imports.Count; i++)
            {
                setupLabels[i] = a.CreateLabel();
            }

            for (var i = 0; i < imports.Count; i++)
            {
                var last = i == imports.Count - 1;
                var nameParts = AnsiWords(imports[i].Name);

                var nameLoop = a.CreateLabel();
                var nameNext = a.CreateLabel();

                a.MarkLabel(setupLabels[i]);
                a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.EDI, 0x20)); // AddressOfNames
                a.Add(X64Size.Dword, X64Register.ESI, X64Register.EBX);
                a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.EDI, 0x24)); // AddressOfNameOrdinals
                a.Add(X64Size.Dword, X64Register.EDX, X64Register.EBX);
                a.Xor(X64Size.Dword, X64Register.ECX, X64Register.ECX);

                a.MarkLabel(nameLoop);
                a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESI, 0)); // name RVA
                a.Add(X64Size.Dword, X64Register.ESI, 4);
                a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // name VA
                a.Push(X64Register.EAX); // [esp] = name VA, [esp+4] = mask

                for (var k = 0; k < nameParts.Count; k++)
                {
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESP, 0)); // name VA
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, k * 4));
                    a.Or(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESP, 4));
                    a.And(X64Size.Dword, X64Register.EAX, nameParts[k].Mask);
                    a.Cmp(X64Size.Dword, X64Register.EAX, nameParts[k].Word);
                    a.Jcc(X64CondCode.NotEqual, nameNext);
                }

                a.Movzx(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 0)); // ordinal value
                a.Shl(X64Size.Dword, X64Register.EAX, 2);
                a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBP);
                a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0)); // func RVA
                a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // func VA

                a.Push(X64Register.EAX); // save func VA
                a.MovGs(X64Register.EAX, 0x30); // PEB
                a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0x08)); // ImageBase
                a.Add(X64Size.Dword, X64Register.EAX, dataRva + imports[i].IatOffset);
                a.Pop(X64Register.ECX); // func VA
                a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EAX, 0), X64Register.ECX); // write slot

                a.Add(X64Size.Dword, X64Register.ESP, 4); // pop name VA

                if (last)
                {
                    a.Add(X64Size.Dword, X64Register.ESP, 4); // pop mask
                    a.Jmp(entryPointLabel);
                }
                else
                {
                    a.Jmp(setupLabels[i + 1]);
                }

                a.MarkLabel(nameNext);
                a.Add(X64Size.Dword, X64Register.ESP, 4); // pop name VA
                a.Add(X64Size.Dword, X64Register.EDX, 2);
                a.Add(X64Size.Dword, X64Register.ECX, 1);
                a.Cmp(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.EDI, 0x18)); // NumberOfNames
                a.Jcc(X64CondCode.Below, nameLoop);
            }

            a.Add(X64Size.Dword, X64Register.ESP, 4); // pop mask
            a.Jmp(entryPointLabel);

            a.MarkLabel(findNext);
            a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.ESI, 0));
            a.Jmp(findLoop);

            a.MarkLabel(fail);
            a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            a.Add(X64Size.Dword, X64Register.ESP, 4);
            a.Ret();
        }

        /// <summary>导出名 → ANSI 字模板（每字节 OR 0x20 折叠小写），覆盖完整名字长度；掩码屏蔽跨界读入的相邻名字字节。</summary>
        internal static List<(int Word, int Mask)> AnsiWords(string name)
        {
            var parts = new List<(int Word, int Mask)>();
            var count = (name.Length + 3) / 4;
            for (var k = 0; k < count; k++)
            {
                var word = 0;
                for (var j = 0; j < 4; j++)
                {
                    var i = k * 4 + j;
                    var c = i < name.Length ? (byte)((byte)name[i] | 0x20) : (byte)0;
                    word |= c << (j * 8);
                }

                var mask = k < count - 1 ? -1 : LengthMask(name.Length - k * 4);
                parts.Add((word, mask));
            }

            return parts;
        }

        private static int LengthMask(int bytes)
        {
            return bytes >= 4 ? -1 : (1 << bytes * 8) - 1;
        }
    }
}
