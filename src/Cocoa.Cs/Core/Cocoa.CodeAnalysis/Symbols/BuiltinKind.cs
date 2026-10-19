namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 内置函数种类（功能层原语）。三后端（Evaluator/IL/native）按 <see cref="FunctionSymbol.BuiltinKind"/> 分发；
    /// 不依赖 `== BuiltinFunctions.X` 引用相等。
    /// </summary>
    public enum BuiltinKind
    {
        WriteLine,
        Write,
        ReadLine,
        ReadKey,
        Random,
        Sleep,
        TickCount,
        Exit,
        Sqrt,
        Beep,
        DoubleToString,
        StringFromChars,

        // ---- 加密（6e-G7 ⑤a）----
        Sha256Hash,

        // ---- 解密/编码（自举 IO）----
        StringFromBytes,
        StringToBytes,

        // ---- 文件 IO（6e-G7 ④）----
        FileReadAllText,
        FileWriteAllText,
        FileReadAllBytes,
        FileWriteAllBytes,
        FileOpen,
        FileSize,
        FileSeek,
        FileTell,
        FileRead,
        FileWrite,
        FileClose,
        FileExists,
        FileDelete,
        FileCopy,
        DirectoryExists,
        DeleteDirectory,
        GetEnvironmentVariable,
        GetCurrentDirectory,
        SetCurrentDirectory,
        GetExecutablePath,

        // ---- 进程（P1-9）----
        LaunchProcess,

        // 6e-M19 M2-c：System.Object 内建成员（实例虚四方法 + 静态二方法）。
        // 不进 _specs 表——由 SystemObjectMembers 自持 spec/单例，避免污染 GetByName 全局名表；
        // `.coa` 序列化经 GetByKindName → SystemObjectMembers.GetByKindName 解析。
        CreateDirectory,

        // 6e-M19 M2-c：System.Object 内建成员（实例虚四方法 + 静态二方法）。
        // 不进 _specs 表——由 SystemObjectMembers 自持 spec/单例，避免污染 GetByName 全局名表；
        // `.coa` 序列化经 GetByKindName → SystemObjectMembers.GetByKindName 解析。
        ObjectToString,
        ObjectGetHashCode,
        ObjectEquals,
        ObjectGetType,
        ObjectStaticEquals,
        ObjectReferenceEquals,
        TypeName,
        TypeFullName,

        // ---- 数组切片（P1-2 .. Range 运算符）----
        CopyRange,

        // ---- .cocoa 元数据存在性（M6 弱化，native-only）----
        // 运行期读 data 段回填的 .cocoa 元数据指针槽判非零（不再 GetModuleHandle/遍历自身镜像）。
        // 仅 native 产物内嵌 .cocoa；Evaluator/IL 无此载体 → 返回 false。
        SelfIntrospect,

        // ---- 反射（C-1）：System.Type 完整面子集 ----
        // 仅 Evaluator/IL 后端（跑在 CLR 上可直接反射宿主类型）；native 无宿主类型表故为覆盖缺口。
        TypeIsClass,
        TypeIsValueType,
        TypeGetMethods,
        TypeGetMethod,
        TypeHasMethod,
    }
}