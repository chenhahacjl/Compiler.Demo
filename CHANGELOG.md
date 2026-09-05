# Changelog

> 状态：✅ 生效（2026-09-06）
> 定位：面向用户的近期变更流水（里程碑粒度）；完整提交历史见 git。
> 相关：[docs/README.md](docs/README.md)、[docs-dev/开发计划.md](docs-dev/开发计划.md)（阶段路线图总计）

---

## 未发布（2026-09-06）

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
- 修复 33 处文档坏链；新增 [docs/快速上手.md](docs/快速上手.md) 与 [docs/标准库API参考.md](docs/标准库API参考.md)；语法手册状态标记核对补齐。

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