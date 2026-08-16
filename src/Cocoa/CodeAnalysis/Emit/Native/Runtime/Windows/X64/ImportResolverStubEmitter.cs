using System;
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

            // 按 DLL 分组：kernel32 组在前（RSI 保存其基址，LoadLibraryA 解析用），其余按首见顺序
            var groups = new List<List<PefileImport>>();
            var k32Group = new List<PefileImport>();
            var otherGroups = new List<List<PefileImport>>();
            var groupByDll = new Dictionary<string, List<PefileImport>>(StringComparer.OrdinalIgnoreCase);

            foreach (var import in imports)
            {
                if (string.Equals(import.DllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
                {
                    k32Group.Add(import);
                }
                else
                {
                    if (!groupByDll.TryGetValue(import.DllName, out var group))
                    {
                        group = new List<PefileImport>();
                        groupByDll.Add(import.DllName, group);
                        otherGroups.Add(group);
                    }

                    group.Add(import);
                }
            }

            groups.Add(k32Group);
            groups.AddRange(otherGroups);

            var reordered = new List<PefileImport>();
            foreach (var group in groups)
            {
                reordered.AddRange(group);
            }

            a.Mov(X64Size.Qword, X64Register.RSI, X64Register.RBX); // 保存 kernel32 基址（LoadLibraryA 解析用）

            var setupLabels = new int[reordered.Count];
            for (var i = 0; i < reordered.Count; i++)
            {
                setupLabels[i] = a.CreateLabel();
            }

            var groupSetupLabels = new int[groups.Count];
            for (var g = 1; g < groups.Count; g++)
            {
                groupSetupLabels[g] = a.CreateLabel();
            }

            var reorderedIndex = 0;
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];

                if (g > 0 && group.Count > 0)
                {
                    a.MarkLabel(groupSetupLabels[g]);

                    // 优先在已加载模块链表（InLoadOrderModuleList）中按 UTF-16 名找本组 DLL。
                    // 静态导入的 DLL（如 user32.dll）已在进程初始化时装入，直接复用其基址；
                    // 找不带的才回退到 LoadLibraryA，避免对已加载模块重复走加载器隔离重定向路径（RtlDosApplyFileIsolationRedirection）。
                    // R11 = 链表游标，RSI/RDI/R14/R12/R13/R15 保持 k32 扫描状态（LLA 回退路径需要）。
                    a.Mov(X64Register.R10, Mask);
                    a.MovGs(X64Register.RAX, 0x60); // PEB
                    a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RAX, 0x18)); // Ldr
                    a.Mov(X64Size.Qword, X64Register.R11, new X64MemoryOperand(X64Register.RAX, 0x10)); // Flink
                    a.Mov(X64Size.Qword, X64Register.R9, X64Register.R11); // 头节点（环绕判定界：扫回头部即未找到）

                    var moduleFindLoop = a.CreateLabel();
                    var moduleFindNext = a.CreateLabel();
                    var moduleExportSetup = a.CreateLabel();
                    var llaSetup = a.CreateLabel();

                    a.MarkLabel(moduleFindLoop);
                    a.Mov(X64Size.Qword, X64Register.RDX, new X64MemoryOperand(X64Register.R11, 0x60)); // BaseDllName.Buffer
                    a.Test(X64Size.Qword, X64Register.RDX, X64Register.RDX);
                    a.Jcc(X64CondCode.Equal, moduleFindNext);
                    a.Movzx(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.R11, 0x58)); // BaseDllName.Length
                    a.Cmp(X64Size.Dword, X64Register.RAX, group[0].DllName.Length * 2);
                    a.Jcc(X64CondCode.NotEqual, moduleFindNext);

                    var modParts = Utf16NameParts(group[0].DllName);
                    for (var k = 0; k < modParts.Count; k++)
                    {
                        var (word, mask) = modParts[k];
                        a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, k * 8));
                        a.Or(X64Size.Qword, X64Register.RAX, X64Register.R10);
                        if (mask != -1)
                        {
                            a.Mov(X64Register.R8, mask);
                            a.And(X64Size.Qword, X64Register.RAX, X64Register.R8);
                        }
                        a.Mov(X64Register.R8, word);
                        a.Cmp(X64Size.Qword, X64Register.RAX, X64Register.R8);
                        a.Jcc(X64CondCode.NotEqual, moduleFindNext);
                    }

                    a.Mov(X64Size.Qword, X64Register.RBX, new X64MemoryOperand(X64Register.R11, 0x30)); // 已加载模块基址
                    a.Mov(X64Register.R10, AnsiMask);
                    a.Jmp(moduleExportSetup);

                    a.MarkLabel(moduleFindNext);
                    a.Mov(X64Size.Qword, X64Register.R11, new X64MemoryOperand(X64Register.R11, 0)); // Flink
                    a.Cmp(X64Size.Qword, X64Register.R11, X64Register.R9);
                    a.Jcc(X64CondCode.Equal, llaSetup); // 环绕回头部 → 未在已加载模块中找到 → 回退 LoadLibraryA
                    a.Jmp(moduleFindLoop);

                    // 回退路径：从 kernel32 导出目录解析 LoadLibraryA（RSI = k32 基址，RDI/R14/R12 仍为 k32 状态）
                    var llaLoop = a.CreateLabel();
                    var llaNext = a.CreateLabel();

                    a.MarkLabel(llaSetup);
                    a.Mov(X64Register.R10, AnsiMask); // 模块链表扫描用过 UTF-16 mask，回退扫描需要 ANSI mask

                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x20));
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RSI);
                    a.Mov(X64Size.Qword, X64Register.R15, X64Register.RAX);
                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x24));
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RSI);
                    a.Mov(X64Size.Qword, X64Register.R13, X64Register.RAX);
                    a.Xor(X64Size.Dword, X64Register.R11, X64Register.R11);

                    a.MarkLabel(llaLoop);
                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.R15, 0)); // name RVA
                    a.Add(X64Size.Qword, X64Register.R15, 4);
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RSI); // name VA

                    var llaParts = AnsiWords("LoadLibraryA");
                    for (var k = 0; k < llaParts.Count; k++)
                    {
                        var (word, mask) = llaParts[k];
                        a.Mov(X64Size.Qword, X64Register.R9, new X64MemoryOperand(X64Register.RAX, k * 8));
                        a.Or(X64Size.Qword, X64Register.R9, X64Register.R10);
                        if (mask != -1)
                        {
                            a.Mov(X64Register.R8, mask);
                            a.And(X64Size.Qword, X64Register.R9, X64Register.R8);
                        }
                        a.Mov(X64Register.R8, word);
                        a.Cmp(X64Size.Qword, X64Register.R9, X64Register.R8);
                        a.Jcc(X64CondCode.NotEqual, llaNext);
                    }

                    a.Movzx(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.R13, 0)); // ordinal value
                    a.Shl(X64Size.Qword, X64Register.RAX, 2);
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.R12);
                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RAX, 0)); // func RVA
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RSI); // func VA
                    a.Mov(X64Size.Qword, X64Register.R9, X64Register.RAX); // LoadLibraryA

                    // 调用 LoadLibraryA(dllNameW)：dll 名紧随 IAT 槽之后（data 段）
                    a.MovGs(X64Register.R8, 0x60);
                    a.Mov(X64Size.Qword, X64Register.R8, new X64MemoryOperand(X64Register.R8, 0x10)); // ImageBase
                    a.Add(X64Size.Qword, X64Register.R8, dataRva + group[0].IatOffset + 8);
                    a.Sub(X64Size.Qword, X64Register.RSP, 0x28);
                    a.Mov(X64Size.Qword, X64Register.RCX, X64Register.R8);
                    a.Call(X64Register.R9);
                    a.Add(X64Size.Qword, X64Register.RSP, 0x28);
                    a.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
                    a.Jcc(X64CondCode.Equal, fail);
                    a.Mov(X64Size.Qword, X64Register.RBX, X64Register.RAX); // 新模块基址
                    a.Mov(X64Register.R10, AnsiMask); // LoadLibraryA 破坏 R10

                    a.Jmp(moduleExportSetup);

                    a.MarkLabel(llaNext);
                    a.Add(X64Size.Qword, X64Register.R13, 2);
                    a.Add(X64Size.Qword, X64Register.R11, 1);
                    a.Cmp(X64Size.Qword, X64Register.R11, X64Register.R14);
                    a.Jcc(X64CondCode.Below, llaLoop);

                    // 共享导出目录设置：RBX = 目标 DLL 基址（无论来自链表还是 LoadLibraryA）
                    a.MarkLabel(moduleExportSetup);
                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBX, 0x3C)); // e_lfanew
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RAX, 0x88)); // export dir RVA
                    a.Test(X64Size.Dword, X64Register.RAX, X64Register.RAX);
                    a.Jcc(X64CondCode.Equal, fail);
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX); // export dir VA
                    a.Mov(X64Size.Qword, X64Register.RDI, X64Register.RAX);
                    a.Mov(X64Size.Dword, X64Register.R14, new X64MemoryOperand(X64Register.RDI, 0x18)); // NumberOfNames
                    a.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RDI, 0x1C)); // AddressOfFunctions
                    a.Add(X64Size.Qword, X64Register.RAX, X64Register.RBX);
                    a.Mov(X64Size.Qword, X64Register.R12, X64Register.RAX);
                    a.Jmp(setupLabels[reorderedIndex]);
                }

                for (var j = 0; j < group.Count; j++)
                {
                    var i = reorderedIndex + j;
                    var import = group[j];
                    var last = i == reordered.Count - 1;
                    var nameParts = AnsiWords(import.Name);

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
                    a.Add(X64Size.Qword, X64Register.R8, dataRva + import.IatOffset);
                    a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.R8, 0), X64Register.RAX); // write slot

                    var nextTarget = -1;
                    if (last)
                    {
                        a.Add(X64Size.Qword, X64Register.RSP, 0x28);
                        a.Jmp(entryPointLabel);
                    }
                    else if (j < group.Count - 1)
                    {
                        nextTarget = setupLabels[i + 1];
                        a.Jmp(nextTarget);
                    }
                    else
                    {
                        nextTarget = groupSetupLabels[g + 1];
                        a.Jmp(nextTarget);
                    }

                    a.MarkLabel(nameNext);
                    a.Add(X64Size.Qword, X64Register.R13, 2);
                    a.Add(X64Size.Qword, X64Register.R11, 1);
                    a.Cmp(X64Size.Qword, X64Register.R11, X64Register.R14);
                    a.Jcc(X64CondCode.Below, nameLoop);
                }

                reorderedIndex += group.Count;
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

        /// <summary>
        /// UTF-16 module-name templates for the loaded-module-list scan: each char is 2 bytes (low byte OR 0x20, high 0).
        /// The UNICODE_STRING.Length pre-check pins the exact name length, so only the real chars are compared (the
        /// higher byte of each char is 0 and is included via a per-qword LengthMask so trailing qwords are exact).
        /// </summary>
        private static List<(long Word, long Mask)> Utf16NameParts(string name)
        {
            var parts = new List<(long Word, long Mask)>();
            var bytes = name.Length * 2;
            var count = (bytes + 7) / 8;
            for (var k = 0; k < count; k++)
            {
                long word = 0;
                for (var j = 0; j < 8; j++)
                {
                    var i = k * 8 + j;
                    var c = i % 2 == 1 || i / 2 >= name.Length ? 0L : (long)((byte)name[i / 2] | 0x20);
                    word |= c << (j * 8);
                }

                var mask = k < count - 1 ? -1L : LengthMask(bytes - k * 8);
                parts.Add((word, mask));
            }

            return parts;
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