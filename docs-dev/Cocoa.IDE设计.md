# Cocoa.IDE 设计 — 类 Visual Studio 桌面 IDE

> 状态：🔧 设计中（2026-08-25 定稿技术路线与功能矩阵，实施排期见 §12）
> 目标：为 Cocoa 语言构建**类 Visual Studio 的桌面 IDE**——解决方案/项目管理 + 语法着色编辑器 + 实时诊断 + 补全/Hover/F12 + 构建运行 + （M7）解释器调试器，进程内直接复用 `Cocoa.Core` 完整编译管线。
> 核心决策：**Avalonia 11 跨平台**；**public 门面优先**（新增 `Cocoa.CodeAnalysis.Authoring.SemanticModel`，零 `InternalsVisibleTo`）；**调试器基于解释器**（复用 REPL 的 `Evaluator` 执行路径，后端无关）。
> 相关文档：`src/Cocoa.IDE/README.md`（路线定稿记录）、`docs/编译手册.md`（`cocoa` CLI 子命令）、`docs/项目格式规范.md`（`.coproj`/`.cosln`）、`docs-dev/实现目标.md`（编译器架构）
> 最后更新：2026-08-25

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
    /// <summary>替换单棵树（其余树缓存复用），下次查询惰性重绑 —— 对应编辑场景</summary>
    public void UpdateTree(SyntaxTree tree);

    // ---- 诊断（global scope + program 合并去重）----
    public ImmutableArray<Diagnostic> GetDiagnostics();
    public ImmutableArray<Diagnostic> GetDiagnostics(SyntaxTree tree);   // 按来源树过滤

    // ---- 符号查询（补全/Hover 数据源）----
    public Symbol? LookupSymbol(string name);                            // 变量/类/枚举/函数首选
    public ImmutableArray<FunctionSymbol> LookupFunctions(string name);  // 重载全集
    public IEnumerable<Symbol> GetAllSymbols();                          // 含 stdlib 注入后的可见符号

    // ---- 位置解析（Hover/F12 基础，§7.4）----
    public Symbol? ResolveAtPosition(SyntaxTree tree, int position);
}
```

要点：
- 内部走既有惰性缓存模式（仿 `Compilation.GlobalScope` 的 Interlocked 惰性初始化）；
- stdlib 符号经构造时 `SystemLibrary.Load()` 注入路径自然进入作用域（与 CLI 同机制），IDE 无需感知 `.cod`；
- REPL 的 `Classifier.Classify`（现位于 `Cocoa.Compiler\Repl\Authoring\`，命名空间已是 `Cocoa.CodeAnalysis.Authoring`）
  **M0 物理迁入 Core** 并公开，CLI 改为委托调用——IDE 引用 exe 不可行，必须迁移。

### 4.2 长期公开白名单（按需逐个提升，不一次性开放）

| 类型/成员 | 用途 | 提升时机 |
|-----------|------|---------|
| `Compilation.GlobalScope`（或 `GetSemanticModel()` 直通） | 高级分析 | 有第二消费者时 |
| `BoundScope.TryLookup*` / `GetDeclared*` 查询族 | LSP 化时避免门面重复转发 | LSP 路线启动时 |
| `CodProgram` / `CodSerializer` / `SystemLibrary` | 对象浏览器枚举库符号（P2） | 对象浏览器动工前 |
| `SyntaxTree.GetParent` | 位置解析性能优化（父子索引） | ResolveAtPosition 热点化时 |
| `ControlFlowGraph` + `BasicBlock` 族 | 数据流可视化工具窗（远期构想） | 不排期 |

保持 internal 不动：`Lexer`/`ParserCore` 具体类、`DiagnosticBag`、`Evaluator` 内部细节（M7 仅加钩子，见 §11.2）。

---

## 5. 功能矩阵与路线图

### 5.1 主线里程碑（M0–M7）

| 里程碑 | 功能 | 复用点 | 状态 |
|--------|------|--------|:---:|
| **M0 编译器侧准备** | `SemanticModel` 门面；`Classifier` 迁入 Core；`NewCommand.BuildTemplate` 模板抽取为 `Projects\CocoaTemplates.cs`（public static，CLI 委托调用）；xUnit 测试 | 既有全部 public API | 📋 待实现 |
| **M1 IDE 骨架** | 五区布局主窗口；多标签编辑器（着色/行号/折叠/括号匹配/Ctrl+F）；打开 `.co/.cs/.coproj/.cosln` | `SyntaxTree.Parse`、Classifier | 📋 待实现 |
| **M2 项目系统** | 解决方案树（懒加载展开 glob）；新建项目向导（console/library/csharp/solution）；添加/移除文件（文本级改写 `[sources]` 节） | `CocoaSolutionFile`、`Glob`、`CocoaTemplates` | 📋 规划 |
| **M3 实时诊断** | 防抖重解析管线；错误列表（错误/警告分组过滤、双击定位）；编辑器红色波浪线 | `SemanticModel.GetDiagnostics` | 📋 规划 |
| **M4 构建运行** | F6 构建项目/解决方案；F5 运行产物（Process.Start）；输出窗口；"up to date" 增量指示；清理 | `ProjectBuilder`/`SolutionBuilder`/`BuildCache` | 📋 规划 |
| **M5 语义服务** | Ctrl+Space 补全（作用域+成员访问）；Hover 显示签名；F12 跳转定义（跨文件） | `SemanticModel` 查询族、`Symbol.ToString()` | 📋 规划 |
| **M6 打磨** | 暗色/亮色主题切换；启动页（最近项目）；状态栏信息；基础选项页（字体/主题）；最近文件菜单 | — | 📋 规划 |
| **M7 解释器调试器** | 断点/继续/单步/步入/步出；局部变量+监视；调用栈窗口；黄色当前行 | `Evaluator` 加显式帧+语句边界钩子（§11） | 📋 规划 |

### 5.2 增强层（P1/P2/P3）

| 优先级 | 功能 | 说明 / 前置依赖 |
|--------|------|----------------|
| **P1** | Peek 定义（Alt+F12 内嵌浮窗） | 编辑器弹出层复用 F12 结果 |
| **P1** | 查找所有引用（Shift+F12）+ 引用结果窗口 | 遍历项目内全部语法树匹配标识符 Span |
| **P1** | 重命名重构（Ctrl+R,R） | 声明 + 全部引用处文本改写 |
| **P1** | 全解决方案查找（Ctrl+Shift+F）+ 结果窗口 | glob 遍历 + 正则 |
| **P1** | 项目属性页（F4） | 表格化编辑 `.coproj` 字段（output/platform/entry/dotnetRuntime…） |
| **P1** | 引用管理对话框 | `.cod` / .NET dll / native DLL 三形态；逻辑对齐 `cocoa add reference` |
| **P1** | 集成终端（Ctrl+`）嵌入 REPL | 直接托管 `cocoa -i` 同款交互（REPL 已有行编辑器与着色） |
| **P2** | 代码片段（Tab 展开 ~15 个内置模板） | `for`/`if`/`class`/`function`… |
| **P2** | 格式化文档（Ctrl+K,D） | 按语法树重排的自研 formatter |
| **P2** | 快速操作灯泡（Ctrl+.） | 诊断 → 修复建议映射表 |
| **P2** | 导航栏（类型/成员下拉）、书签、TODO 任务列表 | 各自独立小功能 |
| **P2** | Git 集成（变更徽标/提交/历史） | LibGit2Sharp 或 git CLI 包装 |
| **P2** | Debug/Release 配置切换 | `.coproj debug=` 目前仅为指纹参数，需补真实语义（如断言/优化开关） |
| **P2** | 预览标签/拆分编辑器/Ctrl+Q 全局搜索/全屏/完整选项页/中文 i18n | Shell 增强 |
| **P3** | **GUI 库 + 工具箱 + 设计器** | 前置链：Cocoa 标准库窗体/控件层 → 事件模型 → 序列化格式 → 设计画布；随语言生态立项 |
| **P3** | **扩展系统（MEF 类）** | 前置链：Shell 插件点抽象（命令/工具窗口/语言服务注入协议）→ 清单格式 → 隔离加载 |
| **P3** | **单元测试资源管理器** | 前置链：`cocoa test` 子命令 + 测试发现约定（如 `#[test]` 或命名约定）→ 测试适配器 |
| **P3** | 包管理器（NuGet 类） | 前置：Cocoa 包仓库生态 |
| **P3** | 性能分析器 / native 符号发射研究 / IL Portable PDB 发射 | 见 §11.4 |

明确不做：Live Share 协作、AI 助手、Web 版。

---

## 6. UI 设计

### 6.1 五区布局线框

```
┌────────────────────────────────────────────────────────────────────┐
│ 文件(F)  编辑(E)  视图(V)  项目(P)  生成(B)  调试(D)  工具(T)  帮助(H) │ ← 菜单栏
├────────────────────────────────────────────────────────────────────┤
│ ▶运行  🔨生成  │ 配置:[Debug ▾] 平台:[x64 ▾] │        🔍查找        │ ← 工具栏
├──────────────┬─────────────────────────────────────────────────────┤
│ 解决方案资源   │ ● main.co    ○ Util.co                              │ ← 标签条(●=未保存)
│ 管理器        │ ┌─────────────────────────────────────────────────┐ │
│              │ │  1  function main() {                           │ │
│ ▾ Demo.cosln │ │  2      let msg = "Hello, Cocoa!"               │ │
│   ▾ MyApp    │ │  3      print(msg)                              │ │ ← 编辑器
│     main.co  │ │  4  }                                           │ │   (行号/折叠/
│     MyApp…   │ │                                                 │ │    波浪线/断点边距预留)
│   ▾ Lib      │ │                                                 │ │
├──────────────┴─────────────────────────────────────────────────────┤
│ 错误列表 │ 输出 │ 查找结果(P1) │ 监视(M7) │ 调用栈(M7)                │ ← 底部工具窗标签
│  ✗ 0 错误  ⚠ 1 警告                                                │
│  main.co(3,13): warning: 未使用的变量 'msg'                         │
├────────────────────────────────────────────────────────────────────┤
│ 就绪 │ Ln 3, Col 13 │ 插入 │ UTF-8 │ C# 严格方言 │ Demo.cosln        │ ← 状态栏
└────────────────────────────────────────────────────────────────────┘
```

- MVP 固定泊靠布局（Grid 行列 + 底部 TabControl + 左侧 TreeView）；真·可拖拽 Docking 列为 P2（评估 Dock.Avalonia）。
- 工具窗口可隐藏（视图菜单勾选），记忆布局到 `%LOCALAPPDATA%\Cocoa\IDE\layout.json`。

### 6.2 快捷键映射（对齐 VS 默认方案）

| 快捷键 | 功能 | 里程碑 |
|--------|------|:---:|
| Ctrl+S / Ctrl+Shift+S | 保存 / 全部保存 | M1/M2 |
| Ctrl+F / Ctrl+H | 文件内查找 / 替换（AvaloniaEdit SearchPanel） | M1 |
| Ctrl+Space | 补全 | M5 |
| F12 / Alt+F12 | 转到定义 / Peek 定义 | M5 / P1 |
| Shift+F12 | 查找所有引用 | P1 |
| Ctrl+R, R | 重命名 | P1 |
| Ctrl+F / Ctrl+Shift+F | 文件内 / 全解决方案查找 | M1 / P1 |
| F6 / Ctrl+Shift+B | 生成项目 / 生成解决方案 | M4 |
| F5 / Ctrl+F5 | 运行（M7 前=不调试直接跑）/ 运行不调试 | M4 |
| Shift+F5 | 停止 | M4 |
| F9 / F10 / F11 / Shift+F11 | 断点 / 步过 / 步入 / 步出 | M7 |
| Ctrl+K,C / Ctrl+K,U | 注释 / 取消注释 | P2 |
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

MVP 直接移植 REPL 的 `Classifier.Classify(SyntaxTree, TextSpan)`（纯函数、Span+分类输出、与渲染解耦），
并扩展分类枚举：

```csharp
// 现状（REPL）：Text / Keyword / Identifier / Number / String / Comment
// IDE 扩展：+ Type（内置类型符号名）/ Punctuation / Operator / Preprocessor(#meta)
```

接入方式：实现 AvaloniaEdit `IHighlightingColorizer`，按可视区域调 `Classify(tree, visibleSpan)`；
文档变更即换绑新 `SyntaxTree`（解析 <1ms 量级，无需异步）。

### 7.2 补全算法

触发：`.`、标识符字符输入、Ctrl+Space。

```
GetCompletions(tree, position):
  1. 上下文检测（光标处最小包含节点沿父链回溯）:
     a. 成员访问 "expr." → 解析 expr:
        - 局部/全局变量 → SemanticModel.LookupSymbol(name) → 其 TypeSymbol
        - 类型名/枚举名 → LookupSymbol(name)
        - ClassTypeSymbol → Methods/Properties/Fields/Events（含继承链 GetMethod 等）
        - EnumTypeSymbol → 成员常量
     b. 否则（裸上下文）:
        - 作用域声明集 + GetAllSymbols() + 上下文关键字（按所在语法位置过滤，如语句首补 function/if/let…）
  2. 前缀过滤（大小写敏感遵循方言）→ 排序（成员 > 局部 > 全局 > 类型 > 关键字）
  3. 每项 Detail = symbol.ToString()   // Hover 同源文本："function add(a: int, b: int): int"
```

限制（诚实标注）：MVP 成员推断覆盖「变量.」与「类型名.」两类；任意表达式（如 `f().`）的精确类型流分析留待
Bound 层 API 开放后增强——降级策略：无推断时给空列表，不给错误建议。

### 7.3 Hover

光标悬停 500ms → `ResolveAtPosition` → 命中则 tooltip 显示 `Symbol.ToString()`；
该行若有诊断，追加显示消息（VS 行为一致）。

### 7.4 位置解析与 F12（`ResolveAtPosition`）

```
ResolveAtPosition(tree, position):
  1. 定位最深 token：自 Root 递归取 FullSpan.Contains(position) 的子节点直至 token
     （基础设施：节点遍历用现有 GetChildren()；必要时启用 SyntaxTree.GetParent 父子索引提速）
  2. token 为标识符 → name = token.Text：
     a. 父链含成员访问 → 先解析接收者（同 §7.2.a）再在类型成员中查找
     b. 否则 LookupSymbol(name) ?? LookupFunctions(name)[0]
     c. 类方法体内 → 先查 ContainingClass 成员
  3. 命中 FunctionSymbol/ClassTypeSymbol/… → 返回符号
F12 导航：symbol.Declaration?.Syntax?.Span 或 FunctionSymbol.Syntax.Span
  → 所在 SyntaxTree.Text.FileName → NavigationService.OpenAt(file, span.Start)
```

跨文件跳转要求项目级 `SemanticModel`（全部源树一次建型），单文件标签页降级为本文件查找。

---

## 8. 项目系统集成

| 能力 | 实现 |
|------|------|
| 解决方案树 | `CocoaSolutionFile.Load` → 逐 `CocoaProjectFile.Load` → `Glob.Expand(SourcePatterns)` 懒展开（首次点击项目节点才扫盘） |
| 新建项目向导 | M0 后调 `CocoaTemplates.BuildTemplate("console"/"library"/"csharp"/"solution", name)`；写盘后刷新树。**模板单一事实源在 Core，CLI `new` 同步受益** |
| 添加/移除文件 | 文本级改写 `.coproj` `[sources]` 节（保留注释与其余节原样；移除=删行，添加=节尾插行）→ 重载项目节点 |
| 文件监听 | `FileSystemWatcher` 监听项目目录；外部改动 → 对应 DocumentService 缓冲失效提示重载 |
| 未保存状态 | 标签 ● 标记；关闭确认；构建前自动保存询问（VS 行为） |

`.cocoa` 增量缓存目录、`.coproj.user` 覆盖均由 Core 侧既有逻辑处理，IDE 零额外实现。

---

## 9. 构建与运行集成

```
F6 → BuildService.BuildAsync(选中项目或解决方案)
      → ProjectBuildOptions { CacheRoot = <sln锚>/.cocoa, … }   // 可注入，复用增量
      → SolutionBuilder.Build(soln, options, outputWriter)      // 拓扑序已含环检测
        或 ProjectBuilder.Build(proj, options, outputWriter)
      → outputWriter = OutputViewModel 的 TextWriter 适配器（行缓冲，Dispatcher 回传）
      → 结果行正则匹配 ^(.*?\((\d+),(\d+)\)): (error|warning): (.*)$  → 可跳转错误项
F5 → 无调试运行：Process.Start(产物 exe)；工作目录=输出目录；Stop 按钮 Kill 进程组
      → M7 后切换为调试器启动（解释器会话）
```

已知边界：`ProjectBuildResult` 仅 Success/UpToDate 二态，无结构化诊断集合——错误跳转靠输出文本正则
（CLI 格式稳定 `file(line,col): error/warning: msg`）。P1 考虑给 Core 增加 `BuildReport` 结构化返回。

---

## 10. 实时诊断管线

```
键入 → EditorTabViewModel.TextChanged(newText)
     → DocumentService.Debounce(300ms, key=file)          // 快速输入只保留最后一拍
     → T1 = Task.Run:
         tree = SyntaxTree.Parse(SourceText.From(newText, file), dialect)   // 语法级，毫秒级
         model.UpdateTree(tree)                             // 其余树缓存复用，惰性重绑
         diags = model.GetDiagnostics(tree)                 // 本树相关诊断
     → Dispatcher 回传:
         ErrorListViewModel.Merge(file, diags)
         EditorTab.SetSquiggles(diags → TextSpan 区段)
```

- 诊断合并口径：`BoundGlobalScope.Diagnostics ∪ BoundProgram.Diagnostics`，按 (Location, Message) 去重；
- 性能护栏：>2MB 文件进入只读模式（大文件保护）；绑定耗时超阈值（暂定 200ms）自动降频为 1s 防抖并在状态栏提示；
- 错误列表列：描述 / 文件 / 行 / 列 / 项目 / 严重级别，支持错误/警告/消息过滤器与双击跳转。

---

## 11. 调试器设计（M7）

### 11.1 为什么是解释器调试器

编译器已有三条执行路径：**Evaluator 解释器**（REPL 在用）、IL 发射、native 发射。
只有解释器能在托管代码里被我们完全控制（暂停/观测/单步），且**与产物后端无关**（调试的是语言语义而非某个后端）。
参考系 minsk 同样采用解释器调试路线。

### 11.2 Evaluator 改造（编译器侧，有界工作量）

现状：`Evaluator`（internal，~1400 行）以 `_locals` 栈 + `_globals` 字典 + `_thisStack` + 闭包环境栈驱动，
调用栈隐式存在于 C# 递归中；已有 `BoundSequencePointStatement` 承载源码映射（IL 序列点在用）。

改造四件套：

```csharp
// ① 显式调用帧（观测面，不改变执行语义）
internal sealed class CallFrame {
    public FunctionSymbol Function;
    public Dictionary<VariableSymbol, object> Locals;
    public int StatementIndex;
}

// ② Evaluator 增量（约 +100 行）
private readonly Stack<CallFrame> _frames;
internal IReadOnlyList<CallFrame> Frames => _frames;          // 调用栈窗口数据源
internal Action<CallFrame, BoundStatement>? StatementBoundaryHook;
//   EvaluateStatement 主循环每条语句执行前回调（含 BoundSequencePointStatement 展开）

// ③ 断点解析：遍历 BoundProgram.Functions 的语句树，
//    收集 statement.Syntax?.Span → SourceText 行号 → (file,line) 集合
internal IReadOnlyCollection<SourceSpan> CollectSequencePoints(FunctionSymbol f);

// ④ 暂停协议：Hook 内查断点表命中 → DebuggerService.Pause(frame)（ManualResetEventSlim 等待，
//    UI 线程刷新局部变量/调用栈；Continue/SetNext 置位放行）
```

单步语义：步过=暂停于同帧或更浅的下一序列点；步入=任意更深帧的首序列点；步出=帧弹出后的下一序列点。

### 11.3 IDE 侧组件

| 组件 | 内容 |
|------|------|
| DebuggerService | 会话生命周期（启动=以当前工作区建 Compilation+Evaluator；停止=取消令牌）；断点表 `(file,line)` 管理 |
| 编辑器集成 | 左边距断点圆点（红）/ 当前行黄底箭头；F9 切换 |
| 局部变量/监视窗口 | 当前帧 `Locals` + `_globals` 快照；值渲染 `ToString()` + 类型名；数组/List 摘要（长度+前 N 项） |
| 调用栈窗口 | `Frames` 自栈顶列出 `functionName @ file(line)`，双击切帧并联动编辑器定位 |
| 即时窗口（P1） | 复用 REPL `Compilation.CreateScript` 链共享变量字典方向探索，详细设计 M7 时定 |

### 11.4 远期：真调试器路线（P3 研究）

| 路线 | 前置工作 | 说明 |
|------|---------|------|
| native 调试 | PE Debug Directory + COFF/CodeView PDB 发射 | 产物可进 WinDbg/GDB；自家 IDE 内嵌引擎工程量极大 |
| IL 托管调试 | IL 后端发射 Portable PDB | **务实折中**：产物先做到能被 VS/Rider/dotnet-dump 调试；自家 IDE 内嵌（ICorDebug/dbgshim）缓行 |

---

## 12. 实施里程碑与验收标准

| 里程碑 | DoD（验收） |
|--------|------------|
| M0 | `dotnet test src\Cocoa.Cs\Cocoa.Tests` 全绿（新增 SemanticModelTests：诊断/补全符号/位置解析 ≥15 用例；模板 round-trip 用例）；CLI 回归 `cocoa new console X && cocoa build` 与 `samples.cosln` 构建不变 |
| M1 | IDE 启动 <2s；打开 samples 任一 `.co/.cs` 着色正确（对照 REPL 配色）；折叠/行号/查找可用；崩溃率 0（冒烟脚本 20 操作序列） |
| M2 | 向导创建 console/library/csharp/solution 四模板与 `cocoa new` 产物一致；添加/移除文件后 `.coproj` 手工 diff 仅 `[sources]` 节变化 |
| M3 | 故意输错 → ≤0.5s 出红波浪线与错误列表项；双击定位准确；修复后消失 |
| M4 | IDE 内 F6 构建 `samples.cosln` 成功（18 项目拓扑序）；F5 运行 HelloWorld 弹出控制台；二次构建显示 up to date |
| M5 | Ctrl+Space 对变量./类型名./裸上下文三类给出正确候选；Hover 显示签名；F12 跨文件跳转准确（含 stdlib 函数） |
| M6 | 主题切换即时生效；最近项目持久化；重启恢复上次布局 |
| M7 | HelloWorld 上设断点命中；四种步进行为正确；局部变量/调用栈数值正确；停止后进程干净退出 |

依赖关系：M0 → M1 → {M2, M3} → M4 → M5（M3/M5 共享 SemanticModel，可并行）；M7 独立线，可与 P1 并行。

## 13. 风险与开放问题

| # | 风险 | 缓解 |
|---|------|------|
| R1 | NuGet 还原需联网（Avalonia/AvaloniaEdit/CommunityToolkit） | 首次还原后锁定版本进版本库外缓存；离线机器预置包 |
| R2 | `libs/System.Core.cod` 分发不到新 IDE bin（现有分发面向 Compiler/Tests） | csproj 增加与 Compiler 相同的 copy target；M1 冒烟必查 stdlib 补全可用 |
| R3 | 整项目重绑 O(全部源码)，大项目卡顿 | 单树替换缓存（UpdateTree 只换一棵）+ 防抖 + 取消令牌；超阈值降频（§10）；长期：增量 Binder 是编译器侧课题 |
| R4 | AvaloniaEdit 大文件/极端行性能 | >2MB 只读保护；长行软折行关闭 |
| R5 | 模板/Classifier 迁移破坏 CLI 行为 | 迁移为纯物理移动+委托；`NewCommandTests` + REPL 冒烟把关（M0 DoD） |
| R6 | 跨平台字体/快捷键差异（macOS Cmd） | 键位表抽象层；Linux/macOS 作为 best-effort，Windows 为主验证平台 |

**开放问题**（后续设计输入）：
1. Docking 方案终选（Dock.Avalonia vs 自研）——P2 动工前定；
2. 设置存储格式（倾向 `%LOCALAPPDATA%\Cocoa\IDE\*.json`，与 REPL submissions 目录同级）；
3. 多根工作区（无 `.cosln` 直接开文件夹）是否支持——倾向 MVP 后按需求加；
4. `BuildReport` 结构化构建诊断返回（替代 §9 正则解析）——P1 与 Core 团队对齐接口。

---

## 附录 A：可复用编译器 API 清单（调研结论，2026-08-25）

**Public（直接可用）**
- 语法：`SyntaxTree.Parse/ParseCs/Load`、`SyntaxNode.GetChildren/Ancestors/Span/WriteTo`、`SyntaxToken(Position/Text/Value/Trivia)`、`SeparatedSyntaxList<T>`、`SyntaxFacts`、`LanguageDialect`
- 文本：`SourceText.From/Lines/ToString(span)`、`TextSpan.Start/Length/End/OverlapsWith`、`TextLocation(FileName/StartLine/StartCharacter/EndLine/EndCharacter)`
- 诊断：`Diagnostic(IsError/IsWarning/Location/Message)` + 静态工厂
- 编译：`Compilation.Create/Create(references,…)/CreateScript(previous,trees)/Evaluate/Emit(IL)/GetSymbols/MainFunction/Functions/Variables`
- 符号：`Symbol.ToString()`（Hover 文本）、`FunctionSymbol(Parameters/ReturnType/Declaration/Syntax/ContainingClass/…)`、`ClassTypeSymbol(FullName/BaseType/Fields/Methods/Properties/Events/GetMethod/GetProperty/…)`、`TypeSymbol` 静态单例、`EnumTypeSymbol`/`VariableSymbol`/`ParameterSymbol`
- 分类：`Classifier.Classify(SyntaxTree, TextSpan)`（M0 迁入 Core）、`Classification`、`ClassifiedSpan`
- 控制台着色：`Cocoa.IO.TextWriterExtensions.WriteKeyword/…`（REPL 终端复用）
- 项目：`CocoaProjectFile.Load(+GetOutputDirectory/GetDefaultOutputFileName)`、`CocoaSolutionFile.Load`、`ProjectFileParser`、`UserProjectOverrides`、`Glob.Expand`、`ProjectBuilder.Build`、`SolutionBuilder.Build/TopologicalOrder`、`ProjectBuildOptions(CacheRoot/Backend/…)`、`ProjectBuildResult(Success/UpToDate)`、`BuildCache(ComputeFingerprint/IsUpToDate/Write)`

**Internal（门面内部使用，不对外）**
- `Binder.BindGlobalScope/BindProgram`、`BoundGlobalScope(Diagnostics/Functions/Enums/Classes/Variables/UsingNamespaces…)`、`BoundProgram`、`BoundScope(TryLookupSymbol/TryLookupFunctions/TryLookupNamespaceFunctions/GetDeclaredVariables/Enums/Classes/Functions)`
- `CodProgram/CodSerializer/SystemLibrary.Load()`（stdlib 注入机制）
- `SyntaxTree.GetParent`（惰性父子索引）
- `ControlFlowGraph/BasicBlock`（远期数据流可视化储备）
- `Evaluator(_globals/_locals/_thisStack/_closureEnvironments)` + `BoundSequencePointStatement`（M7 调试器挂载点）

**关键缺口（本设计以 M0 门面闭合）**
- 位置→符号解析（`ResolveAtPosition`）：全新实现，积木齐备（token 遍历 + Span + Scope 查询 + `Symbol.Syntax` 反查声明）
