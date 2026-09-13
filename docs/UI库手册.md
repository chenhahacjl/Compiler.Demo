# Cocoa System.UI 手册（立即模式 UI 库，阶段 3）

> 状态：✅ 阶段 3（2026-09-13）——完整控件集 + 键盘/滚轮输入 + Child 滚动/裁剪 + 主题系统（Dark/Light/Classic + 样式栈）；IL 后端可用，Native 后端属阶段 4。
> 设计依据：[`docs-dev/plan/UI库规划.md`](../docs-dev/plan/UI库规划.md)；相关：[`docs/互操作手册.md`](互操作手册.md)。

## 1. 定位与分发

`System.UI` 是独立 `.coa` 库（**方案 B**：不复制进 `src/Cocoa.Cs/libs/`，用户项目显式引用），位于 `src/Cocoa.UI/`，与 `src/Cocoa.Cs`、`src/Cocoa.SDK` 平级。产物：`src/Cocoa.UI/out/System.UI.coa`。

```xml
<!-- 用户项目 .coproj -->
<ItemGroup>
  <Source Include="*.co" />
  <Reference Include="../../../../src/Cocoa.UI/out/System.UI.coa" />
</ItemGroup>
```

构建顺序：先 `cocoa build -p src/Cocoa.UI/System.UI.coproj`，再构建应用（参见 `samples/Samples/UI/BasicUI/build.cmd` 与 `samples/Samples/UI/AdvancedUI/build.cmd`）。

## 2. 快速上手

```cocoa
using System
using System.UI
using System.UI.Backends

function Main(): i32
{
    var gui = new ImGui(4096, 64)          // 顶点/索引容量, 状态存储容量
    var io = new ImGuiIO()
    let hwnd = Win32Window.Create("Demo", 640, 420)
    if hwnd == 0 { return 1 }

    let memDC = Win32GDIBackend.CreateBuffer(hwnd, 640, 420)
    var running = true
    while running
    {
        running = Win32Window.Pump(io, hwnd)   // 采样输入；ESC/窗口消失 → false
        io.NewFrame(f32(0.016))

        gui.NewFrame(io)
        if gui.Begin("Demo", f32(10.0), f32(10.0), f32(620.0), f32(400.0))
        {
            gui.Label("Hello")
            if gui.Button("Click Me") { Console.WriteLine("clicked") }
            gui.SameLine()
            gui.CheckBox("Enable", 1)          // 第二参 = 持久状态 id
            gui.TrackBarFloat("Value", 2, f32(0.0), f32(100.0))
            gui.ProgressBar(f32(0.35))
        }
        gui.End()

        let dl = gui.Render()
        Win32GDIBackend.Present(hwnd, memDC, dl, 640, 420, ImGuiStyle.Abgr(30, 30, 30, 255))
        Win32Window.Sleep(16)
    }

    Win32GDIBackend.DestroyBuffer(memDC)
    Win32Window.Destroy(hwnd)
    return 0
}
```

退出：轮询架构无 `DispatchMessage`，窗口关闭按钮不触发；**按 ESC 退出**。

控件/输入/子区域补充示例（**控件命名参照 WinForms**）：

```cocoa
gui.TrackBar("Count", 3, 0, 20)                 // WinForms TrackBar（整型滑动）
gui.GroupBox("Advanced", 10)                    // WinForms GroupBox（可折叠，状态持久）
if gui.TreeView("Options", 11) { gui.Label("child item"); gui.EndTreeView() }  // WinForms TreeView

var name = gui.TextBox("Name", 20, 32)          // WinForms TextBox：点击聚焦、输入、返回文本
gui.Label("Hello, " + name)

if gui.BeginPanel(30, f32(300.0), f32(80.0))    // WinForms Panel：可滚动子区（滚轮）
{
    gui.Label("line 1")
    gui.Label("line 2")
}
gui.EndPanel()

gui.PushStyleColor(0, ImGuiStyle.Abgr(230, 120, 60, 255))  // 临时文本色
gui.Label("highlighted")
gui.PopStyleColor()
gui.PushStyleVar(ImGuiStyleVar.ItemSpacingY, f32(12.0))
gui.Label("loose spacing")
gui.PopStyleVar()
```

### 控件命名对照（WinForms）

| System.UI（现行） | WinForms 对应 | 说明 |
|-------------------|---------------|------|
| `Button` | Button | 同 |
| `Label` / `LabelColored` | Label（ForeColor） | 带色变体对应 ForeColor |
| `TextBox` | TextBox | 单行输入 |
| `CheckBox` | CheckBox | 同 |
| `TrackBar` / `TrackBarFloat` | TrackBar | WinForms TrackBar 为整型；`TrackBarFloat` 为浮点扩展 |
| `ProgressBar` | ProgressBar | 同 |
| `GroupBox` | GroupBox | 可折叠分组 |
| `TreeView` / `EndTreeView` | TreeView / TreeNode | 节点区域 |
| `BeginPanel` / `EndPanel` | Panel | 子区域（裁剪 + 滚动） |
| `Separator` | —（无对应控件） | 分隔线，保留 |
| `SameLine` / `Indent` / `Spacing` / `Dummy` | FlowLayoutPanel 等布局能力 | 布局辅助，保留 |

## 3. 组件

| 组件 | 说明 |
|------|------|
| `ImGui` | 立即模式门面：`NewFrame`/`Begin`/`End`/`Render` + 控件（`Label`/`LabelColored`/`Button`/`CheckBox`/`TrackBar`/`TrackBarFloat`/`ProgressBar`/`Separator`/`SameLine`/`Dummy`/`Spacing`/`Indent`/`Unindent`/`GroupBox`/`TreeView`/`EndTreeView`/`TextBox`/`IsItemHovered`）+ 子区（`BeginPanel`/`EndPanel`）+ 样式栈（`PushStyleColor`/`PopStyleColor`/`PushStyleVar`/`PopStyleVar`） |
| `ImGuiIO` | 每帧输入：`MouseX/Y`、`SetMouseButton`/`IsMouseDown`、`SetKeyDown`/`IsKeyDown`、`MouseWheel`、字符队列（`AddInputCharacter`/`CharAt`/`CharCount`/`ClearChars`）、`DisplayWidth/Height`、`DeltaTime` |
| `ImGuiStorage` | id→i32/bool/f32 持久状态（`GetInt/SetInt` 等；控件第二参为 id） |
| `ImGuiStyle` | 颜色（`ImGuiCol` + `Abgr` 打包）+ 间距/圆角；预设 `MakeDark()`/`MakeLight()`/`MakeClassic()`（可运行期切换） |
| `ImGuiStyleVar` | 可临时覆盖的样式变量枚举（`FramePaddingX/Y`、`ItemSpacingX/Y`、`FrameRounding`、`Alpha`） |
| `ImGuiDrawList` | 顶点/索引 + 文本命令缓冲；`SetClipRect`/`ClearClip` 轴对齐裁剪 |
| `ImTypes` | `ImVec2`/`ImVec4` 值类型 |
| `ImGuiID` | FNV-1a 标签哈希（32 位，i32 承载） |
| `ImGuiWindow`/`ImGuiLayout` | 窗口状态 + cursor 推进 |

## 4. 后端（Win32，GDI 轮询）

| 类 | 职责 |
|----|------|
| `Win32Window` | `Create`（内建 `STATIC` 类）/`Pump`（PeekMessage + 光标/按键采样）/`Destroy`/`Now`/`Sleep` |
| `Win32GDIBackend` | `CreateBuffer`/`Present`（memDC 双缓冲 + Polygon/TextOutW + BitBlt）/`DestroyBuffer` |
| `Win32Imports` | user32/kernel32/gdi32 extern 声明（句柄统一 `nint`） |

## 5. 已知限制

- 仅 **IL 后端**（示例目标 `net48`）；Native 后端为阶段 4。
- 文本宽度按 **8px/字符** 近似；无字库度量、无自动换行。
- 裁剪为**整图元轴对齐**（矩形精确；文本按行 y 越界剔除，非逐像素）。
- 滚轮仅在 `BeginChild` 内生效（每帧一次，`Render` 清零）；无窗口缩放。
- 样式栈为 LIFO 覆盖（`PushStyleColor`/`PushStyleVar` 不跨帧）；`ImGuiStyleVar.Alpha` 已登记但当前扁平渲染后端未应用全局透明度。
- 无 `PushID/PopID` 的 ID 栈；`Begin` 仅单根窗口，无多窗口浮动/停靠。
- 无消息 `DispatchMessage`：窗口关闭按钮无效，用 ESC 退出。
- 值表达式约束：不使用 `^`/`~` 运算符与参数赋值（当前实现以等价写法规避）。

## 6. 综合示例（AdvancedUI）

`samples/Samples/UI/AdvancedUI/` 综合演示：工具栏（计数/重置/主题三态切换 Dark→Light→Classic）+ 左右双 `BeginPanel` 面板（导航 `TreeView`/`GroupBox` + 内容区控件）+ 进度条/滑块/`TextBox` + 12 行可滚动日志列表；计数为正时以 `PushStyleColor` 高亮。构建见其 `build.cmd`。
