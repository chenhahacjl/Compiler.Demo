# Cocoa.IDE 设计 — 类 Visual Studio 桌面 IDE

> 状态：🔧 设计中（2026-09-08 定稿技术路线与功能矩阵，实施排期见 §12）
> 目标：为 Cocoa 语言构建**类 Visual Studio 的桌面 IDE**——解决方案/项目管理 + 语法着色编辑器 + 实时诊断 + 补全/Hover/F12 + 构建运行 + （M7）解释器调试器，进程内直接复用 `Cocoa.Core` 完整编译管线。
> 核心决策：**Avalonia 11 跨平台**；**public 门面优先**（新增 `Cocoa.CodeAnalysis.Authoring.SemanticModel`，零 `InternalsVisibleTo`）；**调试器基于解释器**（复用 REPL 的 `Evaluator` 执行路径，后端无关）。
> 相关文档：`src/Cocoa.IDE/README.md`（路线定稿记录）、`docs/编译手册.md`（`cocoa` CLI 子命令）、`docs/项目格式规范.md`（`.coproj`/`.cosln`）、`docs-dev/实现目标.md`（编译器架构）
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
| 运行时 | .NET 9（跟随 `Directory.Build.props`） | net9.0 | 与 Cocoa.Core 一致 |

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
│                     Cocoa.IDE（Avalonia 应用）                  │
│                                                                │
│  Shell 层（Views / ViewModels）                                 │
│  ├─ MainWindow（五区布局 §6.1）                                  │
│  ├─ SolutionExplorerView    ErrorListView    OutputView         │
│  ├─ EditorTabsView（AvaloniaEdit 封装）        StatusBar          │
│                                                                │
│  Services 层                                                    │
│  ├─ WorkspaceService     打开的解决方案/项目/文档集合；文件系统监听 │
│  ├─ DocumentService      文档状态机；300ms 防抖重解析调度           │
│  └─ NavigationService    打开文件/定位行列；前进后退导航历史        │
│                                                                │
│  LanguageServices 层（§7，薄壳——全部依赖 Core 门面）              │
│  ├─ CocoaHighlighting（AvaloniaEdit IHighlightingColorizer）    │
│  ├─ CompletionProvider     HoverProvider                        │
│  └─ GoToDefinitionProvider DiagnosticsMapper                    │
└───────────────────────────┬────────────────────────────────────┘
                            │ 进程内项目引用（零 IPC）
┌───────────────────────────▼────────────────────────────────────┐
│                       Cocoa.Core（net9.0）                      │
│  语法层   SyntaxTree / Parser / Lexer            public         │
│  语义层   Binder / BoundScope / BoundProgram     internal       │
│          └→ SemanticModel 门面（public，§4.1）                   │
│  符号层   Symbol 体系（FunctionSymbol/ClassTypeSymbol…） public  │
│  文本层   SourceText / TextSpan / TextLocation   public         │
│  分类层   Classifier（M0 从 CLI 迁入，public）                    │
│  项目层   CocoaProjectFile / SolutionBuilder / BuildCache public │
│  模板层   CocoaTemplates（M0 从 NewCommand 抽出，public）         │
│  执行层   Evaluator（internal）← M7 调试器唯一挂载点               │
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

### 4.1 决策：public 门面，不用 InternalsVisibleTo

现状：`Binder`/`BoundGlobalScope`/`BoundProgram`/`BoundScope`/`CodProgram`/`SystemLibrary` 均 internal，
`InternalsVisibleTo` 白名单仅 `Cocoa.Tests` 与 `cocoa`。IDE 若加入白名单，等于把整个 Bound 层暴露给 UI 程序集，
API 面失控且阻碍后续重构。

**方案**：在 `Cocoa.Core` 内新增 public 门面类型（同程序集天然可见 internal，外部只见稳定 API）：

```csharp
namespace Cocoa.CodeAnalysis.Authoring;   // M0 新增，目录 CodeAnalysis\Authoring\

/// <summary>IDE 语言服务门面：惰性绑定 + 缓存，线程安全性由实现保证</summary>
public sealed class SemanticModel
{
    public static SemanticModel Create(params SyntaxTree[] syntaxTrees);
    public static SemanticModel Create(string[]? references, params SyntaxTree[] syntaxTrees);

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public void UpdateTree(SyntaxTree tree);

    public ImmutableArray<Diagnostic> GetDiagnostics();
    public ImmutableArray<Diagnostic> GetDiagnostics(SyntaxTree tree);

    public Symbol? LookupSymbol(string name);
    public ImmutableArray<FunctionSymbol> LookupFunctions(string name);
    public IEnumerable<Symbol> GetAllSymbols();

    public Symbol? ResolveAtPosition(SyntaxTree tree, int position);
}
```

### 4.2 长期公开白名单（按需逐个提升）

| 类型/成员 | 用途 | 提升时机 |
|-----------|------|---------|
| `Compilation.GlobalScope` | 高级分析 | 有第二消费者时 |
| `BoundScope.TryLookup*/GetDeclared*` | LSP 化时避免门面重复转发 | LSP 路线启动时 |
| `CodProgram/CodSerializer/SystemLibrary` | 对象浏览器枚举库符号 | 对象浏览器动工前 |
| `SyntaxTree.GetParent` | 位置解析性能优化 | ResolveAtPosition 热点化时 |
| `ControlFlowGraph + BasicBlock` 族 | 数据流可视化工具窗 | 不排期 |

---

## 5. 功能矩阵与路线图

### 5.1 主线里程碑（M0–M7）

| 里程碑 | 功能 | 复用点 | 状态 |
|--------|------|--------|:---:|
| **M0 编译器侧准备** | `SemanticModel` 门面；`Classifier` 迁入 Core；`NewCommand.BuildTemplate` 模板抽取为 `Projects\CocoaTemplates.cs`；xUnit 测试 | 既有全部 public API | 📋 待实现 |
| **M1 IDE 骨架** | 五区布局主窗口；多标签编辑器（着色/行号/折叠/括号匹配/Ctrl+F）；打开 `.co/.cs/.coproj/.cosln` | `SyntaxTree.Parse`、Classifier | ✅ 骨架已搭 |
| **M2 项目系统** | 解决方案树（懒加载展开 glob）；新建项目向导；添加/移除文件 | `CocoaSolutionFile`、`Glob`、`CocoaTemplates` | 📋 规划 |
| **M3 实时诊断** | 防抖重解析管线；错误列表（过滤、双击定位）；编辑器波浪线 | `SemanticModel.GetDiagnostics` | 📋 规划 |
| **M4 构建运行** | F6 构建项目/解决方案；F5 运行产物；输出窗口；增量指示；清理 | `ProjectBuilder`/`SolutionBuilder`/`BuildCache` | 📋 规划 |
| **M5 语义服务** | Ctrl+Space 补全；Hover 显示签名；F12 跳转定义 | `SemanticModel` 查询族、`Symbol.ToString()` | 📋 规划 |
| **M6 打磨** | 暗色/亮色主题；启动页（最近项目）；状态栏；选项页 | — | 📋 规划 |
| **M7 解释器调试器** | 断点/继续/单步/步入/步出；局部变量+监视；调用栈窗口；黄色当前行 | `Evaluator` 加显式帧+语句边界钩子（§11） | 📋 规划 |

### 5.2 增强层（P1/P2/P3）

| 优先级 | 功能 | 说明 / 前置依赖 |
|--------|------|----------------|
| **P1** | Peek 定义（Alt+F12）、查找所有引用（Shift+F12）、重命名重构（Ctrl+R,R）、全解决方案查找（Ctrl+Shift+F）、项目属性页（F4）、引用管理对话框、集成 REPL 终端（Ctrl+`） | 各自独立 |
| **P2** | 代码片段、格式化文档（Ctrl+K,D）、快速操作灯泡（Ctrl+.）、导航栏、书签、TODO、Git 集成、Debug/Release 配置切换、预览标签/拆分编辑器/Ctrl+Q/全屏/完整选项页/i18n | Shell 增强 |
| **P3** | GUI 库+工具箱+设计器（前置：Cocoa GUI 控件库）、扩展系统 MEF 类（前置：插件点抽象）、单元测试资源管理器（前置：`cocoa test`）、包管理器（前置：包仓库生态）、性能分析器、native 符号发射研究 | 远期 |

### 5.3 与编译器开发的并行策略

| 阶段 | 触碰编译器？ | 时序 |
|------|:---:|------|
| M1 骨架壳（窗口/布局/标签编辑器） | 否 | 可立即开工；高亮暂用 `.xshd` 规则文件，零编译器依赖 |
| M0 三件套（SemanticModel / Classifier 迁移 / 模板抽取） | 是（仅此一次） | 待泛型 M20 收官后单独一个提交落库 |
| M2-M7 | 否 | 全部纯增量新工程 |

分支隔离：`feature/ide-shell`（纯新增）与编译器分支互不相扰；唯一交叉点即 M0 单独提交，挑编译器里程碑空档合并。

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

M1 阶段直接使用 `.xshd` 规则文件（关键词表枚举），不依赖编译器管线。M0 后切换为 `Classifier.Classify(SyntaxTree, TextSpan)` 语义着色。

分类枚举扩展：Text / Keyword / Identifier / Number / String / Comment / **Type** / **Punctuation** / **Operator**

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
| 解决方案树 | `CocoaSolutionFile.Load` → `CocoaProjectFile.Load` → `Glob.Expand` 懒展开 |
| 新建项目向导 | M0 后调 `CocoaTemplates.BuildTemplate`；写盘后刷新树 |
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

### 11.2 Evaluator 改造（编译器侧）

```csharp
internal sealed class CallFrame {
    public FunctionSymbol Function;
    public Dictionary<VariableSymbol, object> Locals;
    public int StatementIndex;
}

// Evaluator 增量
private readonly Stack<CallFrame> _frames;
internal IReadOnlyList<CallFrame> Frames => _frames;
internal Action<CallFrame, BoundStatement>? StatementBoundaryHook;
//   主循环每条语句前回调；Hook 内查断点表命中 → Pause(ManualResetEventSlim 等待)
```

断点解析：遍历 BoundProgram.Functions 的语句树收集 Span → 源码行号 → (file,line) 集合。
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
| M0 | `dotnet test` 全绿（SemanticModelTests ≥15 用例）；CLI 回归不变 |
| M1 | IDE 启动 <2s；打开文件着色正确；行号/查找可用 |
| M2 | 四模板创建与 `cocoa new` 一致 |
| M3 | ≤0.5s 出红波浪线；双击定位准确 |
| M4 | F6 构建 samples.cosln 成功；F5 运行 HelloWorld |
| M5 | 补全三类候选正确；Hover 签名正确；F12 跨文件准确 |
| M6 | 主题切换即时生效；最近项目持久化 |
| M7 | 断点命中；四种步进行为正确；局部变量正确 |

依赖关系：M0 → M1 → {M2, M3} → M4 → M5；M7 独立线。

## 13. 风险与开放问题

| # | 风险 | 缓解 |
|---|------|------|
| R1 | NuGet 还原需联网 | 首次还原后锁定版本 |
| R2 | `libs/System.Core.cod` 分发 | csproj 增加 copy target |
| R3 | 整项目重绑 O(全部源码) | 单树替换 + 防抖 + 取消令牌 |
| R4 | AvaloniaEdit 大文件性能 | >2MB 只读保护 |
| R5 | 模板/Classifier 迁移破坏 CLI | 纯物理移动 + 回归测试 |
| R6 | 跨平台字体/快捷键差异 | 键位表抽象层 |

**开放问题**：
1. Docking 方案终选（Dock.Avalonia vs 自研）；
2. 设置存储格式（`%LOCALAPPDATA%\Cocoa\IDE\*.json`）；
3. 多根工作区（无 `.cosln` 直接开文件夹）；
4. `BuildReport` 结构化构建诊断返回。
