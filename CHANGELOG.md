# Changelog

> 状态：✅ 生效（2026-09-06）
> 定位：面向用户的近期变更流水（里程碑粒度）；完整提交历史见 git。
> 相关：[docs/README.md](docs/README.md)、[docs-dev/开发计划.md](docs-dev/开发计划.md)（阶段路线图总计）

---

## 未发布（2026-09-14）

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