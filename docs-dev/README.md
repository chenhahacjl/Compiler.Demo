# Cocoa 开发文档索引（docs-dev）

> 状态：✅ 生效（2026-09-06，文档体系整理）
> 定位：开发文档分类导航 + ADR（架构决策记录）索引；开发文档按 **根（总纲）/ plan（规划·待办）/ archive（已实现·设计依据）** 三层组织。
> 相关：[docs/README.md](../docs/README.md)（正式参考入口）、[docs/文档格式规范.md](../docs/文档格式规范.md)（格式约定）

---

## 1. 根级（总纲 / 现状执行）

| 文档 | 定位 |
|------|------|
| [开发计划.md](开发计划.md) | 阶段路线图总纲（阶段 0-9，含各里程碑提交与进度） |
| [标准库设计.md](标准库设计.md) | System.Core 单库布局 · Syscall 收口 · facade 原则与例外 |

## 2. plan/ — 规划 · 待办（🧭/📋）

| 文档 | 定位 |
|------|------|
| [plan/自举缺口分析.md](plan/自举缺口分析.md) | 阶段 7 前置盘点：语言面现状 / 标准库缺口分级 / 实施顺序（已定稿） |
| [plan/IR分层与格式设计.md](plan/IR分层与格式设计.md) | HIR/MIR/LIR 三层语义与命名、「.coa 存 HIR」决策（S-7 定稿为准） |
| [plan/语义债务清单.md](plan/语义债务清单.md) | 定夺类设计偏差 D1-D5（非 bug），修或维持需逐项拍板 |
| [plan/重构执行计划.md](plan/重构执行计划.md) | 重构步骤与进度表（⬜/🔄/✅/⏭️ 标记） |
| [plan/Cocoa.IDE设计.md](plan/Cocoa.IDE设计.md) | 类 Visual Studio 桌面 IDE：Avalonia 11 路线 + 功能矩阵 |
| [plan/文档注释设计.md](plan/文档注释设计.md) | `///` 文档注释 → XML + `.coa` 内嵌（6e-M24 规划，未开工） |

## 3. archive/ — 已实现 · 设计依据保留（🛑 归档）

以下设计稿对应特性均已落地；正文与终态能力以检索 [docs/语法手册.md](../docs/语法手册.md) 及 [docs/标准库API参考.md](../docs/标准库API参考.md) 为准，本目录仅作设计依据与决策追溯：

| 设计稿 | 对应特性（落地） |
|--------|------------------|
| [archive/对象模型设计.md](archive/对象模型设计.md) | System.Object 基类 / 全类型成员方法 / System.Type / native vtable（6e-M19，2026-08-24） |
| [archive/OOP设计.md](archive/OOP设计.md) | 继承/多态/静态/readonly/属性（M5 + 6e-M10） |
| [archive/out与ref参数设计.md](archive/out与ref参数设计.md) | out/ref 参数 + 明确赋值（6e-M23，R1-R9，2026-08-26） |
| [archive/内部调用与互操作设计.md](archive/内部调用与互操作设计.md) | syscall / import 块 / extern 元数据（6e-M17，2026-08-22） |
| [archive/数值类型扩展设计.md](archive/数值类型扩展设计.md) | i8-u64/f32/f64 数值全集（6e-M21，2026-08-23） |
| [archive/委托与Lambda设计.md](archive/委托与Lambda设计.md) | 函数类型/Lambda/闭包/事件（C1-C8）+ delegate 真实类型化决策（Q1-Q4） |
| [archive/泛型设计.md](archive/泛型设计.md) | 泛型类/接口/方法/约束 + 编译期单态化（G0-G7） |
| [archive/标准库设计.md](archive/标准库设计.md) | 功能层/逻辑层体系 + 类库交付形态 + BCL 对齐（吸收本目录另两份） |
| [archive/类库设计.md](archive/类库设计.md) | class 库构建/消费（-r + using）— 已并入标准库设计 |
| [archive/SDK标准库增强方案.md](archive/SDK标准库增强方案.md) | 泛型集合 BCL 对齐— 已并入标准库设计 |
| [archive/输出格式.md](archive/输出格式.md) | exe/library/.coa 输出（2026-08-20 起逐一落地） |
| [archive/代码结构.md](archive/代码结构.md) | 后端分层与命名约定（结构治理 Phase 1/2 依据，现行见 CODING.md） |

## 4. ADR — 架构决策记录索引

关键决策（Q/A-style）在归档稿头部或专项稿中留存，此处登记：

| 决策 | 决策方/日期 | 落点 |
|------|-------------|------|
| delegate 真类型化（真 CLR 委托/多播/列表相等/泛型 in/out 型变）Q1-Q4 | 用户，2026-09-05 | [archive/委托与Lambda设计.md](archive/委托与Lambda设计.md) 头部 |
| 标准库 API 形态 = 补齐 C# 形态；数字解析 Parse 抛异常 + TryParse(s,out) | — | [plan/自举缺口分析.md](plan/自举缺口分析.md) §4 |
| 文件 IO 走 Runtime syscall 路线 | — | [plan/自举缺口分析.md](plan/自举缺口分析.md) §4.1 |
| `.coa` 存结构化 HIR（非 goto-only）；MIR 不落盘；LIR native 私有 | S-7，2026-10-11 | [plan/IR分层与格式设计.md](plan/IR分层与格式设计.md) 头部 |
| 6e 里程碑（M14 标准库 / M15 双前端 / M17 互操作 / M19 对象模型 / M20 泛型 / M21 数值 / M22 委托 / M23 out-ref） | 用户+实施 | [开发计划.md](开发计划.md) |

## 5. 维护约定

- **特性落地当天**：归档对应设计稿 + 同步 [docs/README.md](../docs/README.md) 状态列 + ADR 表登记。
- **新增档案**：设计稿完成且实现落地 → `git mv` 至 `archive/`，头部状态行改 `🛑 已归档`。
- 纯过程进度 / 专项流水（S5/S7/项目结构重组等）不归档，决策吸收进保留文档后删除（git 保留历史）。