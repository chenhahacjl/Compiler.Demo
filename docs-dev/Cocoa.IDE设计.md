# Cocoa.IDE 设计 — 类 Visual Studio 桌面 IDE

> 状态：🔧 实施中（M1~M5 已落地；2026-09-12 完成**第二轮审计（§5.5，A1–A10）**并修订路线图：新增 **M2b VS2022 两步式新建项目向导（§8.1）**、**M5b 工程上下文语义（§7.5）**、**M6c VS 风格解决方案资源管理器（§6.4）**；实施顺序见 §12.2）
> 目标：为 Cocoa 语言构建**类 Visual Studio 的桌面 IDE**——解决方案/项目管理 + 语法着色编辑器 + 实时诊断 + 补全/Hover/F12 + 构建运行 + （M7）解释器调试器，进程内直接复用编译器 `Cocoa.Compiler.Core` 完整编译管线。
> 核心决策：**Avalonia 11 跨平台**；**直接消费既有 public API**（`Compilation.GetSemanticModel`/`SemanticModel`/`Classifier`/`BoundScope`/`Cocoa.Build` 全部已公开，零 `InternalsVisibleTo`，详见 §4）；**调试器基于解释器**（在 `Cocoa.CodeGen.Interpreter` 内新增 public `DebuggerSession`，见 §11）。
> 相关文档：`docs/编译手册.md`（`cocoa` CLI 子命令）、`docs/项目格式规范.md`（`.coproj`/`.cosln`）、`docs-dev/实现目标.md`（编译器架构）
> 最后更新：2026-09-12

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
| **M1 IDE 骨架** | 五区布局主窗口；多标签编辑器（着色/行号/折叠/括号匹配/Ctrl+F）；打开 `.co/.cs/.coproj/.cosln` | `SyntaxTree.Parse` | ✅ 已落地（审计清单见 §5.4） |
| **M1.1 编辑器接线** | EditorView 实际嵌入中央区；VM↔编辑器双向同步（Text/Caret）；标签点击激活+高亮+关闭按钮；状态栏 Ln/Col/Language 联动；B1-B5 修复 | AvaloniaEdit 事件桥接 | ✅ 已落地（B1-B5/D2-D4/D7 关闭） |
| **M2 项目系统** | 解决方案树改走 `Cocoa.Build`（`CocoaSolutionFile`/`CocoaProjectFile`/`Glob`），删手写解析（D1） | `Cocoa.Build` | ✅ 已落地（D1 关闭） |
| **M2b 新建项目向导** | **VS2022 两步式向导**（模板选择 → 名称/位置/解决方案名/目标框架）；统一生成 `.cosln` + 项目子目录；空白解决方案模板；位置自动创建 | `Cocoa.Build` + 模板 XML | 📋 规划（§8.1） |
| **M3 实时诊断** | 防抖重解析管线；错误列表（过滤、双击定位，改 ObservableCollection + 全量筛选 D6）；编辑器波浪线 | `SemanticModel.GetDiagnostics` | ✅ 已落地（D6 关闭） |
| **M4 构建运行** | F6 构建项目/解决方案；F5 运行产物；输出窗口；增量指示；清理 | `ProjectBuilder`/`SolutionBuilder`/`BuildCache` | ✅ 已落地（D5 关闭；A7 待修） |
| **M5 语义服务** | Ctrl+Space 补全；Hover 显示签名；F12 跳转定义 | `SemanticModel`、`Compilation.GetSemanticModel`、`BoundScope` | ✅ 基础落地（补全 99 项、F12/Hover 主函数可用） |
| **M5b 工程上下文语义** | 补全/诊断/Hover/F12 接入工程源文件集与 `References`（修跨文件误报、`Console.` 补全）；按内容缓存编译 | `Compilation.Create(references, trees)` | 📋 规划（§7.5） |
| **M6 打磨** | 暗色/亮色主题；启动页（最近项目）；状态栏；选项页 | — | 📋 规划 |
| **M6a UI 接线** | 空壳菜单（退出/视图/项目/生成清理/关于）、状态栏解决方案名、Ctrl+F 查找、错误过滤 UI、输出自动滚动 | AvaloniaEdit SearchPanel | 📋 规划 |
| **M6b F12 定位与打磨** | 声明名字 token 精确定位；空补全不弹；着色扩展名判定；大文件只读/编码 | `Language.GetDeclarationNameLocation` | 📋 规划 |
| **M6c 解决方案资源管理器** | **VS 风格**：矢量图标、嵌套文件夹、引用/依赖项节点、工具栏（刷新/折叠全部/同步活动文档/显示所有文件/属性）、按种类右键、引用可编辑 | `CocoaProjectFile` + IDE `ProjectFileService` | 📋 规划（§6.4） |
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

### 5.5 第二轮审计清单（2026-09-12，M1~M5 落地后全量读码）

> 修复归属：A=M6a 前的正确性修复；C=M5b/M6b。所有位置均经源码核对。

**A 级 — 明确缺陷**

| # | 问题 | 位置 | 修复归属 |
|---|------|------|:---:|
| A1 | 实时诊断结果被别的文件丢弃：`_generation` 为**全局**计数，文件 A 编辑后、回调到达前若编辑 B → `generation != _generation` 使 A 结果永久丢弃（取消令牌本已按文件） | `DiagnosticService.cs:52,78` | M5b |
| A2 | 诊断仅**单文件**编译 `Compilation.Create(tree)`，未带工程源文件集与 `References` → 调用别处函数/`Console.*` 误报红波浪线（构建却成功） | `DiagnosticService.cs:65` | M5b |
| A3 | F5 可运行非 exe 项目：当前项目非 exe 时只打印 error 却仍 `return project` 继续执行 | `MainViewModel.cs:364-369` | M6a |
| A4 | 关闭标签不提示保存直接丢改动：`CloseTab`/`Close` 无脏检查（主窗口 `Closing` 有确认，标签 ✕ 没有，D8 只完成一半） | `EditorTabsViewModel.cs:39-69` | M6a |
| A5 | 浮窗/编辑器事件订阅泄漏：订阅静态 `EditorTabsRegistry` 事件后从不退订（窗口无法 GC，且会向已脱离 Pane 发导航） | `FloatingEditorWindow.axaml.cs:23`、`EditorPane.axaml.cs:43,54,55` | M6a |
| A6 | 新建项目默认目录必然报错：硬校验 `Directory.Exists(dir)`，而默认 `我的文档\CocoaProjects` 通常不存在 | `NewProjectDialog.cs:139` | M2b |
| A7 | 运行程序不捕获输出、无法停止；且运行又触发一次 `BuildFinished`（与构建重复计数） | `BuildService.cs:56-75,130` | M6a |
| A8 | `文件>打开文件` 打开后不出诊断：`OpenFileAsync` 直接 `EditorTabs.OpenFile`，绕过会 `Reanalyze` 的 `OpenFile(path)` | `MainViewModel.cs:199` | M6a |
| A9 | 主窗口关闭不保存浮窗脏标签：只遍历主窗口标签，应用退出时浮窗未保存内容可能丢失 | `MainWindow.axaml.cs:227-253` | M6a |
| A10 | 在任意窗口已打开的文件，若该窗口 `ActiveTab==null` 则找不到（多余条件） | `MainViewModel.cs:91` | M6a |

**B 级 — 语言服务/性能**

| # | 问题 | 位置 | 修复归属 |
|---|------|------|:---:|
| B6 | Hover 每次鼠标移动都重解析目录内全部 `.co/.cs`（`SemanticModelHost.Update`）→ 卡顿；应按内容缓存、编辑时失效 | `EditorPane.axaml.cs:74-93` | M5b |
| B7 | F12 列定位用「行内首个字母」启发式，缩进/字符串会跳错列 | `GoToDefinitionProvider.cs:62-74` | M6b |
| B8 | 无候选仍弹出空补全框 | `EditorPane.axaml.cs:100-115` | M6b |
| B9 | 补全仅在 Ctrl+Space 触发，`.` 后不自动弹（违反 §7.2） | `EditorPane.axaml.cs:58-70` | M6a |

**C 级 — 打磨**

| # | 问题 | 位置 | 修复归属 |
|---|------|------|:---:|
| C1 | `NewProjectService` 类注释仍写 `Templates/templates.xml`（已拆分为每目录 `template.xml`） | `NewProjectService.cs:7` | M2b |
| C2 | `BuildService._isBuilding` 死代码（`_gate` 已串行，检查永不命中） | `BuildService.cs:81-85` | M6b |
| C3 | 构建带位置的诊断一律计为 error，warning 无位置区分 | `BuildService.cs:99-102` | M6b |
| C4 | 非 `.cs` 一律按 Cocoa 着色（`.coproj/.cosln/.txt` 也着色） | `EditorView.axaml.cs:122-130` | M6b |
| C5 | 标签拖动 12px 阈值无视觉反馈；点 ✕ 可能误触拖动 | `EditorPane.axaml.cs:264-291` | M6b |

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

### 6.4 VS 风格解决方案资源管理器（M6c）

> 目标：对齐 Visual Studio 解决方案资源管理器的结构与交互（2026-09-12 定稿）。

**树结构**

```
解决方案 'Demo' (2 个项目)          ← 计数（VS 格式）
├─ 📦 MyApp                        ← 项目（.coproj）
│  ├─ 🔗 引用                       ← 新增节点，列 .coproj <Reference>
│  │   ├─ System.Core
│  │   └─ ../Libs/MyLib.coa
│  ├─ 📁 Sub                        ← 嵌套文件夹（按磁盘目录，非平铺）
│  │   └─ 📄 Util.co
│  └─ 📄 main.co
└─ 📦 Lib
   ├─ 🔗 引用
   └─ 📄 Class1.co
```

- **替代**现状：`LoadProjectInto` 将源文件**平铺**为项目直接子节点、无图标、无引用节点。
- 源文件按 `Path.GetRelativePath(project.Directory, file)` 的目录层级建 `Folder` 节点。

**节点图标（矢量，无外部资源）**

- `NodeKind` 扩展 `Reference`；图标用内置矢量 `Geometry`/`Path`（解决方案/项目/文件夹/引用/`.co`/`.cs`/`.coproj`/`.cosln`），随主题变色，规避 emoji 跨平台字体风险（回应 §5.4-Q4）。

**工具栏（VS 同款常用项）**

| 按钮 | 行为 |
|------|------|
| 刷新 | `SolutionTree.Refresh()` |
| 折叠全部 | `CollapseAll()` |
| 与活动文档同步 | 活动标签切换 → 选中并展开对应节点（`FullPath → 节点` 索引） |
| 显示所有文件 | 开时枚举项目目录，展示未被 `Glob.Expand` 覆盖的项（`IsPhantom` 灰显） |
| 属性 | 打开属性窗口并填充选中节点 |

**交互**

- 双击文件夹展开/折叠；双击文件打开（现状文件夹双击无响应）。
- 右键菜单按 `NodeKind` 区分：解决方案（生成/新建项目…）、项目（生成/添加文件/添加引用…/属性）、引用（移除引用/属性）、文件（打开/打开所在文件夹/从项目中排除/复制路径/属性）。
- 选中 → 属性窗口（已接线）+ VS 风格高亮。

**模型改动**

- `TreeNodeViewModel` 增 `Icon`、`Glyph`、`IsExpanded`、`Caption`、`IsPhantom`；`NodeKind` 增 `Reference`。
- `SolutionTreeViewModel` 增 `SyncToFile(path)`、`CollapseAll()`、`ShowAllFiles` 开关、路径索引。

**引用编辑（写回 `.coproj`）**

- 新增 IDE `Services\ProjectFileService.cs`：`AddReference`/`RemoveReference`（`System.Xml.Linq`，`<ItemGroup>` 内 `<Reference Include="…"/>`，去重、绝对转相对、UTF-8 无 BOM），逻辑同 CLI `ReferenceCommand.cs`。
- **不**给 IDE 引入 `Cocoa.Cli` 依赖，**不**改动并行开发中的编译仓库；后续若需单点复用，可将该逻辑提取为 `Cocoa.Build` 的 public `ProjectFileEditor`（CLI 同步受益）。

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

### 7.5 工程上下文语义与缓存（M5b）

> 背景：现状诊断 `Compilation.Create(tree)`（单树）、语义宿主只加载**同目录** `.co/.cs`，均不含工程 `References` 与 stdlib → 跨文件误报、`Console.` 补全/类型解析失败（§5.5-A2）、Hover 每次全量重解析（§5.5-B6）。

**设计**

| 项 | 方案 |
|----|------|
| `ProjectContext` | 承载「源文件集（`CocoaProjectFile.SourcePatterns` + `Glob.Expand`）+ `References` + 语言」，由 `SolutionTreeViewModel.CurrentProject/CurrentSolution` 提供 |
| 编译构造 | `Compilation.Create(references, params SyntaxTree[])`（已 public，见 §4）多树编译；诊断的**语法**部分只取当前树，**语义**部分取整个工程 |
| 缓存 | 按（文件内容哈希 + 工程引用集）缓存 `Compilation`；仅当某文件内容变化时失效该项并重建 |
| 诊断 | `DiagnosticService` 接收 `ProjectContext`，替代单文件 `Compilation.Create(tree)` |
| 语义服务 | `SemanticModelHost.Update` 接收已算好的 `trees + references`，Hover/F12/补全不再各自重解析目录 |
| 预期收益 | 修跨文件诊断误报、`Console.` 成员补全、跨文件 F12/Hover、Hover 卡顿 |

> 说明：多树编译成本高于单文件，故以缓存 + 防抖（§10）控制；超限文件仍走 §10 的只读/降频保护。

---

## 8. 项目系统集成

| 能力 | 实现 |
|------|------|
| 解决方案树 | `CocoaSolutionFile.Load` → `CocoaProjectFile.Load` → `Glob.Expand` 懒展开（M2 已落地）；M6c 重做为 VS 风格（§6.4） |
| 新建项目向导 | M2 已落地第一版（磁盘模板 XML）；M2b 重做为 VS2022 两步式（§8.1） |
| 添加/移除文件 | 文本级改写 `.coproj`（M6a）；引用增删见 §6.4 |
| 文件监听 | `FileSystemWatcher`；外部改动 → DocumentService 缓冲失效（P2） |

### 8.1 VS2022 两步式新建项目向导（M2b）

> 替代现有 520px 单页表单（`NewProjectDialog.cs`）。**步骤 1** 选模板，**步骤 2** 配置；对齐 Visual Studio 2022「创建新项目」。

**步骤 1 · 创建新项目**

- 顶部搜索框（按模板名称/描述过滤）
- 中央模板卡片列表：图标字形 + `Label` + `Description`，可选中
- 右侧选中项详情（图标、名称、描述）
- 底部 `下一步` / `取消`

**步骤 2 · 配置新项目**

- 项目名称、位置（`浏览…`）、解决方案名称
- ☐ 将解决方案和项目放在同一目录中
- 目标框架下拉：`net48`（默认）/`net9.0`/`net8.0`/`net6.0`/`netcoreapp3.1`
- `上一步` / `创建`

**目录布局（对齐 VS）**

| 情况 | 产物 |
|------|------|
| 默认 | `<位置>\<解决方案名>\<解决方案名>.cosln` + `<位置>\<解决方案名>\<项目名>\<项目名>.coproj` |
| 勾选「同一目录」 | `<位置>\<解决方案名>\<解决方案名>.cosln` + `<位置>\<解决方案名>\<项目名>.coproj` |
| 空白解决方案 | 仅 `<位置>\<解决方案名>\<解决方案名>.cosln`（无项目） |

- 位置不存在时**自动创建**（修 §5.5-A6）。
- `{{Tfm}}` 由界面真正传入（现状恒为 `net48`）。
- **每个模板统一生成 `.cosln`**；原 `Solution` 模板改为「空白解决方案」（VS Blank Solution 语义）。

**服务端改动**

- `NewProjectService` 新增 `CreateWithSolution(template, projectName, solutionName, location, sameDirectory, tfm)` → 生成 `.cosln`（`<Project Include="相对路径" />`）+ 复用 `CreateProject` 生成项目；返回 `(SolutionPath, ProjectPath, CreatedFiles)`。
- `MainViewModel.NewProjectAsync` 改为加载返回的 `.cosln`。
- 清理过期类注释（§5.5-C1）。

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
| M2 | 树由 `Cocoa.Build` 解析驱动（D1 关闭）；新建项目与 `cocoa new` 一致 |
| M2b | 两步式向导可用；默认布局 `<解决方案名>\<解决方案名>.cosln` + `<项目名>\<项目名>.coproj`；勾选同目录生效；空白解决方案模板；目标框架下拉写入 `{{Tfm}}`；位置不存在自动创建（A6） |
| M3 | ≤0.5s 出红波浪线；双击定位准确（含 D6 修复） |
| M4 | F6 构建 samples.cosln 成功；F5 运行 HelloWorld（含 D5；A7 运行输出/停止） |
| M5 | 补全三类候选正确；Hover 签名正确；F12 跨文件准确 |
| M5b | 多文件 + `References` 编译；跨文件调用不再误报；`Console.` 成员可补全；Hover 不重解析全目录；A1/A2/B6 关闭 |
| M6a | 空壳菜单接线（退出/视图/项目/生成清理/关于）；状态栏显示解决方案名；Ctrl+F 查找；错误过滤 UI；输出自动滚动；A3/A4/A5/A7/A8/A9/A10/B9 关闭 |
| M6b | F12 精确到声明名字列；空补全不弹；非源码不着 Cocoa 色；大文件只读；B7/B8/C2-C5 关闭 |
| M6c | VS 风格树（矢量图标/嵌套文件夹/引用节点/解决方案计数）；工具栏（刷新/折叠全部/同步活动文档/显示所有文件/属性）；按种类右键；引用可增删并写回 `.coproj` |
| M6 | 主题切换即时生效；最近项目持久化 |
| M7 | 断点命中；四种步进行为正确；局部变量正确（`DebuggerSession`） |

依赖关系：M1 → M1.1 → {M2, M2b, M3} → M4 → M5 → M5b → {M6a, M6b, M6c} → M6；M7 独立线。

### 12.2 实施顺序与提交分组（2026-09-12 定稿）

> 约定：每组一次构建冒烟（`dotnet build src\Cocoa.IDE\Cocoa.IDE.slnx`）后单独提交。

| 序 | Phase | 内容 | 提交信息 |
|:--:|-------|------|---------|
| 1 | 正确性 | A1/A3/A7/A8 诊断与运行修复 | `F8：修复实时诊断跨文件丢弃/运行非可执行项目/打开文件不出诊断` |
| 2 | 生命周期 | A4/A5/A9/A10 关标签/浮窗事件 | `F9：修复关标签丢改动/浮窗事件泄漏/关闭未保存` |
| 3 | M2b | VS2022 两步式向导 + 解决方案布局 + 空白解决方案（含 A6/C1） | `M2b：VS2022 两步式新建项目向导 + 解决方案布局，空白解决方案模板` |
| 4 | M5b | 工程上下文语义 + 缓存（A2 + B6 + C 主项） | `M5b：语义/诊断接入工程上下文与引用(修跨文件误报与 Console. 补全)` |
| 5 | M6c | VS 风格解决方案资源管理器 | `M6c：VS 风格解决方案资源管理器(矢量图标/嵌套文件夹/引用节点/工具栏/同步活动文档)` |
| 6 | M6a | UI 接线（菜单/状态栏/查找/过滤） | `M6a：补齐菜单/状态栏/查找/过滤等 UI 接线` |
| 7 | M6b | 精确 F12 定位与打磨项 | `M6b：精确 F12 定位与打磨项` |
| 8 | M7 | 解释器调试器（触碰编译器，避开并行里程碑） | `M7：解释器调试器` |

> 顺序约束：M5b 为 M6a 的 `.` 自动补全提供工程上下文，不可颠倒；M7 唯一改动编译器（`Cocoa.CodeGen.Interpreter`），置于最后。

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
5. ~~「新建项目向导」实现方案终选~~ ✅ 已定：磁盘模板 XML（每模板一 `template.xml`）+ IDE 侧 `NewProjectService.CreateWithSolution`（M2b，§8.1）；
6. 引用写回逻辑单点化：M6c 先在 IDE 内实现 `ProjectFileService`，是否提取为 `Cocoa.Build` 的 public `ProjectFileEditor`（CLI 同步受益）留待评估（§6.4）。
