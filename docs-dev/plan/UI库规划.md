# UI 生态系统规划（Handle + System.UI，6e-M25 规划）

> 规划标记：🧭 规划/待办（2026-09-08 定稿；未开工）
> 状态：📋 设计定稿（2026-09-08，经范式调研 + 仓颉 eDSL 调研 + 双轮审查修正）
> 目标：为 Cocoa 建立 **UI 生态系统**两块基石——① `Handle` 通用资源句柄类型（System.Core，对接 Win32/POSIX 句柄语义，修复 64 位指针截断隐患）；② `System.UI` 独立立即模式 UI 库（ImGui 风格，双后端 IL + Native，先 Windows 后 Linux），远期叠加**声明式语法糖**（函数调用风格，仓颉 eDSL 方向）。
> 核心决策（ADR 登记 docs-dev/README §4）：
> ① **UI 范式 = 立即模式**（microui 1.1k 行下限 / Nuklear 18k 行参照；Cocoa 现有特性 100% 覆盖，保留模式与 XAML 声明式不采用）；
> ② **Handle 置 System.Core**（通用资源原语，IO/Net 复用；UI 专用句柄 WindowHandle/DCHandle 归 System.UI）；
> ③ **分发模型方案 B**：System.UI.coa 不进 `libs/`，用户项目显式 `<Reference>` 引入（真独立库，.NET+WPF 同构）；
> ④ **无回调轮询架构**：WNDPROC 填 `DefWindowProcW` 地址（`GetProcAddress` 获取），`PeekMessage` 自取消息 + `GetAsyncKeyState`/`GetCursorPos` 轮询，双后端统一，完全回避原生函数指针回调；
> ⑤ **编译器前置增强**：native extern 参数上限 7→12+（CreateWindowExW 12 参硬需求）；
> ⑥ **`.coa` 序列化门禁前置扩展**：带属性实例类 / 含 body 静态类入库（Handle/System.UI 类的硬前置，与「流式库」前置项同源，见 CHANGELOG 2026-09-06 已知边界）。
> 相关文档：`docs-dev/plan/Cocoa.IDE设计.md`（IDE 为 UI 库远期消费方）、`docs/互操作手册.md`（import 机制）、`docs-dev/标准库设计.md`（System.Core 布局约定）、`docs/项目格式规范.md`（Reference 机制）
> 最后更新：2026-09-08

---

## 目录

1. [目标与范围](#1-目标与范围)
2. [范式调研结论（为何立即模式）](#2-范式调研结论为何立即模式)
3. [总体架构](#3-总体架构)
4. [Handle 类型系统（System.Core）](#4-handle-类型系统systemcore)
5. [System.UI 库设计](#5-systemui-库设计)
6. [Win32 后端：轮询架构](#6-win32-后端轮询架构)
7. [分发模型（方案 B）](#7-分发模型方案-b)
8. [实现阶段](#8-实现阶段)
9. [示例项目](#9-示例项目)
10. [测试与文档计划](#10-测试与文档计划)
11. [风险与边界](#11-风险与边界)
12. [决策记录（ADR）](#12-决策记录adr)

---

## 1. 目标与范围

### 1.1 纳入本规划

| 项 | 说明 |
|----|------|
| Handle 类型（System.Core） | `Handle` 基类（`Raw: long`）+ FileHandle/ProcessHandle；修复现有 `GetModuleHandleW(moduleName: i32): i32` 式 64 位截断隐患 |
| System.UI 库（`src/Cocoa.UI/`） | 立即模式核心（ImGui/IO/DrawList/ID/Storage/Style）+ GDI 轮询后端 + Win32 import 声明 |
| 编译器增强 ×2 | native extern 参数上限 7→12+；`.coa` 序列化门禁扩展（带属性实例类/含 body 静态类） |
| 示例 ×3 | BasicUI（IL）/ AdvancedUI（主题+多窗口）/ NativeUI（`-b native` 零依赖 PE） |
| 声明式语法糖（阶段 5） | UIView 基类 + VStack/HStack 工厂函数 + `Body()` 组件约定（Elm/Flutter 风格函数调用） |

### 1.2 后置（不在本规划）

- XAML 类独立标记语言（用户明确不要；声明式走宿主语言语法糖）
- 保留模式 Widget 框架（Qt/GTK 类，复杂度 100k+ 行不成比例）
- Linux 后端（依赖 ELF 输出，阶段 9 后评估）
- trailing lambda / 类型扩展（`50.vp`）/ `@State` 宏（仓颉式 eDSL 完整体验，编译器远期增强）
- 多视口 / docking / 表格 / 动画系统（参照 ImGui 分期，v2 议题）

### 1.3 动机

- **编译器能力展示**：UI 是用户可见产出，比自举更能直观验证 import/类库/泛型/委托全链路；
- **精准暴露短板**：UI 开发过程反推编译器需补什么（本轮已定位参数上限、序列化门禁两项）；
- **生态位**：Cocoa 定位系统级语言，工具/调试器/仪表盘类 UI 是其主要场景，立即模式恰好最优。

---

## 2. 范式调研结论（为何立即模式）

### 2.1 三范式实现复杂度（行业数据）

| 范式 | 参照实现 | 代码量 | 需要的编译器特性 | Cocoa 覆盖 |
|------|---------|--------|----------------|-----------|
| **立即模式** | microui（C89）≈1.1k 行；Nuklear ≈18k 行；Dear ImGui ≈30k+ 行 | 最小 | 函数/数组/基本类型即可 | ✅ 100% |
| 保留模式 | Qt ≈5M 行；Fyne ≈200k 行 | 极大 | struct/泛型/闭包/OOP 全家桶 | ⚠️ 部分（无 struct） |
| 声明式 DSL | Slint ≈200k 行（自含 DSL 编译器） | 极大 | 宏/元编程/绑定引擎 | ❌ |

### 2.2 新系统语言趋势

Rust→egui（立即）、Go→Gio（立即）、Zig→DVUI（立即）、C→Nuklear（立即）。新语言首个成熟 GUI 库几乎清一色立即模式。

### 2.3 仓颉 eDSL 调研（声明式远期方向）

仓颉声明式 UI 的干净语法依赖：**trailing lambda**（层级描述，官方口径省 70% 代码）、命名参数（Cocoa ✅ 已有）、类型扩展（`50.vp`）、省略 `new`、`@State` 宏 + property 代理。Cocoa 短期用**函数调用 + getter/setter lambda** 达到 Elm/Flutter 级体验（阶段 5），trailing lambda 等列为编译器远期增强。

---

## 3. 总体架构

```
┌──────────────────────────────────────────────┐
│ 声明式语法糖（阶段 5，函数调用风格）           │
│   VStack / HStack / Body() 组件约定           │
├──────────────────────────────────────────────┤
│ System.UI（立即模式核心）— 独立 .coa 库       │
│   ImGui / ImGuiIO / DrawList / ID / Storage  │
│   Widgets / Style / UI 句柄(WindowHandle…)   │
├──────────────────────────────────────────────┤
│ 渲染后端（GDI 轮询架构，IL/Native 双后端统一） │
├──────────────────────────────────────────────┤
│ Handle 类型（System.Core）+ 编译器互操作增强  │
└──────────────────────────────────────────────┘

依赖方向：声明式 → System.UI → System.Core（单向，禁止反向）
```

## 4. Handle 类型系统（System.Core）

### 4.1 归属依据

Handle 是**通用资源原语**非 UI 专属：System.IO FileStream 内部存 FD、未来 System.Net Socket 同需；.NET 对齐 `System.IntPtr` 位于 `System.Runtime`；System.Core 随 `SystemLibrary` 自动加载零配置。UI 专用句柄（WindowHandle/DCHandle/GdiObjectHandle）**不放 Core**（方案 B 下 UI 类型归 UI 库，依赖单向）。

### 4.2 目录

```
src/Cocoa.SDK/System.Core/Handle/
├── Handle.co              # 基类
├── FileHandle.co          # 文件句柄
└── ProcessHandle.co       # 进程句柄
```

### 4.3 API 定义

```cocoa
namespace System
{
    class Handle
    {
        public var Raw: long

        constructor(value: long) { this.Raw = value }

        static function FromPtr(ptr: long): Handle { return new Handle(ptr) }
        static function Null(): Handle { return new Handle(0) }

        function IsNull(): bool { return this.Raw == 0 }
        function IsNotNull(): bool { return this.Raw != 0 }
        function ToLong(): long { return this.Raw }
        function ToInt(): i32 { return this.Raw as i32 }
        function Equals(other: Handle): bool { return this.Raw == other.Raw }
        function GetHashCode(): i32 { return this.Raw as i32 }
        // ToString 保守实现：Int64ToString 拼 "Handle(0x" + hex + ")"（不依赖未验证的 {x:X} 格式符）
    }
}
```

类型化句柄（FileHandle 增 `Path: string`/`IsOpen()`；ProcessHandle 增 `ProcessId: i32`/`IsValid()`；UI 句柄在 System.UI 中同样继承 Handle）。

### 4.4 `ToInt()` 适用范围（API 文档必须注明）

- USER/GDI 句柄（HWND/HDC/HBRUSH）：x64 上是**符号扩展的 32 位值**，ToInt 截断往返安全；
- kernel 句柄（HANDLE/文件 FD）：完整 64 位指针，**禁止 ToInt 截断**，一律 ToLong。

### 4.5 既有 import 的修正方向

所有 Win32 指针/句柄参数与返回值统一 `long`（`GetModuleHandleW(name: i32): long` 等）；阶段 0a 落地时顺带修正 samples/Interop 既有 `i32` 句柄声明并补双后端 e2e。

---

## 5. System.UI 库设计

### 5.1 定位

| 属性 | 决策 |
|------|------|
| 位置 | `src/Cocoa.UI/`（与 Cocoa.Cs/Cocoa.SDK 平级，独立仓库候选体） |
| 产物 | `System.UI.coa`（OutputType=Cocoa），**不进 libs/**（方案 B，§7） |
| 命名空间 | `System.UI`（核心）/ `System.UI.Backends`（Win32 import） |
| 依赖 | `<Reference Include="../Cocoa.SDK/out/System.Core.coa" />` |
| 后端 | IL 完整功能（CLR 编组 string/float）；Native 简化版（int/long，无 float/string） |

### 5.2 目录结构

```
src/Cocoa.UI/
├── System.UI.coproj
├── ImGui.co                    # 核心入口：NewFrame/Begin/End/Render + Widget 静态门面
├── ImGuiIO.co                  # 输入状态（鼠标/键盘/显示尺寸/字符流）
├── ImGuiStyle.co               # 主题 + ImGuiCol 枚举（Dark/Light/Classic 预设）
├── ImGuiWindow.co              # 窗口状态（pos/size/scroll/cursor/StateStorage）
├── ImGuiLayout.co              # 布局引擎（cursor 推进/ItemSize/ItemAdd/SameLine/Group）
├── ImGuiID.co                  # FNV-1a 哈希 + ID 栈（PushID/PopID/##id 约定）
├── ImGuiDrawList.co            # 顶点/索引/命令列表（平行数组模拟 vector）
├── ImGuiStorage.co             # 开放寻址 hash map（int/bool/float 三视图）
├── Handles.co                  # WindowHandle/DCHandle/GdiObjectHandle extends System.Handle
├── Widgets/                    # Text/Button/Checkbox/SliderFloat/SliderInt/InputText/
│                               # ProgressBar/Separator/SameLine/CollapsingHeader/TreeNode/ScrollBar
├── Backends/
│   ├── Win32Imports.co         # IL 版 import 声明（string/float 可用，句柄统一 long）
│   ├── Win32NativeImports.co   # Native 版 import 声明（全 int/long，阶段 4 依赖参数上限增强）
│   ├── Win32Window.co          # RegisterClass/PeekMessage 轮询循环/输入采集
│   └── Win32GDIBackend.co      # 双缓冲渲染（CreateCompatibleDC + BitBlt）+ DrawList→GDI 翻译
└── Syscall/
    └── UISyscall.co            # 字体测量等底层声明
```

### 5.3 核心 API（节选）

```cocoa
namespace System.UI
{
    class ImGui
    {
        static function NewFrame(io: ImGuiIO): void
        static function Render(): ImGuiDrawData

        static function Begin(name: string): bool
        static function End(): void

        static function Text(text: string): void
        static function Button(label: string): bool
        static function Checkbox(label: string, storage: ImGuiStorage, id: i32): bool
        static function SliderFloat(label: string, storage: ImGuiStorage, id: i32,
                                     min: f32, max: f32): f32
        static function SliderInt(label: string, storage: ImGuiStorage, id: i32,
                                   min: i32, max: i32): i32
        static function ProgressBar(fraction: f32): void
        static function Separator(): void
        static function SameLine(): void
        static function CollapsingHeader(label: string): bool
        static function TreeNode(label: string): bool
        static function TreePop(): void

        static function IsItemHovered(): bool
        static function BeginChild(id: string, w: f32, h: f32, flags: i32): bool
        static function EndChild(): void

        static function PushStyleColor(idx: i32, r: f32, g: f32, b: f32, a: f32): void
        static function PopStyleColor(count: i32): void

        static function CalcTextSize(text: string): f32
        static function GetFrameCount(): i32
    }
}
```

ID 系统：`ID = FNV1a(label, seed=ID栈顶)`；支持 `"Label##hidden"`（同文案多实例）与 ID 栈（PushID/PopID）。状态存储：per-window `ImGuiStorage`（id→int/bool/float），Checkbox/Slider/TreeNode 等持久状态经此读写（ImGui 同构）。

### 5.4 import 声明规范

- **所有指针/句柄参数一律 `long`**（修正审查发现的 i32/long 混用）；
- IL 后端：`charset = unicode`，`string` 参数直传（CLR 编组 LPWSTR）；
- Native 后端：文本走 `i32` 传 UTF-16 缓冲指针（`StringToBytes` 系原语 + HeapAlloc 缓冲）；
- MSG/POINT 等可写结构：`HeapAlloc` 分配、`long` 传址、完成后 `HeapFree`。

### 5.5 示例语法约束（保守写法）

- 插值格式符仅使用已验证集（`{x,10}`/`{x:F2}` 等）；hex 用字符串拼接；
- lambda 用显式参数 `{ x => ... }`，不使用 `_` 占位符（未验证语法）；
- 方法组→delegate（`Button("OK", OnClick)`）已验证可用。

---

## 6. Win32 后端：轮询架构

### 6.1 为何无回调

WNDPROC 需要原生函数指针回调；Cocoa 双后端均无可靠路径（IL 的 delegate→函数指针编组未经 emitter 支持，native 无回调机制）。立即模式 UI 本就适合轮询架构（输入每帧采样）。

### 6.2 架构

```
窗口创建：
  wndprocAddr = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW")
  RegisterClassExW(... lpfnWndProc = wndprocAddr ...)   // 消息全交 DefWindowProc
  CreateWindowExW(...) + ShowWindow

每帧循环：
  while PeekMessageW(msgBuf, 0, 0, 0, PM_REMOVE):        // 自取消息，不 Dispatch
      if msg == WM_QUIT → 退出
      TranslateMessage(msgBuf)                            // WM_CHAR 进队供 ImGuiIO 消费
  GetCursorPos + ScreenToClient → io.MouseX/Y
  GetAsyncKeyState(VK_LBUTTON...) → io.MouseButtonDown[]
  WM_CHAR 队列 → io.AddInputCharacter
  QueryPerformanceCounter → DeltaTime

  ImGui.NewFrame(io) → 用户 UI 代码 → ImGui.Render()
  GDI 双缓冲：CreateCompatibleDC/Bitmap → 遍历 DrawCmd
    （FillRect/Rectangle/TextOutW/SetTextColor/SetBkMode）→ BitBlt 到屏幕
```

### 6.3 后端能力矩阵

| 功能 | IL 后端 | Native 后端 |
|------|---------|------------|
| 窗口/消息/输入 | 完整 Win32 API（string/long/float） | 同 API，全 int/long 参数 |
| 文本 | string 参数（CLR 编组） | i32 传 UTF-16 缓冲指针 |
| 滑块 | SliderFloat | SliderInt |
| 布局精度 | f32 亚像素 | int 像素级 |
| 主题 | RGBA f32 → packed ABGR | packed ABGR 直传 |
| 依赖 | .NET Framework 4.x 运行时 | 零依赖原生 PE |

---

## 7. 分发模型（方案 B）

**决策**：System.UI.coa **不复制进** `src/Cocoa.Cs/libs/`（否则会被 `SystemLibrary.LoadCore()` 的 `System*.coa` 枚举自动加载，与「显式 Reference」语义冲突）。

```xml
<!-- 用户项目 .coproj -->
<ItemGroup>
  <Source Include="*.co" />
  <Reference Include="../../../src/Cocoa.UI/out/System.UI.coa" />
</ItemGroup>
```

- 构建链已有支撑：`.coa` 引用 → 语义合并（native 静态链接）/ 动态链接托管包装（IL）；CopyLocal 复制到输出目录；
- 需验证点（阶段 1 spike）：多库拓扑（refcod）+ 同名检测在 samples 相对路径下正常；
- 未来独立仓库：`git mv` `src/Cocoa.UI` 即可，`.coproj` 结构不变（.NET+WPF 同构模型）。

---

## 8. 实现阶段

| 阶段 | 内容 | 前置 | 工作量 |
|------|------|------|--------|
| **0a** | Handle 类型（System.Core/Handle/）+ spike：① `extends` 继承链经 `.coa` 静态链接在 native 下验证（M19 理论支持无 UI 实例）；② long 参数/返回值 import 双后端 e2e | **`.coa` 序列化门禁扩展**（带属性实例类/含 body 静态类入库，见 §11 R1） | 1-2 天（门禁另计） |
| **0b** | 编译器增强：native extern 参数上限 7→12+（x64 寄存器 4+栈；x86 栈压入，扩 PushSysCallArg 路径）+ 12 参 stdcall e2e（x86/x64） | — | 1-2 天 |
| **1** | System.UI 基础框架 + GDI 轮询后端 + BasicUI 最小 demo（里程碑：窗口/双缓冲/Begin-End/Text/Button/点击动作） | 0a/0b | 1-2 周 |
| **2** | 完整控件集（Checkbox/Slider/InputText/ProgressBar/Separator/SameLine/CollapsingHeader/TreeNode/Child 滚动） | 1 | 1-2 周 |
| **3** | 主题系统（Dark/Light/Classic + PushStyleColor/Var）+ AdvancedUI demo | 2 | 1 周 |
| **4** | Native 后端适配（Win32NativeImports + SliderInt + 手动 UTF-16）+ NativeUI demo | 0b + 3 | 3-5 天 |
| **5** | 声明式语法糖（UIView/VStack/HStack/Body() 约定/getter-setter lambda 双向绑定） | 3 | 1-2 周 |
| **6** | Linux 跨平台（ELF 输出后：SDL2/OpenGL 后端） | 编译器 ELF | 远期 |

执行纪律沿用仓库惯例：每步独立提交 + 全量测试绿 + 文档回填（§10）。

---

## 9. 示例项目

```
samples/Samples/UI/
├── BasicUI/      # IL 后端：窗口+文本+按钮+复选框+滑块；.coproj 显式 Reference System.UI.coa
├── AdvancedUI/   # 多窗口 + 主题切换 + 折叠标题 + 树 + 滚动区 + 进度条
└── NativeUI/     # -b native --platform x64；简化控件集（Text/Button/Checkbox/SliderInt/Separator）
```

BasicUI 骨架（保守语法）：

```cocoa
using System
using System.UI

function Main(): i32
{
    var io = new ImGuiIO()
    var storage = new ImGuiStorage(64)
    // Win32 窗口 + PeekMessage 轮询循环（Backends.Win32Window）
    while Win32Window.Pump(io)
    {
        ImGui.NewFrame(io)
        if ImGui.Begin("Cocoa.UI Demo")
        {
            ImGui.Text("Welcome to Cocoa.UI!")
            ImGui.Separator()
            if ImGui.Button("Click Me")
            {
                Console.WriteLine("Button clicked!")
            }
            ImGui.SameLine()
            ImGui.Checkbox("Enable", storage, 1)
            ImGui.SliderFloat("Value", storage, 2, 0.0, 100.0)
        }
        ImGui.End()

        var drawData = ImGui.Render()
        Win32GDIBackend.Present(drawData)
    }
    return 0
}
```

---

## 10. 测试与文档计划

| 类别 | 内容 |
|------|------|
| e2e（阶段 0a） | long 参数/返回值 import ×IL/Native ×x86/x64；Handle 基础行为 |
| e2e（阶段 0b） | 12 参数 stdcall extern ×x86/x64（栈传参正确性） |
| 序列化 | 门禁扩展后：带属性实例类 / 含 body 静态类 `.coa` 往返 |
| UI 冒烟 | 离屏 DC（CreateCompatibleDC）headless 渲染 DrawList 比对（CI 无窗口可跑） |
| 单元 | FNV-1a 哈希 / ImGuiStorage 往返 / 布局 cursor 推进 |
| 文档 | `docs/UI库手册.md`（新）；`docs/标准库API参考.md`（Handle）；`samples/README.md`（3 示例）；`docs/互操作手册.md`（long 句柄规范 + 参数上限变更）；`CHANGELOG.md` |

---

## 11. 风险与边界

| # | 风险 | 等级 | 缓解 |
|---|------|------|------|
| R1 | **`.coa` 序列化门禁**：带属性实例类/含 body 静态类暂不可入库（2026-09-06 已知边界，System.IO 库曾因此撤回）——Handle/System.UI 类直接命中 | 🔴 阶段 0a 硬前置 | 与「流式库」前置项同源，优先扩门禁（属性实例类 + 构造器 body 序列化）；门禁未扩前 Handle 只能以源文件形式进编译测试，不入 `.coa` |
| R2 | native extern 7 参数上限阻塞 CreateWindowExW(12 参) | 🔴 阶段 4 硬前置 | 阶段 0b 编译器增强（已拍板） |
| R3 | WNDPROC 无回调路径 | 🟡 已消解 | 轮询架构（§6），双后端统一 |
| R4 | `extends` 经 `.coa` 在 native 静态链接下未实测 | 🟡 | 阶段 0a spike 先行；不通过则 Handle 族降级为「每后端伴生声明」 |
| R5 | 无 struct → API 参数冗长（x1,y1,x2,y2） | 🟡 | 接受（struct 明确不做，类替代）；阶段 5 声明式缓解 |
| R6 | GDI 性能（CPU 绘制，无硬件加速） | 🟡 | 双缓冲 + 脏区域；工具类 UI 规模够用；远期 OpenGL 后端 |
| R7 | native 无 float/string 参数 | 🟢 | SliderInt + UTF-16 手动缓冲（既有原语） |
| R8 | 语法细节未验证（`{x:X}` 格式符、`_` lambda） | 🟢 | 示例保守写法（§5.5）；spike 顺带确认 |

---

## 12. 决策记录（ADR）

| # | 决策 | 依据 | 日期 |
|---|------|------|------|
| A1 | UI 范式 = **立即模式**；XAML 式标记语言不采用；保留模式不采用 | 实现复杂度 1.1k vs 100k+ 行；Cocoa 特性覆盖度 100%；新系统语言趋势（egui/Gio/DVUI/Nuklear） | 2026-09-08 |
| A2 | 声明式走**宿主语言语法糖**（函数调用 + 组件约定），远期对齐仓颉 eDSL（trailing lambda/类型扩展为编译器增强项） | 仓颉白皮书：eDSL 优于独立 DSL；Cocoa 无宏/resultBuilder，但函数调用已达 Elm/Flutter 级体验 | 2026-09-08 |
| A3 | **Handle 置 System.Core**；UI 专用句柄归 System.UI | 通用资源原语（IO/Net 复用）；.NET IntPtr 同构；依赖单向 | 2026-09-08 |
| A4 | **分发方案 B**：Reference 显式引入，不进 libs/（避免 SystemLibrary 自动枚举吞并） | 真独立库语义；.NET+WPF 同构；`git mv` 即可分仓 | 2026-09-08 |
| A5 | **无回调轮询架构**（DefWindowProc 地址 + PeekMessage + GetAsyncKeyState） | 双后端无可靠函数指针回调路径；立即模式输入本就每帧采样 | 2026-09-08 |
| A6 | 编译器前置增强两项：**native extern 参数上限 7→12+**；**`.coa` 序列化门禁扩展** | CreateWindowExW 12 参硬需求；Handle/实体类入库硬前置（与流式库前置项同源） | 2026-09-08 |
| A7 | UI 库位置 **`src/Cocoa.UI/`**（与 Cocoa.Cs/SDK 平级），开发期同仓库，成熟后可分仓 | 编译器快速迭代期分仓同步成本高；发布 v1.0 后再评估 | 2026-09-08 |
