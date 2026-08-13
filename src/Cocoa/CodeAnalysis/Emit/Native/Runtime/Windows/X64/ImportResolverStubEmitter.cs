using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X64
{
    /// <summary>
    /// Self-bootstrapping entry stub: PEB -> find kernel32 -> resolve export names -> fill IAT slots -> jump to user entry.
    /// Works around the loader never patching the IAT (slots stay 0 and crash on call).
    /// Mirrors PeExportTable.TryGetExportRva so the C# reference and the machine code stay in sync.
    /// </summary>
    internal static class ImportResolverStubEmitter
    {
        // Per-UTF16-low-byte OR 0x20 (fold to lowercase). Digits/symbols already have bit 0x20 set, so they are unaffected.
        // (AND 0xDF would corrupt digits: 0x33 & 0xDF == 0x13.)
        private const long Mask = 0x0020002000200020; // UTF-16 module names: OR 0x20 into each low byte
        private const long AnsiMask = 0x2020202020202020; // ANSI export names: OR 0x20 into every byte

        public static void Emit(IAssembler a, int entryPointLabel, IReadOnlyList<PefileImport> imports, int dataRva)
        {
            a.Sub(X64Size.Qword, X64Register.RSP, 0x28);

            a.MovGs(X64Register.RAX, 0x60); // PEB
            a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RAX, 0x18)); // Ldr
            a.Mov(X64Size.Qword, X64Register.RSI, new X64MemoryOperand(X64Register.RAX, 0x10)); // InLoadOrderModuleList.Flink

            var (k32w0, k32w1) = Utf16Words("KERNEL32");
            a.Mov(X64Register.RCX, k32w0);
            a.Mov(X64Register.R11, k32w1);
            a.Mov(X64Register.R10, Mask);

            var findLoop = a.CreateLabel();
            var findNext = a.CreateLabel();
            var k32Found = a.CreateLabel();
            var fail = a.CreateLabel();

            a.MarkLabel(findLoop);
            a.Mov(X64Size.Qword, X64Register.RBX, new X64MemoryOperand(X64Register.RSI, 0x30)); // DllBase
            a.Mov(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.RSI, 0x60)); // BaseDllName.Buffer
            a.Test(X64Size.Qword, X64Register.RDX, X64Register.RDX);
            a.Jcc(X64CondCode.Equal, findNext);
            a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, 0));
            a.Or(X64Size.Qword, X64Register.RAX, X64Register.R10);
            a.Cmp(X64Size.Qword, X64Register.RAX, X64Register.RCX);
            a.Jcc(X64CondCode.NotEqual, findNext);
            a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, 8));
            a.Or(X64Size.Qword, X64Register.RAX, X64Register.R10);
            a.Cmp(X64Size.Qword, X64Register.RAX, X64Register.R11);
            a.Jcc(X64CondCode.NotEqual, findNext);

            // "KERNEL32.DLL" in UTF-16: 'D' at byte 18, 'L' at byte 20
            a.Movzx(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, 18));
            a.Or(X64Size.Dword, X64Register.RAX, 0x20);
            a.Cmp(X64Size.Dword, X64Register.RAX, 'd');
            a.Jcc(X64CondCode.NotEqual, findNext);
            a.Movzx(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, 20));
            a.Or(X64Size.Dword, X64Register.RAX, 0x20);
            a.Cmp(X64Size.Dword, X64Register.RAX, 'l');
            a.Jcc(X64CondCode.NotEqual, findNext);

            a.MarkLabel(k32Found);
            a.Mov(X64Register.R10, AnsiMask);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0x3C)); // e_lfanew
            a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RAX, 0x88)); // export dir RVA (PE hdr + 0x18 optional hdr + 0x70 DataDirectory[0])
            a.Test(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            a.Jcc(X64CondCode.Equal, fail);
            a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX); // export dir VA
            a.Mov(X64Size.Qword, X64Register.RDI, X64Register.RAX);

            a.Mov(X64Size.Dword, X64Register.R14, new X64MemoryOperand(X64Register.RDI, 0x18)); // NumberOfNames
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x20)); // AddressOfNames
            a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
            a.Mov(X64Size.Qword, X64Register.R15, X64Register.RAX);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x24)); // AddressOfNameOrdinals
            a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
            a.Mov(X64Size.Qword, X64Register.R13, X64Register.RAX);
            a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x1C)); // AddressOfFunctions
            a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
            a.Mov(X64Size.Qword, X64Register.R12, X64Register.RAX);

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
                a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x20));
                a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
                a.Mov(X64Size.Qword, X64Register.R15, X64Register.RAX);
                a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x24));
                a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
                a.Mov(X64Size.Qword, X64Register.R13, X64Register.RAX);
                a.Xor(X64Size.Dword, X64Register.R11, X64Register.R11);

                a.MarkLabel(nameLoop);
                a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.R15, 0)); // name RVA
                a.Add(X64Size.Qword, X64Register.R15, 4);
                a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX); // name VA

                for (var k = 0; k < nameParts.Count; k++)
                {
                    var (word, mask) = nameParts[k];
                    a.Mov(X64Size.Qword, X64Register.R9, new X64MemoryOperand(X64Register.RAX, k * 8));
                    a.Or(X64Size.Qword, X64Register.R9, X64Register.R10);
                    if (mask != -1)
                    {
                        a.Mov(X64Register.R8, mask);
                        a.And(X64Size.Qword, X64Register.R9, X64Register.R8);
                    }
                    a.Mov(X64Register.R8, word);
                    a.Cmp(X64Size.Qword, X64Register.R9, X64Register.R8);
                    a.Jcc(X64CondCode.NotEqual, nameNext);
                }
                a.Movzx(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.R13, 0)); // ordinal value
                a.Shl(X64Size.Qword, X64Register.RAX, 2);
                a.Add(X64Size.Qword, X64Register.RAX, X64Register.R12);
                a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RAX, 0)); // func RVA
                a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX); // func VA

                a.MovGs(X64Register.R8, 0x60);
                a.Mov(X64Size.Qword, X64Register.R8, new X64MemoryOperand(X64Register.R8, 0x10)); // ImageBase
                a.Add(X64Size.Qword, X64Register.R8, dataRva + imports[i].IatOffset);
                a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.R8, 0), X64Register.RAX); // write slot

                if (last)
                {
                    a.Add(X64Size.Qword, X64Register.RSP, 0x28);
                    a.Jmp(entryPointLabel);
                }
                else
                {
                    a.Jmp(setupLabels[i + 1]);
                }

                a.MarkLabel(nameNext);
                a.Add(X64Size.Qword, X64Register.R13, 2);
                a.Add(X64Size.Qword, X64Register.R11, 1);
                a.Cmp(X64Size.Qword, X64Register.R11, X64Register.R14);
                a.Jcc(X64CondCode.Below, nameLoop);
            }

            a.Add(X64Size.Qword, X64Register.RSP, 0x28);
            a.Jmp(entryPointLabel);

            a.MarkLabel(findNext);
            a.Mov(X64Size.Qword, X64Register.RSI, new X64MemoryOperand(X64Register.RSI, 0));
            a.Jmp(findLoop);

            a.MarkLabel(fail);
            a.Xor(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            a.Add(X64Size.Qword, X64Register.RSP, 0x28);
            a.Ret();
        }

        /// <summary>Module name to a 16-byte UTF-16 template: two bytes per char (low byte OR 0x20, folded to lowercase). Matches the runtime OR-0x20 name byte-for-byte; zero-padded when short.</summary>
        private static (long Word0, long Word1) Utf16Words(string name)
        {
            long word0 = 0;
            long word1 = 0;
            for (var i = 0; i < 8 && i < name.Length; i++)
            {
                var c = (long)((byte)name[i] | 0x20);
                if (i < 4)
                {
                    word0 |= c << (i * 16);
                }
                else
                {
                    word1 |= c << ((i - 4) * 16);
                }
            }

            return (word0, word1);
        }

        /// <summary>
        /// Export name to 64-bit ANSI templates: 8 chars per qword (each byte OR 0x20), covering the full name;
        /// the export-name string pool is NUL-terminated with no inter-name padding, so a qword read straddling
        /// the terminator picks up the start of the next name; each qword is therefore compared masked down to
        /// the name's real length via LengthMask (-1 when no masking is needed). Padding bytes are 0 (not 0x20)
        /// so that the masked compare `(candidate & mask) == template` holds; the runtime OR-0x20 fold only
        /// applies to the real name bytes.
        /// </summary>
        internal static List<(long Word, long Mask)> AnsiWords(string name)
        {
            var parts = new List<(long Word, long Mask)>();
            var count = (name.Length + 7) / 8;
            for (var k = 0; k < count; k++)
            {
                long word = 0;
                for (var j = 0; j < 8; j++)
                {
                    var i = k * 8 + j;
                    var c = i < name.Length ? (long)((byte)name[i] | 0x20) : 0;
                    word |= c << (j * 8);
                }

                var mask = k < count - 1 ? -1L : LengthMask(name.Length - k * 8);
                parts.Add((word, mask));
            }

            return parts;
        }

        /// <summary>Mask with the low <paramref name="bytes"/> bits set (all bits when 8+).</summary>
        private static long LengthMask(int bytes)
        {
            return bytes >= 8 ? -1L : (1L << bytes * 8) - 1;
        }
    }
}