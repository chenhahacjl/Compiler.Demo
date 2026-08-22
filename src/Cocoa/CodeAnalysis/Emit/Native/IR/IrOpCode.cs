namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>
    /// IR 指令操作码（三地址码）。覆盖现有 NativeCodeEmitter 的全部发射语义，
    /// 以虚拟寄存器 + 栈式 ABI 表达；平台差异收敛到 IR → IAssembler 映射。
    /// </summary>
    internal enum IrOpCode
    {
        // 数据移动
        Const,       // Const <dst> <imm>            — 立即数加载
        Mov,         // Mov <dst> <src>              — 虚拟寄存器搬运
        Load,        // Load <dst> [<base> + off]    — 内存 → 寄存器
        LoadSlotField, // LoadSlotField <dst> <base> <off> <size> — 槽内存直接读取（不解引用 base，如 double 高 dword）
        StoreSlotField, // StoreSlotField <base> <off> <src> <size> — 槽内存直接写入（不解引用 base，如拼装 double）
        Store,       // Store [<base> + off] <src>   — 寄存器 → 内存
        LeaData,     // LeaData <dst> <data-symbol>  — 数据段符号地址（字符串字面量）
        Lea,         // Lea <dst> <base> <off>       — 指针算术：dst = base + off
        LeaSlot,     // LeaSlot <dst> <src>          — dst = 地址 of <src> 槽（&局部变量）
        InitParam,   // InitParam <dst> <byteOffset>  — 函数入口把参数区（[rbp+paramOffset+offset]）拷入虚拟寄存器
        InitRegArg,  // InitRegArg <dst> <ordinal>   — 运行时函数入口从调用约定寄存器 (0→ecx,1→edx) 取参

        // 算术
        Add,         // Add <dst> <srcA> <srcB>
        Sub,         // Sub <dst> <srcA> <srcB>
        Imul,        // Imul <dst> <srcA> <srcB>
        Idiv,        // Idiv <dst> <src>             — 有符号除法：dst / src → dst
        Irem,        // Irem <dst> <src>             — 有符号取余：dst % src → dst
        Udiv,        // Udiv <dst> <src>             — 无符号除法：dst / src → dst
        Urem,        // Urem <dst> <src>             — 无符号取余：dst % src → dst
        Neg,         // Neg <dst>
        Not,         // Not <dst>

        // 逻辑/位
        And,         // And <dst> <srcA> <srcB>
        Or,          // Or <dst> <srcA> <srcB>
        Xor,         // Xor <dst> <srcA> <srcB>
        Shl,         // Shl <dst> <src> <count>
        Shr,         // Shr <dst> <src> <count>
        Sar,         // Sar <dst> <src> <count>

        // 比较（Cmp 设标志，Setcc 紧随使用；Jcc 配最近一次 Cmp 的标志）
        Cmp,         // Cmp <srcA> <srcB>
        Setcc,       // Setcc <dst> <cond>

        // 分支
        Label,       // Label <id>
        Jmp,         // Jmp <label>
        Jcc,         // Jcc <cond> <label>

        // 调用/返回
        Call,        // Call <dst> <fn> <argCount>   — 相对调用（用户函数/运行时函数）
        CallReg,     // CallReg <dst> <reg>
        Ret,         // Ret <end-label>              — 函数收尾（返回值装载 + epilog）

        // 调用序列（x64 需调用者负责 rsp 16 字节对齐，深度由后端静态跟踪）
        ReserveArgs, // ReserveArgs <totalBytes>      — sub rsp, totalBytes（x64 每参 8B；x86 按类型累计 4/8B）
        StoreArg,    // StoreArg <byteOffset> <src>   — store [rsp + byteOffset]
        FreeArgs,    // FreeArgs <totalBytes>         — add rsp, totalBytes
        SetArg,      // SetArg <ordinal> <src>       — 运行时调用参数寄存器（0→ecx, 1→edx, 2→r8d, 3→r9d）
        StoreRet,    // StoreRet <src>               — store [rbp - slot]
        StackCheck,  // StackCheck                   — TEB 栈限检查（prolog）
        SysCall,     // SysCall <dst?> <import> <argCount> — 平台化系统调用（x64 fastcall+shadow / x86 stdcall；第 5 参恒为 0）

        // 栈操作（ABI 层：临时保存/恢复）
        Push,        // Push <src>
        Pop,         // Pop <dst>

        // 浮点（double，IEEE-754 binary64）。值在槽中以 64 位位模式存放；
        // x64 槽 8 字节，x86 槽 4 字节×2（低地址=低 32 位）。
        FConst,      // FConst <dst> <data-key>       — 数据段 8 字节 double 位模式载入
        FMov,        // FMov <dst> <src>              — double 槽间搬运
        FAdd,        // FAdd <dst> <srcA> <srcB>
        FSub,        // FSub <dst> <srcA> <srcB>
        FMul,        // FMul <dst> <srcA> <srcB>
        FDiv,        // FDiv <dst> <srcA> <srcB>
        FNeg,        // FNeg <dst> <src>              — 符号位翻转
        FCmp,        // FCmp <srcA> <srcB>            — ucomisd（NaN 由 Setcc(Parity/NoParity) 修正）
        FCvtSI,      // FCvtSI <dst> <src>            — int → double
        FCvtSD,      // FCvtSD <dst> <src>            — double → int（截断）
        SetArg64,    // SetArg64 <ordinal> <src>      — double 参数（x86 拆 low/high 两寄存器）

        // 调试/信息
        Nop,         // Nop
        SeqPoint,    // SeqPoint <file> <line>
    }
}