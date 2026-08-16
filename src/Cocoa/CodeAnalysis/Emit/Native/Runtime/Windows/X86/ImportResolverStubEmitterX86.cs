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

            // 按 DLL 分组：kernel32 组在前（EBX 保存其基址，LoadLibraryA 解析用），其余按首见顺序
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
                    // 找不到才回退到 LoadLibraryA，避免对已加载模块重复走加载器隔离重定向路径（RtlDosApplyFileIsolationRedirection）。
                    // [esp]=mask 之上压入 k32 的 EBP/EDI/EBX（回退路径需要还原）。
                    a.Push(X64Register.EBP);
                    a.Push(X64Register.EDI);
                    a.Push(X64Register.EBX);

                    var moduleFindLoop = a.CreateLabel();
                    var moduleFindNext = a.CreateLabel();
                    var llaSetup = a.CreateLabel();

                    a.MovGs(X64Register.ESI, 0x30); // PEB
                    a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.ESI, 0x0C)); // Ldr
                    a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.ESI, 0x0C)); // InLoadOrderModuleList.Flink
                    a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ESI); // 头节点（环绕判定界：扫回头部即未找到）

                    a.MarkLabel(moduleFindLoop);
                    a.Mov(X64Size.Dword, X64Register.EBX, new X64MemoryOperand(X64Register.ESI, 0x18)); // DllBase
                    a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.ESI, 0x30)); // BaseDllName.Buffer
                    a.Test(X64Size.Dword, X64Register.EDX, X64Register.EDX);
                    a.Jcc(X64CondCode.Equal, moduleFindNext);
                    a.Movzx(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESI, 0x2C)); // BaseDllName.Length（16 位）
                    a.Cmp(X64Size.Dword, X64Register.EAX, group[0].DllName.Length * 2);
                    a.Jcc(X64CondCode.NotEqual, moduleFindNext);

                    // "USER32.DLL" UTF-16LE，按 4 字节字折叠小写（OR 0x00200020）
                    var x86Parts = Utf16Dwords(group[0].DllName);
                    for (var k = 0; k < x86Parts.Count; k++)
                    {
                        a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, k * 4));
                        a.Or(X64Size.Dword, X64Register.EAX, 0x00200020);
                        a.Cmp(X64Size.Dword, X64Register.EAX, x86Parts[k]);
                        a.Jcc(X64CondCode.NotEqual, moduleFindNext);
                    }

                    // 命中：直接在模块导出目录上解析符号（EBX = 目标 DLL 基址）
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0x3C)); // e_lfanew
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX);
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0x78)); // export dir RVA
                    a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
                    a.Jcc(X64CondCode.Equal, moduleFindNext); // 无导出目录视为未命中，继续扫描
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // export dir VA
                    a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX);
                    a.Mov(X64Size.Dword, X64Register.EBP, new X64MemoryOperand(X64Register.EDI, 0x1C)); // AddressOfFunctions
                    a.Add(X64Size.Dword, X64Register.EBP, X64Register.EBX);
                    a.Add(X64Size.Dword, X64Register.ESP, 12); // 丢弃保存的 k32 状态（[esp] = mask）
                    a.Jmp(setupLabels[reorderedIndex]);

                    a.MarkLabel(moduleFindNext);
                    a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.ESI, 0)); // Flink
                    a.Cmp(X64Size.Dword, X64Register.ESI, X64Register.ECX);
                    a.Jcc(X64CondCode.Equal, llaSetup); // 环绕回头部 → 未在已加载模块中找到 → 回退 LoadLibraryA
                    a.Jmp(moduleFindLoop);

                    // 回退路径：从 kernel32 导出目录解析 LoadLibraryA（还原 k32 的 EBX/EDI/EBP 后扫描）
                    var llaLoop = a.CreateLabel();
                    var llaNext = a.CreateLabel();

                    a.MarkLabel(llaSetup);
                    a.Pop(X64Register.EBX); // k32 基址
                    a.Pop(X64Register.EDI); // k32 导出目录
                    a.Pop(X64Register.EBP); // k32 AddressOfFunctions
                    a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.EDI, 0x20)); // AddressOfNames
                    a.Add(X64Size.Dword, X64Register.ESI, X64Register.EBX);
                    a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.EDI, 0x24)); // AddressOfNameOrdinals
                    a.Add(X64Size.Dword, X64Register.EDX, X64Register.EBX);
                    a.Xor(X64Size.Dword, X64Register.ECX, X64Register.ECX);

                    a.MarkLabel(llaLoop);
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESI, 0)); // name RVA
                    a.Add(X64Size.Dword, X64Register.ESI, 4);
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // name VA
                    a.Push(X64Register.EAX); // [esp] = name VA, [esp+4] = mask

                    var llaParts = AnsiWords("LoadLibraryA");
                    for (var k = 0; k < llaParts.Count; k++)
                    {
                        a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESP, 0)); // name VA
                        a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, k * 4));
                        a.Or(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESP, 4));
                        a.And(X64Size.Dword, X64Register.EAX, llaParts[k].Mask);
                        a.Cmp(X64Size.Dword, X64Register.EAX, llaParts[k].Word);
                        a.Jcc(X64CondCode.NotEqual, llaNext);
                    }

                    a.Movzx(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EDX, 0)); // ordinal value
                    a.Shl(X64Size.Dword, X64Register.EAX, 2);
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBP);
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0)); // func RVA
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // func VA (LoadLibraryA)
                    a.Add(X64Size.Dword, X64Register.ESP, 4); // pop name VA ([esp] = mask)

                    // 调用 LoadLibraryA(dllNameW)：dll 名紧随 IAT 槽之后（data 段）
                    a.Push(X64Register.EAX); // [esp] = LLA, [esp+4] = mask
                    a.MovGs(X64Register.EAX, 0x30); // PEB
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0x08)); // ImageBase
                    a.Add(X64Size.Dword, X64Register.EAX, dataRva + group[0].IatOffset + 4);
                    a.Push(X64Register.EAX); // [esp] = dllName, [esp+4] = LLA, [esp+8] = mask
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.ESP, 4));
                    a.Call(X64Register.EAX); // stdcall：被调方清 dllName
                    a.Add(X64Size.Dword, X64Register.ESP, 4); // pop LLA ([esp] = mask)

                    a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
                    a.Jcc(X64CondCode.Equal, fail);
                    a.Mov(X64Size.Dword, X64Register.EBX, X64Register.EAX); // 新模块基址

                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EBX, 0x3C)); // e_lfanew
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX);
                    a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.EAX, 0x78)); // export dir RVA
                    a.Test(X64Size.Dword, X64Register.EAX, X64Register.EAX);
                    a.Jcc(X64CondCode.Equal, fail);
                    a.Add(X64Size.Dword, X64Register.EAX, X64Register.EBX); // export dir VA
                    a.Mov(X64Size.Dword, X64Register.EDI, X64Register.EAX);
                    a.Mov(X64Size.Dword, X64Register.EBP, new X64MemoryOperand(X64Register.EDI, 0x1C)); // AddressOfFunctions
                    a.Add(X64Size.Dword, X64Register.EBP, X64Register.EBX);
                    a.Jmp(setupLabels[reorderedIndex]);

                    a.MarkLabel(llaNext);
                    a.Add(X64Size.Dword, X64Register.ESP, 4); // pop name VA
                    a.Add(X64Size.Dword, X64Register.EDX, 2);
                    a.Add(X64Size.Dword, X64Register.ECX, 1);
                    a.Cmp(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.EDI, 0x18)); // NumberOfNames
                    a.Jcc(X64CondCode.Below, llaLoop);
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
                    a.Add(X64Size.Dword, X64Register.EAX, dataRva + import.IatOffset);
                    a.Pop(X64Register.ECX); // func VA
                    a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.EAX, 0), X64Register.ECX); // write slot

                    a.Add(X64Size.Dword, X64Register.ESP, 4); // pop name VA

                    if (last)
                    {
                        a.Add(X64Size.Dword, X64Register.ESP, 4); // pop mask
                        a.Jmp(entryPointLabel);
                    }
                    else if (j < group.Count - 1)
                    {
                        a.Jmp(setupLabels[i + 1]);
                    }
                    else
                    {
                        a.Jmp(groupSetupLabels[g + 1]);
                    }

                    a.MarkLabel(nameNext);
                    a.Add(X64Size.Dword, X64Register.ESP, 4); // pop name VA
                    a.Add(X64Size.Dword, X64Register.EDX, 2);
                    a.Add(X64Size.Dword, X64Register.ECX, 1);
                    a.Cmp(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.EDI, 0x18)); // NumberOfNames
                    a.Jcc(X64CondCode.Below, nameLoop);
                }

                reorderedIndex += group.Count;
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

        /// <summary>已加载模块 UTF-16 名 → 4 字节字模板（每个 UTF-16 字符的低字节 OR 0x20 折叠小写），零填充补齐。</summary>
        internal static List<int> Utf16Dwords(string name)
        {
            var parts = new List<int>();
            var count = (name.Length + 1) / 2;
            for (var k = 0; k < count; k++)
            {
                var word = 0;
                for (var j = 0; j < 2; j++)
                {
                    var i = k * 2 + j;
                    var c = i < name.Length ? ((byte)name[i] | 0x20) : 0;
                    word |= c << (j * 16);
                }

                parts.Add(word);
            }

            return parts;
        }

        private static int LengthMask(int bytes)
        {
            return bytes >= 4 ? -1 : (1 << bytes * 8) - 1;
        }
    }
}
