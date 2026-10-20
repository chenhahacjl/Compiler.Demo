# Changelog

> 状态：✅ 生效（2026-09-06）
> 定位：面向用户的近期变更流水（里程碑粒度）；完整提交历史见 git。
> 相关：[docs/README.md](docs/README.md)、[docs-dev/开发计划.md](docs-dev/开发计划.md)（阶段路线图总计）

---

---

## 未发布（2026-10-20）

### native-s1 加固：陷阱错误径 ABI 对齐 + x86 拆分 .reloc 修复 ✅
- **陷阱路径行为等价门禁**：新增 `TrapPathSplitTests`（10 例）——除零（i32/i64/u64）、取余、栈溢出在「内联 vs 全拆分（System.Core.Native.dll）」双形态 × x64/x86 下退出码与输出全等（此前 `NativeSplitDifferentialTests` 源码未真实触发陷阱点）。
- **发现并修复 x86 拆分真实缺陷**（拆分工作此前仅 x64 验证）：x86 共享库 `System.Core.Native.dll` 首选基址 0x400000 与 exe 冲突 → 加载器异地加载，而 `.reloc` 仅含 `.data` 指针槽 → 代码内绝对引用（`lea`/`call*`/`jmp*` disp32）跳旧 base → 任何 x86 split 产物首调即 0xC0000005。
- **修复**：x86 汇编器把 `.text` 内绝对引用登记为 `CodeAbsoluteFixups`（MovRip/LeaRip/CallRip/JmpRip/MovssRip/MovsdRip 6 站点）；`PeFileWriter` 对 pe32 产物补发代码侧 HIGHLOW reloc（`BuildRelocBlob` 改收绝对 RVA、按 4K 页跨节分组）；exe 路径不启用（非 ASLR 固定基址，黄金字节不变）、x64 门控忽略（RIP 相对无重定位需求）。
- **回归门禁扩展**：TrapPathSplitTests 新增真实 System.Core 全拆分（linkCoaDynamically+splitRuntime）x86 除零门禁（真实库 `Cocoa_Rt_div_by_zero` 落入）；`CocoaBuildCliTests` 新增 `Build_Project_X86_Native_SharedSystemLibrary_Runs`（SharedSystemLibrary=true + `--platform x86` CLI e2e：exe 变小、`System.Core.Native.dll` 部署、运行 5.5/ab 输出）——x86 拆分重定位修复的端到端兑现。
- 回归：CodeGen 1086 / Integration 287 / CodeAnalysis 2007 / Engine 40 全绿；全解决方案构建 0 警告 0 错误；`WindowsBytesInvariant` x64 exe golden 不动。

### CocoaEngine：`Call` 支持类静态方法 ✅
- **`engine.Call("ClassName.method", args)` / `"Namespace.Class.method"`**——查找面从顶层函数扩至类静态方法：Interpreter 走 `Compilation.EvaluateFunction` 链上按类型 `FullName`/裸 `Name` 匹配（`IsStatic` 且非构造器）；IlEmit 走 `IlEmitSession.Call` 反射（顶层 `<CocoaTopLevel>` 优先 → 程序集用户类型 `SafeAssemblyTypes` 兜反射基类加载失败）。
- **重载消解**：类静态方法多重重载按**实参元数过滤 + 精确 CLR 类型匹配打分**（对齐解释器 `CoerceArgument`「优先精确」语义；IlEmit 同规则经 `ResolveStaticMethod`，顺带消除 `Type.GetMethod(name)` 对重载的 `AmbiguousMatchException`）。
- 实例方法（非 static）暂不支持：C# 侧按可读提示失败，脚本内经 `new` 驱动（后续增量）。
- 测试：Engine 新增 11 例（Interpreter 7：限定名/命名空间/裸类名/未知方法/实例方法负例/按类型重载/按元数重载；IlEmit 4：限定名/命名空间/未知方法/按类型重载）。文档 `docs/嵌入式引擎API.md` §6/§12 同步。

---

## 未发布（2026-10-19）

### AV/分析器白面：native 产物更像标准编译器输出 + COFF 调试符号 ✅
- **Rich 头**：DOS stub 后嵌入标准 Rich 头（DanS + checksum + XOR 条目 + Rich + key，自定义产品/构建 ID），`e_lfanew=0x100`——DIE 识别为 "Linker: Microsoft Linker"；仅 PeObjectWriter（native），不碰 PeImageBuilder（自举差分不动）。
- **导入表自包含 `.idata`**：descriptors + INT + hint/name + 内嵌 IAT 同处 `.idata`；`dir1`/`dir12`/`FirstThunk` 全部落 `.idata`（此前 fake-IAT 槽散布 `.data`，IDA 报 "imports destroyed"）。IAT 保留镜像 INT 初值——实测 Windows 加载器对 0 槽按 bound-import 跳过回填（`.data`/`.idata` 均 26/26 崩 0xC0000005），此为加载器硬性要求。
- **节序**：`.cocoa` 置于末位（.text/.data/.idata/.reloc/.rsrc/.cocoa）。
- **SelfIntrospect 弱化**：读 data 段回填的 `.cocoa` 元数据指针判非零，不再 GetModuleHandleW/遍历自身镜像（删 `GetModuleHandleW` 导入 + Linux `/proc/self/exe` 自省）。
- **默认图标内嵌**：`resources/EXE.ico` 作为 EmbeddedResource 编进 Cocoa.Build.dll——未配置任何图标文件的可执行产物自动内嵌默认图标，无需 `EXE.ico` 在盘。优先级不变：`<ApplicationIcon>` → 工程目录 `EXE.ico` → 仓库 `resources/EXE.ico` → 内嵌默认。
- **版本元数据缺省值**：未配 `Description` 时 `FileDescription` 回落 `AssemblyName`（右键属性「详细信息」页不再只有版本号）。
- **共享运行库部署修复**：`-o` 指定输出路径时 `System.Core.Native.dll`/CopyLocal/Content 一律部署到实际产物目录——修复共享形态 exe 与依赖分家导致的 0xC0000135（此前表现为"main 返回值退出码不对"）。
- **移除 Debug/RSDS**：不再广告不存在的 `<模块>.pdb`（消除工具 "failed to load pdb info" 告警）。
- **COFF 调试符号表**：native 产物追加 COFF 符号表（函数符号名 → RVA + 源文件/行号，`PointerToSymbolTable`/`NumberOfSymbols` 回填）——IDA/dumpbin/WinDbg 直接读函数符号与行号，**无 PDB 文件依赖**。新增 `CoffSymbolTableBuilder`；COFF 行号为函数入口粒度。
- **原生 PDB 7.0（native 产物）**：新增 `PdbWriter`——自写 MSF 容器 + DS/TPI/DBI/IPI + CodeView 符号流（`S_OBJNAME`/`S_GPROC32`/`S_END` + **`S_LINES` 行号表**：段内偏移 + 语句行，file id=0 单文件），`<模块>.pdb` 旁置 + exe 重加 Debug/RSDS 指向真实 .pdb（GUID 与 PDB 的 DS 流一致）。IDA/WinDbg 可加载函数符号与行号。
- **Portable PDB（IL 产物）**：`Compilation.Emit` 经 `System.Reflection.Metadata.PortablePdbBuilder` 旁置便携 `.pdb`（Module MVID 取产物、Document + MethodDebugInformation 表）——dotnet/VS 调试器可加载。**语句级序列点**：发射期在 `EmitStatement` 捕获（语句首个指令下标 + 源文件/行），`Assemble()` 后换算 IL 字节偏移；序列点 blob 手编（net9.0 SRM 无 `SequencePointEncoder`，格式对齐 `SequencePointCollection` 解码器，SRM 读取器回读验证）。
- 测试：新增 Rich 头反解、`.idata` 自包含结构、内嵌图标（RT_GROUP_ICON）、COFF 符号表回读/产物断言、原生 PDB MSF/符号流回读、Portable PDB 读取器回读、版本元数据缺省值测试；`WindowsBytesInvariant` 黄金值重采集。全量 CodeGen 1076 / Integration 287 / Engine 32 绿。

---

## 未发布（2026-10-16）

### 跨平台：import 按目标 OS 区分（对称门禁 + 条件选源）✅
- **对称门禁**：windows 目标编译期拒绝 Linux 动态库（`.so`/libc/libm）extern——`import libc` 在 Windows 立即报「windows 目标不支持 extern .so 库」，杜绝运行期 `LoadLibrary` 坏 exe（镜像 linux 目标对非 shim Windows DLL 的既有拒绝；`NativeBackend.TryValidate`）。
- **`$(TargetOS)` 条件变量修通**：① `--os` 覆盖路径注入改为小写（此前 PascalCase "Linux" 致 `'$(TargetOS)'=='linux'` 失配）；② `ConditionEvaluator` 支持带引号属性引用 `'$(X)'`（此前只当字面量，文档写法恒假）；③ 未声明时缺省注入 `$(TargetOS)=windows` 供条件引用；④ CLI/`.user` 覆盖在无条件属性组后重放（修复「项目无条件 `<TargetOS>` 压掉 `--os`」的优先级缺陷）。
- **跨平台选源模式**：同一工程按 `<ItemGroup Condition="$(TargetOS)=='linux'">` 引入各平台 `import` 声明——`partial class` 门面（`Platform.Pid()`）+ `extern(entry=…)` 别名绑定平台真实导出（`GetCurrentProcessId`/`getpid`）；`--os` 驱动 `ProjectBuilder` 重载重求值条件源；互斥导入不同批进绑定、不触发对称门禁。
- **样例**：`samples/Samples/Linux/CrossPlatform`（同一 `main.co` 双端构建：linux→ELF+WSL `Pid()>0` / windows→PE+本机 `Pid()>0`）。
- 测试：`WindowsTarget_SoImport_IsRejected` + `Condition_TargetOs_SelectsPlatformSource_BothDrivingPaths`（两驱动路径×两种属性写法）+ `ConditionalPlatformSourceTests`（双端构建 ELF/PE + 对称门禁负例）。

---

## 未发布（2026-10-16）

### 跨平台：Linux native 后端（T0-T3 + N3/N4 动态链接与互操作面）✅
- **目标模型（T0）**：`TargetTriple` 单一事实源（删双 OS 枚举）+ `.coproj` 校验矩阵（`Backend` 三分 native/managed-fx/managed-net、四维 `IlTarget`、`TargetOS`/`Arch` 组内校验），设计定稿见 [`coproj目标属性设计.md`](docs-dev/plan/coproj目标属性设计.md)。
- **ABI 去平台化（T1/T2）**：`IAbiDescriptor`（WinX64/WinX86/SysVAmd64）+ `PlatformDllResolver`，kernel32 内联收口；`IObjectWriter` 接口 + `PeObjectWriter`（CheckSum 真算 + TimeDateStamp）。
- **最小可执行 ELF（T3.3 ~ T3/N2）**：`ElfObjectWriter`（ET_EXEC 非 PIE）→ SysV 运行时薄层（裸 syscall：控制台 UTF-8 链/堆 mmap/input/ReadKey raw-mode/文件/env/argv/进程/clock）→ `cocoa build --os linux` → `cocoa inspect` ELF 内嵌 `.cocoa` 元数据节（N5）+ Linux SHA-256 纯 LIR + SelfIntrospect（N5b）→ GuessNumber 跨平台（kernel32 extern shim 四符号映射）。
- **glibc 动态链接（N3）**：`.so` 导入打通——`DT_NEEDED` + `R_X86_64_GLOB_DAT` 数据段 IAT 槽（等价 Windows IAT）；`import libc { ... }` 可调真实 C 函数；`ldd` 解析 libc.so.6；验证器 `tools/verify-linux-wsl.ps1`（免 VM）。
- **extern 互操作面（N4，本批）**：① extern **string 实参**在 Linux 编组为 NUL 结尾 UTF-8 `char*`（`ExternUtf8Ptr0-3`，Windows 侧 `ExternWidePtr0-3` LPCWSTR 保持）；② extern **f64/f32 参数与返回**经 xmm 寄存器（SysV xmm0-7 / Win64 xmm0-3 同步修复，`IAbiDescriptor.FloatArgRegisterCount` + `EmitSysCall` 类型独立寄存器计数 + XMM0 返回）；③ 签名自检 x64 放行浮点（x86 仍拒）。
- **样本/门禁**：`samples/Samples/Linux/{LibcInterop,LibmMath}`（WSL 实机 `puts`/`strlen`/`getpid`/`fflush` + `sqrt`/`pow`/`sin` 通过）；`LinuxEmitE2eTests` string/f64 extern 结构断言 + `X64AssemblerEncodingTests` MOVD/PINSRD 字节断言 + `RuntimePrimitiveCoverageTests` 覆盖 Linux；Cocoa.Tests 全量（CodeAnalysis 1999 + CodeGen 1025 + Compiler 309）通过，Windows `.text`/`.data` 黄金字节不变。

---

## 未发布（2026-10-14）

### 编译器：native 产物 PE 版本资源（.rsrc VS_VERSIONINFO）+ 资源读取器修正
- **版本资源嵌入**：`.coproj` 元数据（Company/Product/FileVersion/AssemblyVersion/Description/Copyright）→ `PeVersionInfo` → `VersionResourceBuilder` 生成 `VS_VERSIONINFO` 资源 blob（VS_FIXEDFILEINFO + StringFileInfo/040904b0/StringTable，含 CompanyName/FileDescription/FileVersion/InternalName/OriginalFilename/ProductName/ProductVersion/LegalCopyright），经 `PeFileWriter` 布局 `.rsrc` 节并注册 Resource 数据目录（index 2）。
- **PE 注入链**：`Compilation.EmitNative` 增可选 `versionInfo` 参数（向后兼容），`NativeBackend` → `NativeCodeEmitter` → `PeFileWriter` 穿透；`ProjectBuilder.BuildPeVersionInfo` 从项目元数据构造。
- **资源目录读取器修正**：`PeResourceTable` 目录项偏移对齐 PE 规范（相对 `IMAGE_DIRECTORY_ENTRY_RESOURCE` 根，先前误用相对父目录）；叶子层判定放宽。
- 收益：native 产物携带合法版本信息，规避杀软（360 等）对无资源裸 PE 的启发式误报；IDA 不再将之手写布局误判为加壳压缩。
- 验证：新增 `VersionResource_Embedded_VsVersionInfo` 测试（x64/x86 双平台：Resource 目录树展开 + VS_VERSION_INFO 键 + 版本字符串 + e2e 运行）；全量回归 3291 通过。

---

### 编译器：IL 产物程序集版本信息（Assembly 版本 + 程序集级 CustomAttribute）
- **程序集版本 + 元数据属性**：`.coproj` 元数据（AssemblyVersion/FileVersion/Company/Product/Description/Copyright）→ `PeVersionInfo` → IL 产物 Assembly 表 Version 字段 + `AssemblyVersionAttribute`/`AssemblyFileVersionAttribute`/`AssemblyCompanyAttribute`/`AssemblyProductAttribute`/`AssemblyDescriptionAttribute`/`AssemblyCopyrightAttribute` 六个程序集级 CustomAttribute（右键「属性 → 详细信息」可见）。
- **CustomAttribute blob 编码修正（对照 System.Private.CoreLib 反编译）**：值类型 FixedArg 为裸值（bool 1B / int32 4B / char 2B / f64 8B，无 ELEMENT_TYPE 前缀——类型由 ctor 签名提供）；string 为 SerString（压缩长度 + UTF-8，无尾 null）；blob 末尾统一追加 numNamed=0（GetI2 的 2 字节）。既有 `EncodeDebuggableAttributeBlob` 误带 `0x02` 前缀 + 缺 numNamed，导致整批 Assembly 属性反射解析崩溃——修正为 `01 00 01 01 00 00`（逐字节对照 csc）。
- **自举侧同步**：`MetadataEncode.co` 的 `AttributeBlobHex`/`DebuggableBlobHex` 对齐新编码，string 改 UTF-8（`Utf8Blob`）；差分测试验证 C# 侧与自举侧一致。
- 验证：新增 `IlAssemblyVersionMetadataTests`（反射读 Assembly 版本 + 6 个属性值）；`MetadataEncodeDifferential` 通过。

---

### 编译器：exe 图标 / 通用资源 / 内嵌资源（双后端）
- **通用 .rsrc 构建器**：新增 `PeResourceData` + `ResourceSectionBuilder`——把多条资源（RT_VERSION / RT_ICON / RT_GROUP_ICON / RT_RCDATA 等）组装为三层资源目录树 + 叶子数据，替代只支持单一 RT_VERSION 的旧实现；`PeFileWriter` 按 DataEntry 偏移列表逐个回填 `.rsrc` 节 RVA。`VersionResourceBuilder` 重构为复用该构建器。
- **exe 图标**：`ApplicationIcon`（`.coproj`，此前预留未消费）→ `IconResourceBuilder` 解析 `.ico`（ICONDIR）→ `RT_GROUP_ICON` + `RT_ICON[]` 嵌入 native `.rsrc`；dotnet 产物经 `ManagedPEWriter` 追加 `.rsrc` 节（win32 图标资源）。
- **通用资源**：`<Resource Include="...">` → `RT_RCDATA` 嵌入 `.rsrc`（native 与 dotnet 产物）。
- **内嵌资源**：`<EmbeddedResource Include="...">` → IL 产物 `ManifestResource` 表 + CLR Resources 目录（`u32` 长度前缀 + 数据，8 字节对齐；COR 头 Resources RVA/Size 指向），运行时 `Assembly.GetManifestResourceStream(文件名)` 读取。
- **ManifestResource 表列序修正（对照 System.Reflection.Metadata 源码）**：行布局 `Offset(u32) + Flags(u32) + Name(#Strings) + Implementation(coded)`；`Implementation` coded index 宽按 File/AssemblyRef/ExportedType 行数判定（本实现恒 2 字节）。
- **资源管线贯通**：`PeResourceOptions`（Core 侧载体：版本 + 图标路径 + RT_RCDATA + 内嵌资源）→ `Compilation.Emit`/`EmitNative` 可选参数 → 双后端；`ProjectBuilder.BuildResourceOptions` 从 `.coproj` 组装。
- 验证：新增 `ResourceEmitTests`（native 图标树/RT_RCDATA 字节级、IL 内嵌资源 `GetManifestResourceStream` 读取回写）5 项；主套件全量 3297 通过；SelfHosting 既有失败（自举 binder `Type 'i32' doesn't exist`）与本增量无关（基线复现确认）。

---

### IDE：VS/dotnet 风格发布布局（编译器 `./Compiler` + SDK `./SDK`）
- **独立发布布局**：`tools/publish-ide.ps1` 把 `Cocoa.IDE` 发布为三件套——宿主 exe + `Compiler\`（编译器程序集 `Cocoa.*.dll`，排除 `Cocoa.IDE.dll`）+ `SDK\`（系统库 `System*.coa`），对齐 dotnet `host + sdk/` 与 VS 安装目录形态。
- **编译器运行时加载**：`Program.cs` 注册 `AssemblyLoadContext.Default.Resolving` 钩子，启动时从 `./Compiler` 按名加载 `Cocoa.*` 编译器程序集；目录缺失（开发期）回退默认 BaseDirectory 解析——不破坏 `dotnet build` 直接运行。
- **SDK 目录发现**：`SystemLibrary.ResolveStdlibDirectory` 解析优先级 `COCOA_SDK` env → `./SDK`（exe 相对）→ `COCOA_STDLIB` env → 祖先 `libs/` → `AppContext.BaseDirectory`；`COCOA_STDLIB` 单文件形态与 `libs/` 兜底保持向后兼容。
- 验证：发布脚本产出正确布局（host/Compiler/SDK），发布产物 `Cocoa.IDE.exe` 成功启动；新增 `Cod_SystemLibrary_Discovers_From_CocoaSdk` 测试（COCOA_SDK 优先于 COCOA_STDLIB）；编译器全量回归通过。

---

### IDE：文档大纲面板 + 补全关键字补齐 + 语义服务对齐
- **文档大纲面板**（VS 风格）：右侧栏改 TabControl「属性 / 大纲」。`DocumentOutlineProvider` 遍历语法树生成符号树（顶层函数/类/接口/枚举/命名空间 → 成员函数/属性/字段/事件/枚举值），随活动标签与内容实时刷新；单击条目跳转到声明行。纯语法树实现，与语义着色/补全同走编译器管线。
- **补全关键字列表**与 `SyntaxFacts.GetKeywordKind` 全集对齐：补 `and`/`or`/`not`（逻辑模式）、`yield`、`with`、`init`/`required`/`file`/`global`（C# 批次）、`operator`/`implicit`/`explicit`（运算符重载）、`params`、`nameof`、`lock`、`checked`/`unchecked`、`typeof`/`sizeof`、`field`；`record` 为上下文标识符（非语法关键字）不加，与分类器一致。
- **构建修复**：语义服务三文件补 `using Cocoa.CodeAnalysis.Bound`（`BoundVariableDeclaration` 命名空间）；`PropertiesViewModel` 移除已删的 `CocoaProjectFile.Language` 显示（去 C# 方言后语言恒为 Cocoa）。
- 语义着色基于编译器 `Classifier`、Hover/补全走 `SemanticModel`——自动反映列表模式 `..rest`/反射面（GetMethods/GetMethod/HasMethod）/主构造函数/可空标注等最新特性。
- IDE 构建 0 警告 0 错误；全量回归 3288 通过。

---

### 文档：彻底清除 C# 方言残留（去 C# 方言收尾）
- **§44 改「Cocoa 语法速查」**：删掉全部 C# 拼写列（成员/参数/局部变量/接口成员对照），仅保留 Cocoa 写法；`§44.7` 删 `.cs` 示例、`§44.8/§44.9` 删 C# 列。
- **§45 折叠为历史说明**：删掉 P1-P7 C# 式写法规表（`public int X`/`static Foo()` 等），保留「去 C# 方言前历史批次」定位，指明沿用至今的 Cocoa 拼写（表达式体/静态构造/foreach/switch/多接口/const）；索引器（6e-M24）说明保留。
- **§46 清空正文**：仅留「已删除」状态行（原 46.1-46.6 的 `.cs` 扩展名分派/严格规则/共享特性/入口/使用/子集边界全部移除）。
- **§47.1 类型词汇**删 C# 方言列（`sbyte`/`byte`/`int`/…），仅留 CO 简写。
- 正文散布的 C# 式残留同步清除：`§10.6.2` C# 风格 for（标已删除）、`§11.1.1` 顶层函数拼写（删 .cs 例）、自动属性/构造函数/delegate/事件/静态构造/using 分号/文件作用域命名空间/out-ref 声明（均删 C# 对照，保留 Cocoa 写法）；`项目格式规范.md` Source 项去 `.cs`；`标准库API参考.md` 基元别名去 `.cs`；文档基准/版本脚注改「C# 语法家族，单一 Cocoa 拼写」。
- 历史日期横幅（2026-08-20~22、2026-09-13）与 ARCHITECTURE 去方言记录保留原样。

---

### 语言特性：列表模式切片变量 `..rest` ✅
- **`[p1, ..rest, p3]` 切片变量绑定**（C# 11 slice designator）：模式匹配成功时把中段（`..` 吞掉的元素）绑定为新数组 `rest`。语法解析早已收 `..rest`（`RangeExpressionSyntax.Right`），本批补上绑定语义。
- **实现零新增发射**：复用既有惰性三元（三端短路）+ `CopyRange` 合成调用（三端已有 Array.Copy/SliceArray）+ 赋值节点。因三端 `&&` 均为急切求值，切片副作用不能挂布尔链尾（长度不足时 count 为负 → 越界），用 `cond ? (rest = CopyRange(...)) != null : false` 隔离——仅匹配时求值切片。
- 长度不足不匹配时切片不求值（惰性短路），rest 保持默认；`[..rest]` 绑定整数组、`[1, ..rest]` 绑定尾部、`[..rest, 3]` 绑定头部之外。
- IL 端 `CollectExpressionLocals` 补 `BoundAssignmentExpression`（表达式内引入的 rest 局部占槽，对齐声明模式变量）。
- 测试：`PatternDeclarationThreeBackendTests.ListRest_*` 4 例（Evaluator/IL/native 端到端 + 长度不足不越界）；全量回归 3288 通过。

---

### 语言特性：反射面子集补 `System.Type.GetMethods()` ✅
- **`Type.GetMethods(): string[]`**——宿主反射面的方法名投影。解释器（CLR `Type.GetMethods(BindingFlags.Instance|Public|NonPublic|Static)`）与 IL（`callvirt GetMethods` + 名字循环投影到 `string[]`）两端闭环；native 无宿主类型表（Type 值为 vtable 记录指针）登记为覆盖缺口，`EvaluatorTypeInfo`（用户类）返回空数组。
- **元数据签名登记修复**：`ResolveMethodRef` 对返回/参数类型补 `RegisterSignatureTypeRef`（含 SzArray 元素），`ResolveClassType` 递归重解析数组元素——否则数组元素（如 `MethodInfo`）保留 reader 的 null-scope TypeRef，写出 ResolutionScope=0（当前模块），CLR 无法匹配成员签名（`MissingMethodException`）。
- **解释器 `typeof` 对齐**：`ClrTypeOf` 按 `SpecialType` 把 Cocoa 物理简写（`i32`/`f64`/...）映射宿主类型——此前 `typeof(i32)` 落到 `typeof(object)`，系解释器与 IL 的行为分叉。
- **`HasMethod(name): bool` / `GetMethod(name): string`**（本批追加）：精确名存在性检查 / 规范名取回（未命中 `false`/`""`）。实现走 `GetMethods` 名字数组 + `Array.IndexOf` 扫描——直调 BCL `Type.GetMethod(string, flags)` 对重载/继承方法抛 `AmbiguousMatchException`（Int32.ToString 多重重载实证），扫描与既有 GetMethods 同素材、歧义安全。IL 端投影循环抽取为共享 helper（三 case 复用）；native 同为覆盖缺口。
- 测试：`RemainingFeatureProbe.Reflection_GetMethods_*` 3 例 + `Reflection_GetMethodHasMethod_*` 3 例；全量回归 3283 通过。

---

## 未发布（2026-10-08）

### 语言特性：`with` + `init` + `required` + `global using`（C# 9/10/11 批次）✅
- **`p with { 字段 = 值, ... }`**：语法（`WithKeyword`/`WithExpression` + 后缀解析）+ 绑定——**脱糖为位置参数构造器调用**（未覆盖字段原值透传，零新 Bound 节点、三后端零发射改动）。
- 接收者限定无副作用引用表达式（变量/参数/this/字段访问，避免重复求值）；赋值目标须为 record 位置字段（否则绑定诊断）；非位置参数 record 明确报错。
- **`init` 访问器**：`property X: int { get init }`——语法（`InitKeyword`）+ 绑定（`set_X` 标记 `IsInitAccessor`）+ 强制（仅所属类构造上下文可赋值，`_function.IsConstructor` 判定，非构造赋值报诊断）；发射复用 setter 管线零改动。
- **`required` 成员**：`required field/property`（`RequiredKeyword` 修饰符）——绑定标记 `IsRequired`；构造体绑定完成后按名收集赋值，缺省报诊断「required 成员 'X' 必须由构造器赋值」（BuildFunctionBody 钩子）。
- **`global using`（C# 10）✅**：`global using System`——语法（`GlobalKeyword` + using 修饰符、绿节点工厂、`IsGlobal`）；本编译器全局作用域已跨文件聚合 using，语义等价普通顶层 using。
- **`file` 类型（C# 11）✅**：`file class Foo`——语法（`FileKeyword` 修饰符）+ 绑定（`IsFileScoped`）+ 类型解析跨树过滤（`LookupType` 检查当前函数/类的声明树与目标树，跨文件不可见视为未定义）；三后端零发射改动。
- 测试：`WithExpressionThreeBackendTests` 6 例 + `InitAccessorThreeBackendTests` 5 例 + `RequiredMemberThreeBackendTests` 5 例 + `GlobalUsingThreeBackendTests` 5 例 + `FileScopedTypeThreeBackendTests` 6 例（各 Evaluator / IL(net9.0) / Native(x86+x64) 端到端 + 绑定形态/负例）。定向回归 810+128 通过。
- **显式接口实现诊断**：`function IReader.Read()` 解析已支持，但运行时分派需 IL MethodImpl 槽 + native 接口槽——加清晰编译期诊断「运行时分派尚未实现」，杜绝「编译通过 → 运行时 TypeLoadException」；排期改后置（见对照表 §7 #12）。

---

## 未发布（2026-10-08）

### 编译器质量：D3 诊断码（`Diagnostic.Code` + `--nowarn` 过滤）✅
- **`Diagnostic.Code`**（COC1001+）：首梯队 6 错误码——未定义类型 COC1001 / 未定义函数 COC1002 / 未定义变量 COC1003 / 已声明 COC1004 / 元数不匹配 COC1005 / 只读赋值 COC1006 + 警告码 COC2001（using 未解析）；`DiagnosticBag.ReportError/ReportWarning` 可选 code 参数。
- **`DiagnosticExtensions.ApplyNowarn`**：按码过滤压制警告（保留全部错误）。
- **CLI `--nowarn:<codes>`** 接线：直接编译与 `.coa` 部署路径（`--nowarn:COC2001` 实测压制 using 未解析警告）。
- **关键约束**：`ToString()` 保持 Message（不含码）——保护自举双后端诊断逐字节差分（M9-a4）与 CLI 输出稳定；码作独立元数据，CLI 输出加码镜像自举侧后再做。
- 测试 `DiagnosticCodeTests` 6 例（各码赋值 + ToString 不变 + ApplyNowarn 过滤）。
- 文档：语义债务 D3 状态翻转；其余 ~80 个 Report 方法按需渐进补码。

---

## 未发布（2026-10-08）

### 语言特性：集合表达式（C# 12）+ D1 复核销账 ✅
- **`[1, 2, 3]` / `[..[10, 20], 4]`**：新 `CollectionExpression` 语法节点 + primary 解析 + 绿节点工厂——绑定降级为数组创建（`BoundArrayCreationExpression`，三后端零发射改动）；元素类型从首个元素推导（空集合默认 `i32[]`）；`..` 字面量数组 spread 展平。
- **限制**：变量 spread（`[..a]`）暂报清晰诊断「spread 目前仅支持编译期字面量数组」（运行期数组合并留后续）。
- 测试 `CollectionExpressionThreeBackendTests` 6 例（Evaluator / IL(net9.0) / Native x86+x64 端到端 + 绑定树形态断言 + 变量 spread 负例）。回归 414 通过。
- **D1 复核销账**：重载转换计分（`ResolveOverloadByScore` 隐式转换罚分 + better-conversion-target 竞拍）早已实现——语义债务 D1 翻转为已实现。

## 未发布（2026-10-01）

### 语言特性：模式匹配 / 嵌套类 / 空条件访问 / 溢出控制 / 运算符重载（2026-09-29 ~ 10-01）
- **模式匹配补全**：IL 后端 relational / logical / property 三类模式发射（`8578abf`）；声明模式 + `when` 子句（`3b23f8c`）；**括号模式 `x is (1 or 2)`**（C# 9 模式分组，`98a7c4e`）；**值类型声明模式 `o is T v`**（unbox.any 路径 + 装箱类型 TypeRef，`18d489b`）。
- **运算符重载落地**：语法层 operator/implicit/explicit（`fe7f068`）→ 绑定与三后端 op_* 发射（`01fbe50`）→ 转换运算符 op_Implicit/op_Explicit 在转换点生效（`ab56c99`）。
- **嵌套类**（`a30b560`）：解析类体内的 class/struct 声明 + 限定名类型 + 嵌套类型登记。
- **`?.` 空条件访问 IL 发射**（`75ae54c`，此前仅求值器路径通，走 Emit 必失败）。
- **checked/unchecked 收尾**（`c5bc8f5`/`dc24aa0`）：IlOpCode 编码表逐条 CLR 校验 + 补 ovf 编码（根因：`Add_Ovf` 单字节编码），溢出检测真正生效。
- **typeof / sizeof**（`975712b`/`9b10f3e`）：语法与绑定落地 + IL 发射（修复 2 个编码与栈增长 bug）。
- **Attribute 语义消费**（`5a5d7e2`）：`[Obsolete]` 在调用点报诊断。
- **数值字面量**：数字分隔符（C# 7.0）验证可用（`55cdd4e`）；`>>>` 仅词法（明确「仅解析」，`acfce7a`）；`??=` 根因定位后归并为统一底层问题（`269d0ed`，转 Skip 桩未实施）。
- **核实已实现**：索引器与元组（`ef873ec`，补测试纠正文档误判）；成员调用 out var + params 修通（`55865aa`）。
- **审计**：语言特性对照表按实测校正（`94264f8`、`2a64b05`）；运算符覆盖审计记录两个新增缺口（`71e375e`）。
- **已知缺口（转 Skip 桩）**：集合表达式（`557b1f1`）、`??=`、`>>>` 语义、ParseExpression 递归护栏（`52c1e99`，尝试失败回退）。

### 阶段 8 里程碑：B2 == B1 fixpoint 达成（字节级一致，2026-09-30）
- **`d17d8e5`**：B2 与 B1 产物**字节级一致**——阶段 8 核心验收「B1 ≡ B2」达成。
- **ilverify 双轨错配清零**：22 条错配按 A/B/C 三类定位归零（装箱 TypeRef 登记挤占 Char / NameExpression compound text 空返回 / ReceiverVarNameOf 缺分支 / 局部签名类型判错 / string 下标 ldelem.ref 误用 / 类方法名表语料规模错位等，`0da3a1e`…`e9a4fee`）。
- **发射产物对齐修复**：.co 轨 TypeDef 命名空间（Binder/IlDriver/IlMetadataBuilder 三处，`8f0eab2`）、字符串下标改发 `String::get_Chars`（`1df7a8c`）、无初始化器变量声明多余 stloc（`ce4932b`，B2 产物首次 CLR 正常加载）、变量声明初始化器下标错位 + EmitBinaryOp 缺位运算符（`bc36027`）、GlyphOf 位运算符字形（非法方法体 59→8，`aed3f47`）。
- **护栏**：产物结构自校验门禁（`4ab5bf6`）+ ilverify 崩溃假阴性门禁修复（`3c1814b`）+ `Corpus_CompileAndDump` 固化为可复用门禁（`25dd428`）。

## 未发布（2026-09-26）

### 阶段 8：B1→B2 全量语料自举通过（2026-09-26）
- **`74ba7ce`**：全量语料（543K，含 CodeGen）自举通过——三元表达式自支撑修复。
- **B1→B2 修复链**：BCL/US 收集数组 2048 定容溢出→扩容（`2beb0fa`/`2875373`）、string[] 参数 Main/局部签名 + Console.WriteLine 签名（`3561836`）、#US 空字符串长度（`708ba5d`）、字符串实例成员索引（`0a86570`）、i32[] 字段初始化 + Stelem_I4=0x9E 澄清（`5c49930`）。

## 未发布（2026-09-25）

### 阶段 8：B1 自举闭环达成（2026-09-25）
- **`9bf1fbc`**：自举编译器（B0 产出）全量自编译 241K 语料通过——B1 自举闭环。
- **B1 探针体系**：依赖子集快速回路（单文件 Binder.co 25s/轮、14 秒自举回路）、探针分层（Binder/Syntax/Symbols，`ScaffoldChainProbe` 系列）。
- **关键修复**：自举解释器 && 不短路的**根本缺陷**（全部 `X != null && !X.IsToken()` 守卫改三元 `?:` 短路，`5480f97`）、类字段作用域（方法体直接引用 `_field`）、块作用域（嵌套局部重复声明）、KnownType 类名前向解析（`438da0f`）、arity 计参（实例方法 param0=this 裸调用省略接收者，`b6d6e55`）、静态类方法 emit（`0d175a3`）、MethodIndex 纯化 + ExprTypeOf 字段/链式推断（`b0808e7`/`a5ca973`）、B0 驱动完整发射自举编译器源码为可加载可运行 .dll（`8f18d79`）。

## 未发布（2026-09-22）

### 阶段 8 前置·类里程碑 C-1 ~ C-4（2026-09-21 ~ 09-22）
- **C-1 ✅（09-21）**：自举 parser this/new 后缀链 + binder 类地基（实例字段/方法收集、顶层/类方法索引对齐）。
- **C-2 ✅（09-22）**：自举 emit 类实例——this→ldarg0、new→newobj、ldfld/stfld、callvirt、TypeDef 字段表 + HAS_THIS 签名、隐式 ctor（`f7e894e`）。
- **C-3 ✅（09-22）**：自举 emit 数组——newarr/ldelem/stelem/ldlen（含字符串数组）+ szarray 签名（`e6d6d26`）；字符串实例成员（s.substring/IndexOf callvirt，`d812a57`）；字段写 stfld + void 实例方法 + 方法尾隐式 ret（`36a29eb`）；string(n) 转换（box Int32 + Convert.ToString，`66ca626`）；IL 汇编器完整吸收（两遍编码 + token 占位，`42fd595`）。
- **C-4 ✅（09-22）**：命名空间下钻（递归 WalkMember，`bb58fa9`）；**自举 Cli compile 子命令**（.co → 物理 .dll 全链落盘，`117de32`）。

## 未发布（2026-09-21）

### 增量五 M5-a0…a5 收官：自举 Emit 全链（2026-09-15 ~ 09-21）
- **M5-a0** 绑定树结构化输出（B0 `--bound-json`，`4c30a19`）；**M5-a1** 自举 Interpreter 后端（树遍历求值 + EvaluatorRuntime）；**M5-a2** B0 端到端（Backend 项目级声明）；**M5-a3** IL 发射骨架（IlEmitter/IlAssembler/IlMetadataBuilder 自举版）。
- **M5-a4 Native 骨架**：自举 NativeEmitter（绑定树 → LIR 文本，与 C# LirPrinter 逐行一致）+ X64Assembler 编码内核切片 1/2/3（REX/ModRM/SIB、LirToAssembler 布局无关符号化差分、runtime stub 一致性）。
- **M5-a5 自举 ManagedPEWriter（2026-09-20 ~ 09-21）**：自举侧实现物理 .dll 写出全链——批 1-3 端到端（纯算术 Main + 局部变量实跑，`7cdbcaf`）→ 多函数（参数/内部调用/控制流，`00dea99`）→ **BCL/字符串**（Console.WriteLine + 字符串字面量，`cfd5702`）→ **字符串运算**（`==`/`!=`/`+`，String.op_Equality/op_Inequality/Concat，`a50ecea`）。

### tools 构建脚本（2026-09-15）
- **tools 新增**：`build-co` / `build-cs` / `build-ui` / `build-ide` 构建脚本；`build-sdk` 同步收集到 `tools\cocoa-sdk`（`System.Core.coa` 落仓，`d6f5b5c`）。

## 未发布（2026-09-15）

### 结构整理（2026-09）：CoaFormat 拆分 + 前端重命名
- **`Cocoa.Compiler` 更名 `Cocoa.CodeAnalysis`**（文件夹 + csproj + 程序集名三位一体对齐命名空间）：更新 7 处 ProjectReference、`Cocoa.slnx`、README/快速上手路径；测试引用的自举目录 `src/Cocoa.Co/Cocoa.Compiler` 不受影响。
- **CoaSerializer 拆分**：门面 `CoaSerializer` + 子命名空间 `Cocoa.CodeAnalysis.Serialization.CoaFormat`（`CoaFormatWriter`/`CoaFormatReader`/`CoaRegistry`/`CoaTypeResolver`/`CoaTextWriter`/`CoaTextReader`/`CoaReadContext`/`CoaText`/`CoaTypeText`），去 partial、实例化协调器。
- **Syntax 目录归位**：`CocoaGreenNodeFactory` 拆 partial 并移入 `Syntax/Green/`，`RedNode` 移回 `Syntax/` 根。
- **cl 目录合并**：`Evaluation/` 单文件目录并入 `Compilation/`（命名空间保持 `.Evaluation`，避免与 `Compilation` 类同名冲突）。
- **nullable 清零**：`Cli/Cocoa.Cli.csproj` 移除 8 个压制编号（0 警告），更新 `Directory.Build.props` 棘轮注释。
- **自举 hex 去重**：5 个 `.co` 的 `HexByte/HexU16/HexU32` 收敛到 `CodeGen/HexCodec.co`。
- 全量回归：C# **53,364 通过 / 0 失败**；自举 native **135 通过 / 0 失败**。

### 阶段 7 增量五 M5-a0…a4 规划：自举 Emit 实施计划（2026-09-15）
- **增量五计划定稿**：5 个子步（M5-a0 结构化输出 → M5-a1 Interpreter 后端 → M5-a2 B0 端到端 → M5-a3 IL 骨架 → M5-a4 Native 骨架）。
- 架构决策：`CodeGen/CodeGen.coproj` 收敛 C# 7 个 CodeGen csproj 为 1，Native/IL 子目录对称；Interpreter 为首个实现目标（树遍历求值，差分验证）。
- 计划文档：`docs-dev/plan/自举实施计划.md` §9。

### 阶段 7 增量四续：break/continue/step + 嵌套控制流（2026-09-15）
- **自举 Binder 新增**：循环栈（`PushLoopLabels`/`PopLoopLabels`）+ `BreakStatement`/`ContinueStatement` → `GotoStatement` 转换；`BuildBound` 补全 break/continue 节点透传（修复被静默丢弃的 bug）。
- **ForRange step 修复**：`hasStep` 条件修正（BuildBound 后 child index 识别）；`LowerBlockStmts` 递归展平嵌套 BlockStatement。
- **BoundCorpus 5→13 条**：新增 while+break（if 内）、while+continue（if 内）、for-range+step、嵌套 for+while；全量 53,364 通过 / 0 失败。

## 未发布（2026-09-14）

### 阶段 7 增量四 M9-a5：自举绑定树差分——控制流降级（2026-09-14）
- 自举 Binder 新增 lowering：`IfStatement` → `ConditionalGoto`+`Label`（无else/有else），`WhileStatement` → `Goto`+`Label`+`Body`+`ConditionalGoto`（双计数器对齐 C# Binder break/continue + Lowerer body 标签命名）。
- C# 基准 `DumpBound` 扩展 `LabelStatement`/`GotoStatement`/`ConditionalGotoStatement`；`SequencePointStatement` 透传。
- `BoundCorpus` 5 组（直线代码 + if + while）逐字节一致；全量 53,364 通过 / 1 跳过。
- 修复：`MakeLeaf` → `Node.MakeToken`；BlockStatement 嵌套扁平化；`_labelCount` 按函数重置 + `_loopLabelCount` 跨函数共享。

### 阶段 7 增量三 M9-a4 收官：诊断消息逐字节对齐（2026-09-14）
- **C# 基准对齐**：语句验证（仅赋值/调用可作表达式语句）；`cannot-declare` 两次重复输出（绑定器两遍各报一次）；`Function 'X' doesn't exist.`/`Variable 'X' doesn't exist.`/`'x' is already declared.`/`Only assignment and call expressions can be used as a statement.` 等消息句式逐字节对齐。
- **差分**：9 组无效程序 C# 诊断输出 `SequenceEqual`；`SelfBinder_Diagnostics_Match_CSharp_ForInvalidCorpus` 测试全绿；全量 53,364 通过 / 1 跳过。

### 阶段 7 增量三 M9-a3：自举 Binder 局部符号差分（2026-09-14）
- **自举侧**：新增 `Binding/LocalSymbol.co`；`FunctionSymbol` 增局部数组与 `AddLocal/LocalCount/Local/LocalTypeOf/ParamType/ReturnType` 访问器；Binder 函数体声明序收集局部，类型解析=显式子句优先、否则值推断（字面量/一元/二元/名称/调用/括号）。
- **C# 基准**：`SymbolDump` 经 `GetProgram()` 绑定树递归收集 `BoundVariableDeclaration`；局部行缩进 2 空格紧跟函数行，仅绑定无错时输出（双方言门控一致）。
- **差分**：语料 11→15（简单局部/嵌套块/多类型推断+显式标注/跨函数与全局引用）逐字节一致；全量 53,362 通过 / 1 跳过。

### 阶段 7 结构重组定稿：程序集级拆分 + cosln 入口（2026-09-14）
- **定稿**：位置保留 `src/Cocoa.Co`（顶层归位复议回退）；coproj 粒度对齐 C# 程序集边界（`Cocoa.Compiler.coproj` 管线库产 `.coa` + `Cli/Cocoa.Cli.coproj` B0 入口），`Cocoa.Co.cosln` 唯一构建入口；蓝图见 `src/Cocoa.Co/README.md`。
- **Backend 项目级声明**：coproj 新增 `<Backend>` property（命令行 `-b` > 项目声明 > dotnet 缺省）；`Cli` 声明 native（自举终态自足）。
- **目录精简**：自举侧通用 `Node` 使终态 ≈30 文件，Compiler 内收敛为 `Syntax/Symbols/Binding`（+增量四 `Lowering`、根级散文件）；`Evaluation` 砍、`Bound` 并入 Binding、7 个 CodeGen csproj 收敛为 1 coproj。

### 阶段 7 增量三 M9-a2：自举 Binder 诊断面 + 函数体检查差分（2026-09-14）
- **诊断面**：自举 Binder 新增 `ReportError`/`DiagnosticCount()`/`DiagnosticAt(i)`（`error: ` 前缀，消息句式对齐 C# `GetDiagnostics`）；7 类检查——未定义变量/函数、调用 arity、未知类型（签名/局部/全局）、重复声明（参数/全局/函数/局部）、main 与全局语句互斥。
- **两遍式绑定**：第一遍声明全部顶层符号（前向可见，对齐 C# 全局作用域语义），第二遍函数体/初始化/类型检查；`MemberCall` receiver、Lambda、Foreach 宽松放行待 M9-a3 类型面收紧。
- **差分演进**：`SelfBinder_ReportsErrors_ForInvalidCorpus` 9 组无效程序双方言同报错；有效语料 11 个符号 dump 逐字节一致且诊断"有/无"双方言一致；`BinderDifferentialTests` 输出剥离诊断行。
- **B0 冒烟**：自足临时源断言符号行 + parser/binder 零诊断；样例 main.co（调用外部定义函数）确认双方言同报未定义。

### 阶段 7 增量三 M9-a0/a1：自举 Binder 符号声明面 + 差分基建（2026-09-14）
- **差分基建**：自举 `Binder/Binder.co`（namespace `MiniBinder`）按 kind+子索引遍历自举 Parser 树（`Node` 补访问器）；`BinderDifferentialTests.SymbolDump(Compilation)` 为 C# 基准，自举 `DescribeSymbols()` 输出同构行（`CanonicalType` 规范名映射 + 字面量 var 推断 + `main: Main` 判定）。
- **符号面差分 11 语料逐字节一致**（函数签名/全局变量/推断/规范名映射）；`SelfBinderDump` 走 Lexer/Parser/Binder 三源 + Evaluator 端到端（复用增量二模式）。
- **B0 扩展**：`Main.co` 输出 `--- symbols ---` 段；`BootstrapperSmokeTests` ×双后端断言 `function Main(): void`/`main: Main`。
- **结构规范统一**：自举源码一类一文件（`Token.co`/`Node.co`/`FunctionSymbol.co`/`VariableSymbol.co` 独立成文件，文件名==主类名），coproj 与 5 处测试源码加载点同步。

### 阶段 7 增量二 M8-a11/a12 收官：自举 Syntax/Parser 完成（2026-09-14）
- **M8-a11 B0 扩展**：`Cocoa.Co.coproj` 纳入 `Parser\Parser.co`，`Main.co` 读文件 → token 输出 → `--- tree ---` 规范树 dump + 诊断行；`BootstrapperSmokeTests` ×双后端（dotnet net9.0 / native x64）断言树与零诊断。
- **M8-a12 错误恢复与诊断差分**：自举 Parser 新增诊断面（`Match` 失配/兜底 token 报告，含行列与期望/实际 kind）；`SelfParser_ReportsErrors_ForInvalidCorpus` 6 组无效程序（缺括号/缺表达式/缺名字/枚举缺逗号/括号未闭合等）**双方言同报错**；有效程序零诊断由 39 语料 + 33 样例的逐字节树比较隐式锁定。
- **增量二验收达成**：样例 33/33、差分语料 39 逐字节一致、无效程序同报错、B0 双后端打印树。下一里程碑：增量三（自举 Binder）。

### 阶段 7 增量二 M8-a9/a10：自举 Parser 样例覆盖率 33/33 全绿（2026-09-14）
- **M8-a9 声明面 + 链式后缀**：类型声明前置修饰符（`public class/struct/interface/enum`）、`enum`（逗号分隔成员 + `= 值`）、`namespace`（块式，名 token 含点号）、类内 `import <dll> { ... }` 块；标识符后缀链改左结合循环（`a.b.c` / `x.y.z.W()` / `a[i].b`）；`IsModifier` 对齐 C# 全集。样例覆盖率 21→26/33（差分语料 36）。
- **M8-a10 数值/lambda/cast**：自举 Lexer 数字 kind 细分 `Number`/`Double`（f 后缀·小数点·指数 → Double，对齐 C# `DoubleToken`）；Parser 补 `DoubleToken` 字面量、**块体 lambda**（`() => { … }`）、cast 操作数降为 unary 级（对齐 `ParseBinaryExpression(6)`）、`IsCastStart` 前瞻补齐。样例覆盖率 26→**33/33 全绿**（差分语料 39，全部逐字节一致）。
- 修复即捕获三处真实缺陷：自举 Lexer float 与 int 同 kind、块体 lambda 的 `{` 被兜底单 token 吞掉、cast 贪婪吞掉低优先级右操作数。

### 项目结构优化：清除 C# 语言残留 + 摊平方言层（2026-09-14）
- **删除 C# 方言死代码**：`SyntaxKindLanguageOwnership`（零引用）、`Cli/Cocoa.Compiler.Cocoa`（`coc` 独立 exe，与 `cocoa` CLI 重复）及其 `Cocoa.Cli.Program.CompileForLanguage`；`Cocoa.Tests/LanguageSeeding.cs`（无注册表的空种子）。
- **摊平方言层**：`Cocoa.Compiler/Cocoa/{Binder,Syntax,Compilation}` 并入核心目录，命名空间 `Cocoa.CodeAnalysis.Cocoa.*` → `Cocoa.CodeAnalysis.*`（去 C# 方言后单语言，无第二实现）。
- **塌缩单语言抽象**：`CocoaSyntaxNode` 并入 `SyntaxNode`；`CocoaCompilation`/`CocoaSemanticModel` 并入 `Compilation`/`SemanticModel`（具体类）；移除 `IParser`/`ILexer`。
- **彻底移除 `Language` 门面**：内建类型解析移至 `BuiltinTypes.Lookup`；节点位置/根成员辅助归位 `SyntaxNode.GetUnreachableCodeLocation/GetDeclarationNameLocation/HasDeclaredFacadeModifier` 与 `SyntaxTree.GetRootMembers/GetDeclaredNamespaceNames`；泛型用法扫描归 `Monomorphizer.CollectGenericUsages`。删除 `SyntaxTree.Language`/`Parse(…, Language)`/`FromGreen(language)`、`Compilation.Language`、`BindProgram(dialect)` 等语言选择面（IDE/测试同步）；顺带移除仅由语言工厂使用的死接口 `IBinder`。
- **文档**：重写 [`CODING.md`](CODING.md) 为现行单语言结构与流程；[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) v3.0 删除早期双前端重构方案明细；IDE 去 `.cs` 残留（文件提示、`IconCSharpBrush`）。

### 阶段 7 自举：自举 Lexer 完成 + 自举 Parser 进行中（2026-09-13/14）
- **增量一（自举 Lexer）✅**：`src/Cocoa.Co/Lexer/Lexer.co` 结构化 Token（kind/text/行/列/offset）+ `Describe()`；token 面补齐字符串家族（verbatim/raw/插值）、二进制·下划线·后缀数字、`///` 与块注释、运算符/关键字全集；与 C# `CocoaLexer` **非 trivia 逐 token 差分一致（44 语料）**；native ≥1MB 秒级护栏；B0 骨架 `src/Cocoa.Co/{Cocoa.Co.coproj,Main.co}` 双后端（dotnet net9.0 / native x64）可运行冒烟。
- **增量二（自举 Parser）🔄 进行中**：`src/Cocoa.Co/Parser/Parser.co` 递归下降 → 自举 `Node` 树，`Dump()` 与 C# 规范树 dump（`ParserDifferentialTests.TreeDump`）逐字节一致。M8-a0…a8 覆盖：函数/类/结构/接口/构造/属性/字段（含初始化器）、控制流（if/while/do/for/for-range/foreach/try）、`using`/`new`（对象·泛型·数组创建）、数组类型/元素访问/元素访问链、强制转换、`is`/`as`/`??`。**差分语料 39 个逐字节一致；samples 覆盖率 21/33**（扫描工具落盘 `%TEMP%\cocoa-parser-sample-sweep.txt`）。剩余缺口：`namespace`、`enum`（单行/`= 值`）、`import` 块、类型声明前置修饰符、多级链式访问、`switch`/`throw`、插值洞、`>>` 泛型收尾。
- **架构重构（2026-09-14）**：`Cocoa.Compiler.Core` 上移一层并更名 **`Cocoa.Compiler`**（`src/Cocoa.Cs/Cocoa.Compiler`），同步 9 处 csproj 引用与 slnx；全量测试 **53358** 通过。

### 去 C# 方言（只留 .co）——架构塌缩（2026-09-13）
- **C# 方言整体移除**：删除 `Cocoa.Dialects.CSharp`（104 源文件 ~920KB，整前端手写双份 + 逐字节镜像 CSharpBinder）+ `Cocoa.Compiler.CSharp`（csc）+ 6 个方言测试类；迁移/删除全部 `ParseCs` 测试（基线 53494 → 53354，删 ~140 个）。
- **编译器 Core 钩子简化**：删 `Language.CSharp`/`ParseCs`/`.cs` 扩展名分派/`ParametersAreTypeFirst`；语言中间层塌缩——`CocoaLanguage` 并入 `Language` 具体类（删注册表/`GetOrThrow`/抽象分派，单实现直接用）；`Cocoa.Dialects.Cocoa`（108 文件）并入 `Cocoa.Compiler.Core` 单装配件（去反射装载）。
- **B 层重复合并**：`CocoaSyntaxKind`/`CocoaSyntaxKindMappings`/`CocoaKind()` → 共享 `SyntaxKind`；`CocoaSyntaxFacts` → 共享 `SyntaxFacts`（去 `new abstract Kind` 遮蔽与重复 Keyword 表）。
- **IDE/CLI/样例去语言选择**：`cocoa new csharp` 模板、IDE `csharp`/`library-cs` 模板、`.cs` 高亮/图标/文件选择器、`CocoaProjectLanguage.CSharp`、`CSharpDialect` 样例全部移除；文档（语法对照表删除、语法手册 §46、编译手册、快速上手）收敛为单 `.co` 拼写。
- 验证：全量 **53354** 通过 / 1 跳过；IDE/CLI/样例双后端构建绿。

### 6e-M25 阶段 5：声明式语法糖 + 一控件一文件（2026-09-13）
- **声明式层**：`UIView.Body(gui)` 组件约定 + `Ui.VStack/HStack/Group/Panel/Render` 组合子（lambda `() => { ... }`，Elm/Flutter 风格，保序立即模式）；`ImGui.BeginHorizontal/EndHorizontal` + `ImGuiWindow.Horizontal` 水平布局。
- **一控件一文件**：`ImGui` 改 `partial class`，核心留 `ImGui.co`，控件移至 `Widgets/`（Label/CheckBox/TextBox/TrackBar/ProgressBar/Separator/GroupBox/TreeView/Button/Layout）；`System.UI.coproj` 的 `Widgets/*.co` 生效。
- **编译器前提修复**：① `.coa` 零参函数类型 `fnty{;void}` 读侧空段跳过；② `.coa` 方法新增 `virt/abs/ovr/seal` 位（跨库派生 override）；③ 闭包环境类同名捕获字段去重 + lambda 体补 `Lowerer.Lower`（IL/native 遇结构化 `if` 抛错）。按漂移护栏同步 C# 侧 binder。
- **示例**：新增 `samples/Samples/UI/DeclarativeUI`（UIView 子类 + VStack/HStack/Group/Panel）。
- 验证：System.UI（IL）/BasicUI/AdvancedUI/DeclarativeUI（IL）/NativeUI（native）构建并运行；`UiCoreTests` 7 例 + 漂移护栏绿；全量见下。
- 规划 §8 状态行 + §17 实施记录；手册升为阶段 5（新增声明式与代码组织两节）。

### 6e-M25 阶段 4：native 后端 UI + WinForms 控件命名（2026-09-13）
- **native extern 参数编组**（编译器）：`EmitExternCall` 先求值并编组全部实参再统一 `SetArg`（修复运行时调用覆盖已就位参数）；`string → null 结尾 LPCWSTR`（运行时 `ExternWidePtr0..3` + 4 独立编组缓冲）；值类型数组 → 元素区指针（`base+8`）。e2e：`GetModuleHandleW`/`CreateWindowExW`（12 参）原生通过。
- **同一份 System.UI 双后端**：无需 `Win32NativeImports`/简化控件集，`.coa` 直接经 `-b native --platform x64` 构建运行。
- **WinForms 命名**：`Text→Label`、`TextColored→LabelColored`、`InputText→TextBox`、`Checkbox→CheckBox`、`SliderInt→TrackBar`、`SliderFloat→TrackBarFloat`、`CollapsingHeader→GroupBox`、`TreeNode/TreePop→TreeView/EndTreeView`、`BeginChild/EndChild→BeginPanel/EndPanel`；文档增 WinForms 对照表。
- **示例**：新增 `samples/Samples/UI/NativeUI`（native x64 最小集）；BasicUI/AdvancedUI 同步改名。
- 验证：BasicUI/NativeUI 原生构建并进入窗口渲染循环；`UiCoreTests` 5 例绿；全量 **53486** 通过 / 1 跳过。
- 规划 §8 状态行 + §16 实施记录；手册升为阶段 4。

### 6e-M25 阶段 3：主题系统（Dark/Light/Classic + 样式栈）（2026-09-13）
- **预设**：`ImGuiStyle.MakeClassic()`（Dear ImGui Classic 风：黑底灰阶控件），与既有 `MakeDark`/`MakeLight` 三态可运行期切换。
- **样式栈**：`ImGui.PushStyleColor/PopStyleColor`（颜色 LIFO 覆盖，`ColorOf` 栈顶优先）与 `PushStyleVar/PopStyleVar`（新 `ImGuiStyleVar` 枚举：`Alpha`/`FramePaddingX/Y`/`ItemSpacingX/Y`/`FrameRounding`，`VarOf` 生效）；控件全部改经 `ColorOf`/`VarOf` 读取，即时响应覆盖。
- **示例**：`AdvancedUI` 主题按钮三态循环 Dark→Light→Classic；计数为正时 `PushStyleColor` 高亮。
- 测试 +1（`ImGui_PushStyle_Evaluator`：覆盖/回落 + Classic 校验）；全量 **53484** 通过 / 1 跳过。
- 文档：`docs/UI库手册.md` 升为阶段 3；规划 §8 状态行 + §15 实施记录。

### 6e-M25 阶段 2：System.UI 完整控件集 + 键盘/滚轮输入 + Child 滚动（2026-09-13）
- **控件**：`ImGui` 增 `Indent/Unindent`、`Spacing`、`Dummy`、`TextColored`、`SliderInt`、`CollapsingHeader`、`TreeNode/TreePop`，抽出 `Clicked/Fraction/DrawTrack` 复用。
- **文本输入**：`ImGuiIO` 字符队列（`AddInputCharacter/CharAt/CharCount/ClearChars`）+ `ImGui.InputText`（点击聚焦/退格/`maxLen`/聚焦高亮，内部扁平 `char[]` 经 `StringSyscall.StringFromChars`）；`Win32Window.Pump` 接 `WM_CHAR`（`TranslateMessage`，`MSG` 缓冲扩至 48 字节）。
- **子区域与滚动**：`ImGuiDrawList.SetClipRect/ClearClip`（轴对齐裁剪，矩形精确/文本按行剔除）；`ImGui.BeginChild/EndChild`（独立游标/裁剪 + 滚轮滚动，滚动量经 storage 持久）；`Win32Window.Pump` 接 `WM_MOUSEWHEEL` → `io.MouseWheel`。
- **示例**：新增 `samples/Samples/UI/AdvancedUI`（双 `BeginChild` 面板 + 导航树/折叠 + 计数/进度/滑块/输入 + 可滚动列表 + 运行期深/浅主题切换）。
- 测试 +3（`ImGui_Widgets_Evaluator` / `ImGui_InputText_Evaluator` / `ImGui_ChildScrollClip_Evaluator`）；全量 **53483** 通过 / 1 跳过。
- 文档：`docs/UI库手册.md` 升为阶段 2（组件/限制/综合示例）；规划 §8 状态行、§14 实施记录。

### 6e-M25 阶段 1：System.UI（立即模式 UI 库 + Win32 GDI 轮询后端 + BasicUI，2026-09-13）
- **新库**：`src/Cocoa.UI/`（`System.UI.coa`，方案 B 独立库，不进 libs/）。核心：`ImTypes`(ImVec2/ImVec4 struct)、`ImGuiID`(FNV-1a)、`ImGuiStorage`(开放寻址 i32→i32/bool/f32)、`ImGuiStyle`(ABGR 打包 + Dark/Light)、`ImGuiIO`、`ImGuiWindow`+`ImGuiLayout`、`ImGuiDrawList`(顶点/索引/文本命令)、`ImGui` 门面（Begin/End/Text/Button/Checkbox/SliderFloat/ProgressBar/Separator/SameLine）。
- **Win32 后端**：`Backends/Win32Imports`（user32/kernel32/gdi32 import，句柄 nint）+ `Win32Window`（内建 STATIC 类建窗 + PeekMessage/GetCursorPos/GetAsyncKeyState 轮询 + ESC 退出）+ `Win32GDIBackend`（memDC 双缓冲 + DrawList→Polygon/TextOutW + BitBlt）。
- **示例**：`samples/Samples/UI/BasicUI`（显式 Reference System.UI.coa）；端到端弹出窗口并渲染，无异常。
- **编译器修复（支撑跨库 + UI 库）**：① `.coa` 门禁放行跨库 cod 基类 + IL 跨库 `.ctor` 链 + 派生类不重列基类接口（跨程序集 TypeLoad 修复）；② `.coa` 值编解码补 u32/f32；③ 求值器值类型数组默认零值；④ `.coa` 读侧字段/属性类型延后解析（**前向引用**）；⑤ 体读侧 `memberassign` 支持变量目标；⑥ **IL 支持 `f32[]`**。
- 回归：编译器全量 **53480** 通过 / 1 跳过（1 例已知 flaky 重跑通过）；IDE 构建不受影响。
- 规划状态回填 [`docs-dev/plan/UI库规划.md`](docs-dev/plan/UI库规划.md) §13（实施偏离纪要）。

### Cocoa.IDE M6：主题切换 / 语义着色 / 选项页 / 启动弹窗（2026-09-13）
- **编译器侧前置**：`Classifier`/`Classification`/`ClassifiedSpan` 从 `Cocoa.Cli.Repl.Authoring` 物理迁入 `Cocoa.Compiler.Core`（命名空间 `Cocoa.CodeAnalysis.Authoring`），IDE 不再依赖 REPL 程序集；**C3** 构建带位置诊断补 `error:`/`warning:` 前缀，`BuildService` 据此区分严重性并写入错误列表。
- **主题**：`%LOCALAPPDATA%\Cocoa\IDE\settings.json`（`System.Text.Json` + 原子写）+ `App.axaml` `ThemeDictionaries`(Dark/Light) 命名画刷；MainWindow/EditorPane/EditorView/浮窗/Icons/Dialog 共 ~85 处硬编码色资源化。`视图 → 主题` 即时切换并持久化，解决方案树图标随主题重算。
- **编辑器语义着色**：`DiagnosticService` 重解析后随诊断下发 `SyntaxTree`，新增 `SemanticColorizer`（`DocumentColorizingTransformer`：预分类整树 + 按行二分 + 主题调色板），替代 xshd 静态高亮。
- **选项页**：`工具 → 选项`（深/浅主题、编辑器字体/字号、启动显示开关、清除最近列表），保存即时应用。
- **启动「最近/固定项目」窗口**：`StartupDialog` 列出最近 `.cosln/.coproj`（固定置顶、失效置灰、双击打开、右键固定/移除/定位/复制路径）+ 新建/打开入口；`MainWindow.Opened` 按启动参数直开或弹窗。
- IDE 构建冒烟通过；编译器全量 **53477** 通过 / 1 跳过。

### 6e-M24：`///` 文档注释（XML 文档文件 + `.coa` 内嵌 + REPL `#docs`，2026-09-12）
- **语言特性**：`///` 单行文档注释（双方言一致；`////` 及以上归普通注释），新 Trivia `SingleLineDocCommentTrivia`；标签集 `<summary>/<param>/<returns>/<remarks>`，未知标签宽容保留。
- **提取与挂点**：`DocCommentExtractor`（多行合并、结构化标签、格式警告）+ `Symbol.DocumentationText`（Binder 在顶层函数/类/接口/枚举/delegate + 方法/构造/属性/字段/事件共 12 处回填）；格式异常经 `ReportMalformedDocComment` 报警告。
- **XML 文档文件**：`DocIdBuilder`（.NET DocID：`T:/M:/F:/P:/E:`，关键字→`System.Int32` 等全名映射，无参省略括号）+ `DocumentationFileWriter`（保留 `<param name>`、XML 转义、按 DocID 排序去重、无文档跳过）。`build --doc` 与 coproj `<DocumentationFile>` 开启（默认关），exe/library/.coa 三产物均写出同名 `.xml`。
- **`.coa` 内嵌**：manifest 后新增可选 `(docs …)` 段（DocID → 规范化原文）；读侧按 DocID 回填符号 `DocumentationText`（stdlib 等跨程序集符号文档随程序集分发）。
- **REPL**：`#docs`（列出带文档符号）、`#docs <名>`（查看完整文档）、`ls` 符号清单附 summary 首行。
- **stdlib 覆盖**：`System.Core` 的 Console/Math/String/Int32 公开 API 补 `///`（Collections 既有注释一并进入 `.coa`）；`libs/System.Core.coa` 重建入库。
- **修复的阻断缺陷**：多行文档只取首行（换行 trivia 误断块）；类成员文档不入库（枚举漏类成员）；`.coa` 读侧不回填；XML 丢失 `<param name>` 属性。
- 测试 +33（词法/提取/DocID/写出/回填/`.coa` 往返/exe `--doc` e2e/跨程序集 stdlib 文档）；全量 **53471** 通过。
- 设计稿归档 [`docs-dev/archive/文档注释设计.md`](docs-dev/archive/文档注释设计.md)（含实现偏离纪要）。

### 6l/0b：native extern 参数上限移除（7→无限制，2026-09-11）
- 删除前端 `NativeImportValidator` 参数数检查与后端 `MirToLir` >7 抛出守卫——x64/x86 后端本就按 `argCount` 循环处理任意参数量，上限纯为遗留硬编码。
- 新增 e2e：12 参 stdcall `CreateWindowExW`（×x64/x86）+ 5 参 `GetDiskFreeSpaceExW` + 7 参 `ReadFile`。
- 全量 **53438** 绿（0de27ea）。

### 6l/0a：Handle SDK（Handle 基类 + FileHandle/ProcessHandle，2026-09-10/11）
- `System.Core/Handle/` 新增 `Handle`（虚 `Dispose()`，`Raw: long` 句柄）+ `FileHandle`/`ProcessHandle`（override Dispose）+ `Kernel32.CloseHandle` lazy extern；System.Core.coa 重建入库。
- `.coa` 类继承链基类序列化（Handle 前置）；override 解析走全继承链（`classType.GetMethods`）；`OrderClassesByBaseFirst` 改语法级依赖解析（`BaseType` 未就绪时读 `Syntax.BaseTypes`），解决 SDK 构建下 FileHandle 先于 Handle 处理导致的 override 失败。
- HandleThreeBackendTests 4 例 × 三后端绿；全量 **53435** 绿（8fb456b/047127e）。

### 阶段 6 收尾 C9 收官（2026-09-11）
- 并轨期清账六步 A-F **全部落地**：Step B foreach 消费 .coa 集合 + Dictionary 枚举器（26/26）→ Step C 统一动态链接（闭包 NRE 消除）→ Step D fnty/evt/dlgalias 库体序列化（含 delegate 真实类型化 M0-M6）→ Step E refcod 拓扑 + 跨库同名 CS0104 式消歧 → Step F 动态链接运行期闭环（事件/链式/捕获闭包，专项 71/71）。
- 文档同步（阶段6收尾方案状态行 / 开发计划 G7·C9 行）+ 全量回归 **53438** 绿；详见 [`docs-dev/阶段6收尾方案.md`](docs-dev/阶段6收尾方案.md)。

### UI 生态系统规划定稿（Handle + System.UI，6e-M25 规划，纯文档）
- 新增 [`docs-dev/plan/UI库规划.md`](docs-dev/plan/UI库规划.md)：① `Handle` 通用资源句柄类型入 System.Core（`Raw: long`，修复既有 import `i32` 句柄 64 位截断隐患）；② `System.UI` 独立 `.coa` 库（`src/Cocoa.UI/`，ImGui 式立即模式，IL 完整 + Native 简化双后端，GDI 轮询后端）；③ 远期声明式语法糖（函数调用风格）。
- 关键决策（ADR A1-A7，登记 docs-dev/README §4）：立即模式（XAML 式标记不采用）；无回调轮询架构（DefWindowProc 地址 + PeekMessage，规避 WNDPROC 函数指针）；分发方案 B（Reference 显式引入，不进 libs/ 避免自动枚举吞并）；UI 库位于 `src/Cocoa.UI/` 与 Cocoa.Cs/SDK 平级。
- 同期定位两项**编译器前置增强**：`.coa` 序列化门禁扩展（带属性实例类/含 body 静态类入库——Handle/实体类硬前置，与「流式库」前置项同源）；native extern 参数上限 7→12+（CreateWindowExW 12 参硬需求）——两项均已于 6l/0a、6l/0b **实施落地**（见下方条目）。
- 文档：`docs-dev/README.md`（plan/ 表 + ADR 索引 ×5）、`docs-dev/开发计划.md`（新增 §6l 执行序列 0a-6）同步。

### 自举 IO 底层原语收口 + System.IO 门面化前期（P1/P2/P3）
- `ReadAllBytes` / `WriteAllBytes`（二进制全读写）三后端落地：native `_fileBuffer` `_wfopen/fread×2 计长+回零重读 / fwrite`，fail→空数组；`RuntimeIoSyscallThreeBackendTests` 覆盖往返。
- 新增 UTF-8↔UTF-16 原语 `StringFromBytes` / `StringToBytes`（三后端；native 经 MultiByteToWideChar 与手写代理对编码），顺带修复 IL 调用 facade receiver 压栈序缺陷。
- `LaunchProcess` 由 2 参升 3 参（`workdir`），native 用 `SetCurrentDirectoryW` 临时切换 + `_wsystem` + 恢复；既有 LaunchProcessTests 迁移。
- FacadeTargets 扩列 `System.IO.{File,Directory,FileInfo,DirectoryInfo,FileStream,StreamReader,StreamWriter}` 与 `System.Diagnostics.{Process,ProcessStartInfo}`（可口供 IL facade 直连 BCL）；`System.IO.File` 转 `facade class`，System.Core.coa 重编入库。
- 全量回归 **41879** 绿（1 Skip：native 子进程相对 cwd 落点待核）。
- 已知边界：`.coa` 序列化门禁仍为 6b 后置——带属性实例类 / 含 body 静态类不可入库，`System.IO`/`System.Diagnostics` 库本轮撤回，后续作为「流式库 + Process 完整状态机」前置项（见 docs-dev/plan/自举缺口分析.md）。

### 项目格式重构（INI → SDK-style XML，2026-09-06）
- `.cocproj`/`.cscproj` → 统一 `.coproj`；`.cosln` 与 `.coproj.user` 一并 XML 化（`<Solution Version="1">` / `<Project Version="1">`）；旧 INI 解析器移除。
- SDK-style 结构：5 组 `PropertyGroup Label`（Language / Assembly / Target / Output / Build）+ `ItemGroup`（Source / Reference / Import / Content）+ 组级 `Condition`（最小子集；默认值守卫 `== '` 惯用法）。
- 属性面：必填 `<Language>`（Cocoa / CSharp，单语言项目）、`OutputType`（Executable / Library / Cocoa）、`StartupObject`（三种形态）、`TargetFramework`、`Configuration`（Debug/Release，`--debug/--release`）取代旧 `output`/`entry`/`dotnetRuntime`/`debug`；新增程序集元数据 / `TargetOS` / `Subsystem` / `ApplicationIcon` / `Content`；移除 `incremental`；`Platform` 默认 `AnyCPU`（native 显式指定）。
- CLI 扩展名统一 `.coproj`；`cocoa new` 五模板 XML 化；`add/remove reference` 改 XML DOM 增删。
- 迁移：18 个 samples + `samples.cosln` + `src/Cocoa.SDK/*`（含错拼文件名转正）。
- 测试：解析器重写（XML/守卫/Condition/`.user`/未知元素）+ Compiler e2e 迁移；全量 **41866** 通过。
- 文档：`docs/项目格式规范.md` 更新；`docs/编译手册/快速上手/语法手册(§46.1)/互操作手册/ARCHITECTURE` 同步；更新记录入 `docs-dev/开发计划.md`。

### 文档体系整理（本轮）
- 文档分层重构：`docs/`（正式参考）与 `docs-dev/`（开发文档：总纲 / `plan/` 规划 / `archive/` 已实现归档）分离；新增双 README 索引 + [docs/文档格式规范.md](docs/文档格式规范.md)（全仓 .md 约定）。
- 合并三族重叠文档：架构（实现目标 + Roslyn 蓝图 + 符号模型对齐 → ARCHITECTURE §9/§10）、IR（前端拆分 + HIR/LIR 格式 → plan/IR分层与格式设计）、标准库（类库设计 + SDK 增强 → 标准库设计）；消除全部纯进度流水文档（S5/S7/项目结构重组/委托方案，决策已吸收）。
- 修复 33 处文档坏链；新增 [docs/快速上手.md](docs/快速上手.md)；语法手册状态标记核对补齐。

### `.coproj` 零功能元素接线 + console 模板实例化（2026-09-06）
- `<Content CopyToOutput>`：按 glob 复制到输出目录（保留相对路径 / 越界回退文件名），增量命中与全量两路径均幂等执行，未命中告警。
- `<Subsystem>`：native PE 头子系统（`Console` 默认 / `Windows` 无控制台窗口），经 EmitNative 全链透传。
- `<TreatWarningsAsErrors>`：源码 / Content 模式未命中、`[imports]` 未实现、诊断 Warning 级统一升级为错误，构建失败。
- `cocoa new console` `main.co` 升级为有代表性示例（阶乘函数 + 数组 + 循环 + 字符串拼插）；dotnet / native x64 双端冒烟通过。
- 新增 `ContentCopyTests` 4 例、`SubsystemPEEmitTests` 2 例、`TreatWarningsAsErrorsTests` 3 例；文档（项目格式规范 / 编译手册）同步。

## 2026-09-05 ~ 09-06

### ⭐ delegate 真实类型化（6e-M22，M0-M6 七提交全落地）
- delegate 由语法糖升级为**存续运行期真实类型**：IL 真 `MulticastDelegate` 子类 / Evaluator 调用列表对象 / native 委托对象（`[vtable][list]`）。
- 多播 `+` / `-` / `==`：`Delegate.Combine/Remove/Equals` 调用列表语义；快照遍历调用；`null` 引用相等。
- 事件迁移 C# 式 add/remove（后备字段 = 委托类 Combine/Remove + Invoke 触发）；泛型 delegate + `<in T>`/`<out T>` 型变（安全位诊断、参数逆变 + 返回协变）。
- 委托/事件/泛型全后端 e2e 用例 +25；全量 **41871** 绿；`System.Core.coa`/`System.Collections.coa` 重建。

## 2026-08-24 ~ 09-05（阶段 6 收尾延续，择要）

- 8-30：双前端全量拆分 + 双层 IR 落地（Roslyn Y 决议：Cocoa/CSharp 独立节点层 + 共享规范 IR 作 .coa 模块层）。
- 8-26：6e-M23 out/ref 参数完整版（修饰符 + 明确赋值分析 + `Int32.TryParse(s, out v)`）；G7-core 泛型 `.coa` 序列化消费闭环。
- 8-24：6e-M19 对象模型（System.Object 基类 / 全类型成员方法 / System.Type / native vtable + null·is·as）。
- 8-23：6e-M21 数值类型全集（i8..u64 / f32 / f64、字面量后缀、无符号语义、SSE 单精度）。
- 8-22：6e-M17 内部调用与互操作（syscall / import 块 / extern 元数据 / `.coa` v2 容器类序列化）。
- 8-21：6e-M15 双前端拆分（`.co` / `.cs` 按扩展名分派）；6e-M14 标准库（System.Math/String/Array 冒烟 54 断言全绿）。
- 8-18：6d 项目系统（`.coproj` / `.cosln` + 增量缓存 + 单二进制 `cocoa` CLI）。

> 更早阶段 0-5（IR 层 / 运行时 IR 化 / IL 自研 / 输出与项目系统）见 [docs-dev/开发计划.md](docs-dev/开发计划.md)。