# Cocoa.IDE 设计 — 类 Visual Studio 桌面 IDE

> 状态：🔧 设计中（2026-09-08 定稿技术路线与功能矩阵；同日补充 **API 现状核实（§4）** 与 **M1 骨架审计清单（§5.4）**）
> 目标：为 Cocoa 语言构建**类 Visual Studio 的桌面 IDE**——解决方案/项目管理 + 语法着色编辑器 + 实时诊断 + 补全/Hover/F12 + 构建运行 + （M7）解释器调试器，进程内直接复用编译器 `Cocoa.Compiler.Core` 完整编译管线。
> 核心决策：**Avalonia 11 跨平台**；**直接消费既有 public API**（`Compilation.GetSemanticModel`/`SemanticModel`/`Classifier`/`BoundScope`/`Cocoa.Build` 全部已公开，零 `InternalsVisibleTo`，详见 §4）；**调试器基于解释器**（在 `Cocoa.CodeGen.Interpreter` 内新增 public `DebuggerSession`，见 §11）。
> 相关文档：`docs/编译手册.md`（`cocoa` CLI 子命令）、`docs/项目格式规范.md`（`.coproj`/`.cosln`）、`docs-dev/实现目标.md`（编译器架构）
> 最后更新：2026-09-08

---

## 目录

1. [目标与非目标](#1-目标与非目标)
2. [技术选型](#2-技术选型)
3. [总体架构](#3-总体架构)
4. [编译器 API 策略](#4-编译器-api-策略)
5. [功能矩阵与路线图](#5-功能矩阵与路线图)
6. [UI 设计](#6-ui-设计)
7. [语言服务设计](#7-语言服务设计)
8. [项目系统集成](#8-项目系统集成)
9. [构建与运行集成](#9-构建与运行集成)
10. [实时诊断管线](#10-实时诊断管线)
11. [调试器设计（M7）](#11-调试器设计m7)
12. [实施里程碑与验收标准](#12-实施里程碑与验收标准)
13. [风险与开放问题](#13-风险与开放问题)

---

## 1. 目标与非目标

### 1.1 目标

| 维度 | 内容 |
|------|------|
| 形态 | 独立桌面应用（Windows/Linux/macOS），单进程内嵌编译器 |
| 编辑体验 | 多标签编辑器、双方言（`.co` 宽松 / `.cs` 严格 C#）语法着色、折叠、行号、文件内查找 |
| 项目能力 | 打开/创建 `.cosln`/`.coproj`，树形资源管理器，新建项目向导（复用 CLI 模板） |
| 语义服务 | 实时诊断（错误列表 + 波浪线）、补全（Ctrl+Space）、Hover 签名提示、F12 跳转定义 |
| 构建运行 | F6 构建 / F5 运行，输出窗口捕获 `ProjectBuilder` 消息流，增量缓存指示 |
| 调试（M7） | 解释器断点 / 单步 / 步入 / 步出 / 局部变量监视 / 调用栈窗口 |

### 1.2 非目标（当前阶段）

- **LSP 双路线**：不做独立 Language Server 进程（进程内直连更简单；若未来要接 VS Code，可将 §7 语言服务层薄封装成 LSP，接口设计时预留）。
- **原生/托管真调试器**：native 符号发射（PE Debug Directory + COFF/PDB）与 ICorDebug 集成为 P3 研究项（§11.4）。
- **Web 版 / 云端协作**。
- GUI 设计器、扩展商店、测试资源管理器：**列入 P3 远期**（前置依赖链见 §5.2），非"永不做"。

---

## 2. 技术选型

### 2.1 选型结论

| 组件 | 选择 | 版本 | 理由 |
|------|------|------|------|
| UI 框架 | **Avalonia** | 11.x | 跨平台（Win/Linux/macOS）、XAML+MVVM 成熟、自绘渲染观感一致；未来自举（阶段 7）后可随 .NET 迁 Linux |
| 编辑器控件 | **AvaloniaEdit**（Avalonia.AvaloniaEdit） | 11.x | AvalonEdit 移植：高亮 Colorizer/行号/折叠/SearchPanel/自定义边距开箱即用，MIT |
| MVVM | CommunityToolkit.Mvvm | 8.x | 源生成器式 `[ObservableProperty]`/`[RelayCommand]`，样板最少 |
| 主题 | Avalonia.Themes.Fluent + 自定义暗色资源字典 | — | VS 深色风配色（§6.3） |
| 运行时 | .NET 9（跟随 `Directory.Build.props`） | net9.0 | 与 `Cocoa.Compiler.Core` 一致 |

### 2.2 备选对比（定稿依据）

| 方案 | 跨平台 | 编辑器控件 | 工程量 | 结论 |
|------|:---:|------|------|------|
| **Avalonia 11 + AvaloniaEdit** | ✅ | 现成 | 中 | **采用** |
| WPF + AvalonEdit | ❌ 仅 Windows | 最成熟 | 中 | 放弃：无法迁移到 Linux 自举生态 |
| LSP + VS Code | ✅ | 借用宿主 | 小 | 不符合"类 VS"诉求，降级为远期可选输出 |

---

## 3. 总体架构

### 3.1 三层结构

```
┌────────────────────────────────────────────────────────────────┐
│                     Cocoa.IDE（Avalonia 11 应用）               │
│                                                                │
│  Shell 层（Views / ViewModels）                                 │
│  ├─ MainWindow（五区布局 §6.1）                                  │
│  ├─ SolutionExplorerView    ErrorListView    OutputView         │
│  ├─ EditorTabsView（AvaloniaEdit 封装）        StatusBar          │
│                                                                │
│  Services 层                                                    │
│  ├─ WorkspaceService     打开的解决方案/项目/文档集合；文件系统监听 │
│  ├─ DocumentService      文档状态机；300ms 防抖重解析调度           │
│  ├─ NavigationService    打开文件/定位行列；前进后退导航历史        │
│  ├─ DiagnosticService    后台诊断计算与推送                        │
│  ├─ BuildService         F6/F5 单飞执行、输出捕获（M4）            │
│  └─ DebuggerService      断点/单步/变量窗口会话（M7）              │
│                                                                │
│  LanguageServices 层（§7，薄壳——全部依赖 Core 既有 public API）    │
│  ├─ CocoaHighlighting（AvaloniaEdit 着色，M1 用 .xshd / 远期 Classifier）│
│  ├─ CompletionProvider     HoverProvider                        │
│  └─ GoToDefinitionProvider DiagnosticsMapper                    │
└───────────────────────────┬────────────────────────────────────┘
                            │ 进程内项目引用（零 IPC）
┌───────────────────────────▼────────────────────────────────────┐
│              编译器侧（已核实 public，§4 明细）                   │
│                                                                │
│  Cocoa.Compiler.Core                                            │
│   SyntaxTree / Parser / SourceText / TextSpan       ✅ public   │
│   Compilation.GetSemanticModel / SemanticModel      ✅ public   │
│   Symbol 体系（Function/Type/Variable/Parameter…）  ✅ public   │
│   BoundScope / BoundGlobalScope / BoundProgram     ✅ public   │
│   Evaluator（internal）→ 仅供 Compilation.Evaluate 驱动          │
│                                                                │
│  Cocoa.Cli.Repl.Authoring                                       │
│   Classifier / Classification / ClassifiedSpan     ✅ public    │
│   （可选：迁入 Core 去 REPL 依赖，见 §7.1）                        │
│                                                                │
│  Cocoa.Build  【IDE 只需引这一个即带上 Core】                    │
│   CocoaSolutionFile / CocoaProjectFile / Glob      ✅ public    │
│   ProjectBuilder / SolutionBuilder / BuildCache    ✅ public    │
│                                                                │
│  Cocoa.CodeGen.Interpreter                                      │
│   DebuggerSession（M7 新增，public）← 调试唯一挂载点 §11.2        │
└────────────────────────────────────────────────────────────────┘
```

### 3.2 关键数据流（打开解决方案 → 可编辑）

```
MainWindow 启动
  → WorkspaceService.OpenSolution(path)
      → CocoaSolutionFile.Load(path)                 // public
      → 逐项 CocoaProjectFile.Load(p)                // public
      → Glob.Expand(project.SourcePatterns, dir)     // public，源文件清单
  → SolutionTreeViewModel 构建 树(解决方案→项目→文件)
  → 双击文件 → NavigationService.Open(file)
      → DocumentService.GetOrCreate(file)：SourceText.From(text, file) 缓存
      → SyntaxTree.Load(file)                        // 按扩展名自动选方言
      → EditorTabViewModel(text, tree) → 新标签
```

### 3.3 线程模型

| 工作 | 线程 | 说明 |
|------|------|------|
| 全部 UI | UI 线程（Avalonia Dispatcher） | ViewModel 只持有不可变快照 |
| 解析/绑定/求值 | 后台 Task.Run | 输入 `SyntaxTree`/`ImmutableArray` 均不可变，天然安全 |
| 取消 | CancellationToken | 快速连续输入时丢弃过期解析轮次 |
| 结果回传 | Dispatcher.UIThread.Post | 错误列表/波浪线更新封送回 UI 线程 |
| 构建 | 单飞（防重入） | 构建期间禁用 F6，状态栏显示进度 |

---

## 4. 编译器 API 策略

### 4.1 决策：直接消费既有 public API，不新增门面、不扩白名单

> 修订记录（2026-09-08）：早期草案拟在 Core 内新增 `Cocoa.CodeAnalysis.Authoring.SemanticModel` 门面并把 `Classifier` 从 CLI 迁入 Core。**经逐一核实源码，这些能力已经以 public 形态存在**，故全部撤销——IDE 只需 ProjectReference `Cocoa.Build.csproj`（其传递引用即含 `Cocoa.Compiler.Core`），需要 REPL 侧 `Classifier` 时再补 `Cocoa.Cli.Repl.csproj` 引用。

**现状核实表**（IDE 直接消费的 public API）：

| 类型 / 成员 | 程序集 / 程序集命名空间 | 说明 | 复用目标 |
|-------------|------------------------|------|---------|
| `SemanticModel`（abstract） | `Cocoa.Compiler.Core`（`Cocoa.CodeAnalysis`） | `GetTypeInfo` / `GetDeclaredSymbol` / `GetSymbolInfo` / `GetDiagnostics` / `GetOperation` | F12、Hover、补全、诊断 |
| `Compilation.GetSemanticModel(tree)` | 同上 | 每棵树一个语义模型 | 语言服务入口 |
| `Compilation.Create / CreateScript` | 同上 | 编译单元构造 | 诊断/构建共用 |
| `SyntaxTree.Parse / Load / ParseCs` | 同上 | 按文件自动选方言（`.co`/`.cs`） | 打开/重解析 |
| `BoundScope`（public） | 同上 | `TryLookupSymbol` / `TryLookupFunctions` / `GetDeclared*` | F12/补全作用域查询 |
| `BoundGlobalScope` / `BoundProgram` | 同上 | 绑定结果 | M7 断点定位（遍历语句树） |
| Symbol 体系全量 | 同上 | Function/Type/Variable/Parameter/Property… | Hover/补全项 Detail |
| `Classifier` / `Classification` / `ClassifiedSpan` | `Cocoa.Cli.Repl`（`Cocoa.Cli.Repl.Authoring`） | 基于 SyntaxKind 的语义着色 | §7.1 远期着色 |
| `CocoaSolutionFile.Load` / `CocoaProjectFile.Load` / `Glob` | `Cocoa.Build` | 真实格式解析 + glob 展开 | M2 替换 IDE 内手写解析（D1） |
| `ProjectBuilder` / `SolutionBuilder` / `BuildCache` | `Cocoa.Build` | 增量构建 | M4 |
| `Evaluator`（internal） | `Cocoa.CodeGen.Interpreter` | 经 `Compilation.Evaluate` 可达 | M7 需新增 public 入口（§11.2） |

### 4.2 编译器侧需要的新增/改动（极少）

| 项 | 改动 | 归属里程碑 |
|----|------|-----------|
| 调试器入口 | `Cocoa.CodeGen.Interpreter` 内新增 public `DebuggerSession`，对 internal `Evaluator` 加显式帧栈 + 语句边界钩子（§11.2） | M7 |
| 模板（可选） | 抽取 `NewCommand.BuildTemplate` 为 `Cocoa.Build.Projects.CocoaTemplates`（public）供「新建项目向导」程序化调用；**或**向导直接调用 `cocoa new` CLI 子进程，二选一 | M2 |
| Classifier 迁移（可选） | 把 `Classifier` 从 `Cocoa.Cli.Repl` 物理移入 `Cocoa.Compiler.Core`，去除 IDE 对 REPL 程序集的依赖；纯物理移动 + CLI 回归 | M5 前置 |

> 无需项（已核实不存在/不必要）：`InternalsVisibleTo` 扩白名单、Authoring 门面类、`BoundScope`/`BoundGlobalScope` 公开化——均已 public。

### 4.3 长期公开白名单（按需逐个提升）

| 类型/成员 | 用途 | 提升时机 |
|-----------|------|---------|
| `Compilation.GlobalScope` | 高级分析 | 有第二消费者时 |
| `CodProgram/CodSerializer/SystemLibrary` | 对象浏览器枚举库符号 | 对象浏览器动工前 |
| `SyntaxTree.GetParent` | 位置解析性能优化 | ResolveAtPosition 热点化时 |
| `ControlFlowGraph + BasicBlock` 族 | 数据流可视化工具窗 | 不排期 |

---

## 5. 功能矩阵与路线图

### 5.1 主线里程碑（M1–M7）

| 里程碑 | 功能 | 复用点 | 状态 |
|--------|------|--------|:---:|
| **M1 IDE 骨架** | 五区布局主窗口；多标签编辑器（着色/行号/折叠/括号匹配/Ctrl+F）；打开 `.co/.cs/.coproj/.cosln` | `SyntaxTree.Parse` | ✅ 骨架已搭（审计清单见 §5.4） |
| **M1.1 编辑器接线** | EditorView 实际嵌入中央区；VM↔编辑器双向同步（Text/Caret）；标签点击激活+高亮+关闭按钮；状态栏 Ln/Col/Language 联动；B1-B5 修复 | AvaloniaEdit 事件桥接 | 📋 下一优先 |
| **M2 项目系统** | 解决方案树改走 `Cocoa.Build`（`CocoaSolutionFile`/`CocoaProjectFile`/`Glob`），删手写解析（D1）；新建项目向导（`CocoaTemplates` 抽取或 `cocoa new` 子进程）；添加/移除文件 | `Cocoa.Build` | 📋 规划 |
| **M3 实时诊断** | 防抖重解析管线；错误列表（过滤、双击定位，改 ObservableCollection + 全量筛选 D6）；编辑器波浪线 | `SemanticModel.GetDiagnostics` | 📋 规划 |
| **M4 构建运行** | F6 构建项目/解决方案；F5 运行产物；输出窗口；增量指示；清理 | `ProjectBuilder`/`SolutionBuilder`/`BuildCache` | 📋 规划 |
| **M5 语义服务** | Ctrl+Space 补全；Hover 显示签名；F12 跳转定义 | `SemanticModel`、`Compilation.GetSemanticModel`、`BoundScope` | 📋 规划 |
| **M6 打磨** | 暗色/亮色主题；启动页（最近项目）；状态栏；选项页 | — | 📋 规划 |
| **M7 解释器调试器** | 断点/继续/单步/步入/步出；局部变量+监视；调用栈窗口；黄色当前行 | `CodeGen.Interpreter` 新增 public `DebuggerSession`（§11.2） | 📋 规划 |

### 5.2 增强层（P1/P2/P3）

| 优先级 | 功能 | 说明 / 前置依赖 |
|--------|------|----------------|
| **P1** | Peek 定义（Alt+F12）、查找所有引用（Shift+F12）、重命名重构（Ctrl+R,R）、全解决方案查找（Ctrl+Shift+F）、项目属性页（F4）、引用管理对话框、集成 REPL 终端（Ctrl+`） | 各自独立 |
| **P2** | 代码片段、格式化文档（Ctrl+K,D）、快速操作灯泡（Ctrl+.）、导航栏、书签、TODO、Git 集成、Debug/Release 配置切换、预览标签/拆分编辑器/Ctrl+Q/全屏/完整选项页/i18n | Shell 增强 |
| **P3** | GUI 库+工具箱+设计器（前置：Cocoa GUI 控件库）、扩展系统 MEF 类（前置：插件点抽象）、单元测试资源管理器（前置：`cocoa test`）、包管理器（前置：包仓库生态）、性能分析器、native 符号发射研究 | 远期 |

### 5.3 与编译器开发的并行策略

| 阶段 | 触碰编译器？ | 时序 |
|------|:---:|------|
| M1/M1.1 骨架壳（窗口/布局/编辑器接线） | 否 | 可立即开工；高亮暂用 `.xshd` 规则文件，零编译器依赖 |
| M2 项目系统 | 否（`Cocoa.Build` 全部 public）；可选模板抽取为编译器侧单点 | 与编译器并行 |
| M3-M6 | 否 | 全部纯增量新工程 |
| M7 调试器 | 是（仅此一次：`DebuggerSession` + Evaluator 钩子） | 挑编译器里程碑空档单独提交 |

分支隔离：`feature/ide-shell`（纯新增）与编译器分支互不相扰；唯一交叉点即 M7 的 `DebuggerSession`（及可选的 M2 模板抽取），各自独立提交、随时可并。

### 5.4 M1 骨架审计清单（2026-09-08 全量代码检查）

> 已逐一读码核实（含两个 `.xshd` 主题）。B=行为 Bug、D=设计缺陷、Q=打磨项。修复归属：M1.1=编辑器接线期、M2/M3/M4/M7=对应里程碑、P=增强期。

**B 级 — 实际 Bug**

| # | 问题 | 位置 | 修复归属 |
|---|------|------|:---:|
| B1 | 单行注释正则损坏：`<Rule color="Comment">//.*$" />` 末尾多一 `"`，正则永不可匹配 → `.co`/`.cs` 的 `//` 注释永不高亮 | `Cocoa.xshd:19`、`CSharp.xshd:19` | M1.1 |
| B2 | 新开标签即脏：`OnContentChanged` 无条件置 `IsModified=true`，构造读文件赋 `Content` 即触发 | `EditorTabViewModel.cs:28,31` | M1.1 |
| B3 | 目录型项目重复节点：`LoadFolderInto` 加子节点后又调 `LoadFolder` 加到 `RootNodes` | `SolutionTreeViewModel.cs:122-127` | M1.1（M2 换 Cocoa.Build 后整段删除） |
| B4 | 编辑器未接线：中央区只有空态 TextBlock，`EditorView` 从未实例化；编辑不回写 VM `Content` → 保存写旧文本 | `MainWindow.axaml:155-162` | M1.1 |
| B5 | 标签无点击激活/无选中高亮（`ItemsControl` 非选择型）；仅 Ctrl+W 能关当前 | `MainWindow.axaml:136-149` | M1.1 |

**D 级 — 设计缺陷**

| # | 问题 | 位置 | 修复归属 |
|---|------|------|:---:|
| D1 | 手工解析 `.cosln/.coproj` + 自写 glob，重复实现 `Cocoa.Build` 已有 public API（`CocoaSolutionFile.Load`/`CocoaProjectFile.Load`/`Glob.Expand`），且不处理引号/转义/多段模式 | `SolutionTreeViewModel.cs` 全文 | M2 |
| D2 | VM↔编辑器无事件桥：`EditorView` 未暴露 `TextChanged`/`Caret.PositionChanged` → 状态栏 Ln/Col、Language、脏状态无数据源 | `EditorView.axaml.cs` | M1.1 |
| D3 | 状态栏断链：`Language` 恒 `""`、`SolutionName` 无 XAML 绑定、`Encoding` 硬编码 | `StatusBarViewModel.cs`、`MainWindow.axaml` | M1.1 |
| D4 | `CloseTab` 选 `LastOrDefault`，应选相邻标签；无每标签关闭按钮/中键关闭 | `EditorTabsViewModel.cs:31` | M1.1 |
| D5 | `OutputViewModel.AppendLines` 绕过 `_buffer`、逻辑与 `AppendLine` 重复 | `OutputViewModel.cs:27-30` | M4（接输出前） |
| D6 | `FilteredItems` 惰性 LINQ：`ShowErrors/ShowWarnings` 切换不触发 UI 刷新 | `ErrorListViewModel.cs:44` | M3（接错误列表前） |
| D7 | UI 线程同步阻塞 File IO；无 UTF8/BOM 编码策略；构造同步读大文件 | `MainViewModel.cs:74-89`、`EditorTabViewModel.cs:28` | M1.1 |
| D8 | 无未保存提示：`Closing` TODO 空、关标签不确认 → 数据丢失 | `MainWindow.axaml.cs:22` | M1.1 |

**Q 级 — 打磨项**

| # | 问题 | 修复归属 |
|---|------|:---:|
| Q1 | `Avalonia.Diagnostics` 应仅 Debug 引用；`ApplicationIcon` 空占位；csproj 无 ProjectReference（M1 有意；M2 起引 `Cocoa.Build.csproj` 一个即带上 Core） | M2 |
| Q2 | 菜单大面积空壳（编辑/视图/生成/调试多无 Command）；Ctrl+F 未接 AvaloniaEdit SearchPanel | P |
| Q3 | xshd 关键词手工维护易漂移：Cocoa 组 `var` 重复列、`print` 非关键字误列；C# 缺 `record/init/required/file/scoped/nint/nuint`；CSharp verbatim 串规则 `[^\"])` 误排除 `\`（高亮提前终止） | M1.1 / 长期切 Classifier |
| Q4 | 工具栏 emoji 跨平台字体风险；`LoadFile` 仅按 `.cs` 判方言，`.coproj/.cosln/.txt` 亦被当 Cocoa 着色；非源码文件应只读 | M1.1 |

---

## 6. UI 设计

### 6.1 五区布局线框

```
┌────────────────────────────────────────────────────────────────────┐
│ 文件(F)  编辑(E)  视图(V)  项目(P)  生成(B)  调试(D)  工具(T)  帮助(H) │
├────────────────────────────────────────────────────────────────────┤
│ ▶运行  🔨生成  │ 配置:[Debug ▾] 平台:[x64 ▾] │        🔍查找        │
├──────────────┬─────────────────────────────────────────────────────┤
│ 解决方案资源   │ ● main.co    ○ Util.co                              │
│ 管理器        │ ┌─────────────────────────────────────────────────┐ │
│              │ │  1  function main() {                           │ │
│ ▾ Demo.cosln │ │  2      let msg = "Hello, Cocoa!"               │ │
│   ▾ MyApp    │ │  3      print(msg)                              │ │
│     main.co  │ │  4  }                                           │ │
│     MyApp…   │ │                                                 │ │
├──────────────┴─────────────────────────────────────────────────────┤
│ 错误列表 │ 输出 │ 查找结果(P1) │ 监视(M7) │ 调用栈(M7)                │
│  ✗ 0 错误  ⚠ 1 警告                                                │
│  main.co(3,13): warning: 未使用的变量 'msg'                         │
├────────────────────────────────────────────────────────────────────┤
│ 就绪 │ Ln 3, Col 13 │ 插入 │ UTF-8 │ C# 严格方言 │ Demo.cosln        │
└────────────────────────────────────────────────────────────────────┘
```

### 6.2 快捷键映射

| 快捷键 | 功能 | 里程碑 |
|--------|------|:---:|
| Ctrl+S / Ctrl+Shift+S | 保存 / 全部保存 | M1 |
| Ctrl+F / Ctrl+H | 文件内查找 / 替换 | M1 |
| Ctrl+Space | 补全 | M5 |
| F12 / Alt+F12 | 转到定义 / Peek 定义 | M5 / P1 |
| Shift+F12 | 查找所有引用 | P1 |
| Ctrl+R, R | 重命名 | P1 |
| Ctrl+Shift+F | 全解决方案查找 | P1 |
| F6 / Ctrl+Shift+B | 生成项目 / 生成解决方案 | M4 |
| F5 / Ctrl+F5 | 调试 / 运行不调试 | M4 |
| F9 / F10 / F11 / Shift+F11 | 断点 / 步过 / 步入 / 步出 | M7 |
| Ctrl+` | 集成 REPL 终端 | P1 |
| Shift+Alt+Enter | 全屏 | P2 |

### 6.3 暗色主题基色（VS Dark 风）

| 元素 | 色值参考 |
|------|---------|
| 编辑器背景 / 当前行 | #1E1E1E / #282828 |
| 关键字 / 类型名 | #569CD6 / #4EC9B0 |
| 字符串 / 数字 / 注释 | #D69D85 / #B5CEA8 / #57A64A |
| 标识符 / 标点 | #DCDCDC / #DCDCDC |
| 波浪线（错/警） | 红 / 绿下划线 |
| 面板背景 / 选中 | #252526 / #094771 |
| 断点圆点 / 当前行箭头(M7) | #E51400 / #FFE066 |

---

## 7. 语言服务设计

### 7.1 语法着色

M1/M1.1 阶段使用 `.xshd` 规则文件（关键词表枚举），不依赖编译器管线；**注意当前两文件单行注释规则有 `//.*$` 多余引号 Bug（B1），词表存在漂移（Q3），先修再用**。M5 前置切换为语义着色：`Classifier.Classify(SyntaxTree, TextSpan)` —— `Classifier`/`Classification`/`ClassifiedSpan` 已在 `Cocoa.Cli.Repl.Authoring` 以 public 形态存在，IDE 补引 `Cocoa.Cli.Repl.csproj` 即可直接用；若不想依赖 REPL 程序集，可选把 `Classifier` 物理迁入 `Cocoa.Compiler.Core`（§4.2）。

分类枚举现状：Text / Keyword / Identifier / Number / String / Comment / Punctuation / Operator；语义着色需扩展 **Type** 区分（泛型/类名独立色）。

### 7.2 补全算法

触发：`.`、标识符字符输入、Ctrl+Space。

```
GetCompletions(tree, position):
  1. 上下文检测:
     a. 成员访问 "expr." → 局部/全局变量→TypeSymbol→Methods/Properties/Fields
     b. 否则 → 作用域声明集 + GetAllSymbols() + 上下文关键字
  2. 前缀过滤 → 排序（成员 > 局部 > 全局 > 类型 > 关键字）
  3. 每项 Detail = symbol.ToString()
```

限制：MVP 成员推断覆盖「变量.」与「类型名.」两类；任意表达式精确类型流分析留待 Bound 层 API 开放后增强。

### 7.3 Hover

光标悬停 500ms → `ResolveAtPosition` → 命中则 tooltip 显示 `Symbol.ToString()`；该行若有诊断，追加显示消息。

### 7.4 位置解析与 F12

```
ResolveAtPosition(tree, position):
  1. 定位最深 token：自 Root 递归取 FullSpan.Contains(position) 的子节点直至 token
  2. token 为标识符 → name → 父链成员访问判定 → LookupSymbol/LookupFunctions
  3. 返回 symbol → F12: 所在 SyntaxTree.FileName + Span → NavigationService.OpenAt
```

---

## 8. 项目系统集成

| 能力 | 实现 |
|------|------|
| 解决方案树 | `CocoaSolutionFile.Load` → `CocoaProjectFile.Load` → `Glob.Expand` 懒展开（M2 起，删除 §5.4-D1 手写解析） |
| 新建项目向导 | M2：二选一 —— `CocoaTemplates` 抽取（`Cocoa.Build.Projects`，CLI 同步受益）或调用 `cocoa new` CLI 子进程；写盘后刷新树 |
| 添加/移除文件 | 文本级改写 `.coproj` `[sources]` 节 |
| 文件监听 | `FileSystemWatcher`；外部改动 → DocumentService 缓冲失效 |

---

## 9. 构建与运行集成

```
F6 → ProjectBuilder.Build / SolutionBuilder.Build
     → outputWriter = OutputViewModel.TextWriter 适配器
     → 输出行正则 ^(.*?\((\d+),(\d+)\)): (error|warning): (.*)$ → 错误跳转
F5 → Process.Start(产物 exe)
```

已知边界：`ProjectBuildResult` 仅 Success/UpToDate 二态；P1 考虑 `BuildReport` 结构化返回。

---

## 10. 实时诊断管线

```
键入 → Debounce(300ms) → Task.Run:
  tree = SyntaxTree.Parse(SourceText.From(newText, file), dialect)
  model.UpdateTree(tree)
  diags = model.GetDiagnostics(tree)
→ Dispatcher 回传:
  ErrorListViewModel.Merge(file, diags)
  EditorTab.SetSquiggles(diags → TextSpan)
```

>2MB 文件进入只读模式；绑定耗时超 200ms 自动降频为 1s 防抖。

---

## 11. 调试器设计（M7）

### 11.1 为什么是解释器调试器

只有解释器能在托管代码里被完全控制（暂停/观测/单步），且**与产物后端无关**。参考系 minsk 同样采用解释器调试路线。

### 11.2 编译器侧改造：`Cocoa.CodeGen.Interpreter` 内新增 public `DebuggerSession`

> `Evaluator` 是 internal，无法从 IDE 直接触碰；但 `DebuggerSession` 与 `Evaluator` 同程序集即可见 internal，故**无需扩 `InternalsVisibleTo`**。

```csharp
// Cocoa.CodeGen.Interpreter 内新增（public 门面）
public sealed class DebuggerSession
{
    public static DebuggerSession Create(Compilation compilation);   // 用 Compilation.Evaluate 同路径实例化 Evaluator
    public void SetBreakpoint(string filePath, int line);            // 断点表（文件,行）
    public void RemoveBreakpoint(string filePath, int line);
    public DebugExecutionState State { get; }                        // Running / Paused / Completed
    public IReadOnlyList<StackFrame> CallStack { get; }              // 调用栈窗口
    public IReadOnlyDictionary<VariableSymbol, object>? CurrentLocals { get; }
    public object? ReturnValue { get; }
    public void Continue();  public void StepOver();
    public void StepInto();  public void StepOut();
    public void Stop();
    public event Action<DebugPauseReason>? Paused;                   // 断点命中 / 单步完成
}

// 配套类型（public）：DebugExecutionState / DebugPauseReason / StackFrame(函数+文件+行+Locals)

// Evaluator 内部增量（internal，同一程序集可见）
internal sealed class CallFrame {
    public FunctionSymbol Function;
    public Dictionary<VariableSymbol, object> Locals;
    public int StatementIndex;
}
private readonly Stack<CallFrame> _frames;
internal IReadOnlyList<CallFrame> Frames => _frames;
internal Action<CallFrame, BoundStatement>? StatementBoundaryHook;
//   主循环每条语句前回调；Hook 内查断点表命中 → Pause(ManualResetEventSlim 等待)
```

断点解析：遍历 `BoundProgram.Functions` 的语句树收集 Span → `SourceText.GetLineIndex` → (file,line) 集合。
单步语义：步过=同帧下一序列点；步入=更深帧首序列点；步出=帧弹出后下一序列点。

### 11.3 IDE 侧组件

| 组件 | 内容 |
|------|------|
| DebuggerService | 会话生命周期；断点表管理 |
| 编辑器集成 | 左边距断点圆点（红）/ 当前行黄底箭头；F9 切换 |
| 局部变量/监视窗口 | 当前帧 `Locals` + `_globals` 快照 |
| 调用栈窗口 | `Frames` 列表，双击切帧联动编辑器 |

### 11.4 远期：真调试器路线（P3 研究）

| 路线 | 前置 |
|------|------|
| native 调试 | PE Debug Directory + COFF/CodeView PDB 发射 |
| IL 托管调试 | IL 后端发射 Portable PDF（产物可被 VS/Rider 调试） |

---

## 12. 实施里程碑与验收标准

| 里程碑 | DoD（验收） |
|--------|------------|
| M1 | IDE 启动 <2s；打开文件着色正确；行号/查找可用 |
| M1.1 | 双击树/打开文件即在编辑器可编辑；标签点击激活、脏标记正确（B2 修复）、`//` 注释着色（B1 修复）；Ctrl+S 写入即内容；状态栏 Ln/Col/Language 实时；关脏标签弹保存确认（D8）；B3-B5/D2-D4/D7 关闭 |
| M2 | 树由 `Cocoa.Build` 解析驱动（D1 关闭）；新建项目与 `cocoa new` 一致（二选一方案） |
| M3 | ≤0.5s 出红波浪线；双击定位准确（含 D6 修复） |
| M4 | F6 构建 samples.cosln 成功；F5 运行 HelloWorld（含 D5） |
| M5 | 补全三类候选正确；Hover 签名正确；F12 跨文件准确（着色切 Classifier） |
| M6 | 主题切换即时生效；最近项目持久化 |
| M7 | 断点命中；四种步进行为正确；局部变量正确（`DebuggerSession`） |

依赖关系：M1 → M1.1 → {M2, M3} → M4 → M5；M7 独立线。

## 13. 风险与开放问题

| # | 风险 | 缓解 |
|---|------|------|
| R1 | NuGet 还原需联网 | 首次还原后锁定版本 |
| R2 | `libs/System.Core.cod` 分发 | csproj 增加 copy target |
| R3 | 整项目重绑 O(全部源码) | 单树替换 + 防抖 + 取消令牌 |
| R4 | AvaloniaEdit 大文件性能 | >2MB 只读保护 |
| R5 | 编译器侧改动（M7 `DebuggerSession`、可选模板/Classifier 迁移）破坏 CLI | 同程序集内新增、纯物理移动 + 回归测试，独立提交 |
| R6 | 跨平台字体/快捷键差异 | 键位表抽象层 |
| R7 | 手写解析 `.cosln/.coproj` 与 `Cocoa.Build` 语义漂移（D1） | M2 起一律走 `CocoaSolutionFile`/`CocoaProjectFile`/`Glob`，删除自研 |
| R8 | VM↔编辑器状态失同步（B4/D2）→ 保存旧文本/脏标记错乱 | M1.1 事件桥（TextChanged/Caret）双向回写 + Dispatcher 封送 |

**开放问题**：
1. Docking 方案终选（Dock.Avalonia vs 自研）；
2. 设置存储格式（`%LOCALAPPDATA%\Cocoa\IDE\*.json`）；
3. 多根工作区（无 `.cosln` 直接开文件夹）；
4. `BuildReport` 结构化构建诊断返回；
5. 「新建项目向导」实现方案终选：`CocoaTemplates` 抽取入库 vs `cocoa new` CLI 子进程（M2）。
