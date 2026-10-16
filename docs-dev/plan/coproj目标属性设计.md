# `.coproj` 目标属性设计（定稿 v2，2026-10-15）

> 规划标记：🧭 规划/待办（2026-10-15 架构评审，三道关：分层/显式性/默认值均通过）
> 状态：**定稿待实施**（T0a-b-c 依据）。实施完成后合入 `docs/项目格式规范.md` §2.2/2.3。
> 前置阅读：[docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md)（目标模型与演进）、[docs-dev/plan/IR分层与格式设计.md](./IR分层与格式设计.md)（IR 分层）、[docs/项目格式规范.md](../../docs/项目格式规范.md)（现行 .coproj 格式）。
> 与现行差异：本设计是**目标演进态**（三分 Backend / `Arch` 命名 / TFM 必填 / target-aware SDK）；现行 `docs/项目格式规范.md` 表是**落地态**（二分 Backend / `Platform` / TFM 默认 net48），实施后两者合并。

---

## 1. 设计目标

1. **跨产物诚实**：`.coproj` 一个文件声明三种产物链（native / managed-fx / managed-net），`Backend` 显式必填，缺省报错。
2. **目标维度正交**：`TargetOS`（OS）× `Arch`（CPU）× `TargetFramework`（运行时版本）各司其职，不再由 TFM 兼任架构（`pe32 = Cpu != X64` 取代 `pe32 = IsNetFx`）。
3. **零跨后端特例**：校验只在 Backend 对应**组内**进行；组外属性写不写无关（非禁写、非忽略），从根源消除矛盾组合校验。

## 2. 属性模型（分组）

### A. 公共目标坐标（所有 Backend 一致）

| 属性 | 类型 | 可选值 | 默认 | 说明 |
|---|---|---|---|---|
| `TargetOS` | enum | `windows` · `linux` | `windows` | 决定 PE/ELF 文件格式；native+linux 自 T3 起启用（最小可执行 ELF：控制台链 + kernel32 四符号 shim） |
| `Arch` | enum | `anycpu` · `x86` · `x64` | `anycpu` | 原 `Platform` 更名；决定 `Cpu{AnyCPU,X86,X64}`。native 下 `anycpu` 报错（须显式 `--arch x86/x64`） |

### B. 生产链（显式必填）

| 属性 | 类型 | 可选值 | 默认 |
|---|---|---|---|
| `Backend` | enum | `native` · `managed-fx` · `managed-net` | 无（缺省报错，提示三选一） |

- 现 `Backend=dotnet|native|interpreter` 三值的 `dotnet`/`interpreter` 折叠为 `managed-net`/保留现状（interpreter 为求值器，非产物链，T0a 再定是否移出）。
- 三分制避免属性组合爆炸；`managed-` 前缀显式标出托管产物，与 `native` 对立。

### C. 后端细分属性（仅对应 Backend 组内读取）

| 属性 | 类型 | 可选值 | 默认 | 适用 Backend | 说明 |
|---|---|---|---|---|---|
| `TargetFramework` | text | `net40`~`net48` 或 `net5.0`+（如 `net9.0`） | 无（组内必填） | managed-fx / managed-net | fx∩net+、net∩4x 报错；native 组外无关 |
| `Prefer32Bit` | bool | `true` · `false` | `false` | managed-fx | 仅 `anycpu` 生效，COR20 `32BITPREFERRED(0x20000)`；非 anycpu 报错（复活死选项） |
| `RuntimeIdentifier` | text | `win-x64`（预留 `win-arm64`/`linux-arm64`） | 空 = AnyCPU | managed-net | apphost 选型；未实现 RID 报 NotSupported |
| `Subsystem` | enum | `Console` · `Windows` | `Console` | native | PE 子系统；`OutputType=library` 时忽略 |

### D. 构建属性（现有，不变）

| 属性 | 类型 | 可选值 | 默认 |
|---|---|---|---|
| `OutputType` | enum | `executable` · `library` · `cocoa` | `executable` |
| `Configuration` | enum | `Debug` · `Release` | `Debug` |
| `OutputPath` | 路径 | 相对项目文件目录 | 项目目录本身（不建子目录） |
| `AssemblyName` | text | — | 项目文件名 |
| `RootNamespace` | text | — | AssemblyName |
| `StartupObject` | type | `Namespace.Class`（Main 由绑定解析，如 `Program`/`Acme.App.Program`） | 缺省由绑定决定 |
| `RequestedExecutionLevel` | enum | `AsInvoker` · `HighestAvailable` · `RequireAdministrator` | `AsInvoker` |
| `ApplicationIcon` | 路径 | `.ico` | — |
| `DocumentationFile` | 路径 | — | — |
| `TreatWarningsAsErrors` | bool | `true` · `false` | `false` |
| `Optimize` | bool | `true` · `false` | `false` |

### E. 元数据组（进 VS_VERSIONINFO）

| 属性 | 类型 |
|---|---|
| `Company` / `Product` / `Copyright` / `Title` / `Description` / `Authors` | text |
| `AssemblyVersion` / `FileVersion` | 1~4 段 `major.minor.build.revision`，缺段补 0；非数字段报错 |
| `ProjectGuid` | GUID text（可选；缺省自动生成） |

### F. 项组（ItemGroup）

| 项 | 属性 | 说明 |
|---|---|---|
| `<Source Include>` | 路径/glob | 源文件，至少 1 条必填 |
| `<Reference Include>` | `.coa`/`.dll` 路径 | 库引用；可带 `Condition`；SDK 平台库由 `SystemLibrary.Load(TargetTriple)` 按 `TargetOS×Arch×TargetFramework` 过滤 |
| `<Import Include>` | 路径 | 项目级源文件引入（扩展 Source 集合，非库引用；与语言级 `import`（DLL）隔离） |
| `<Content Include>` | 路径 + `CopyToOutput` | 内容文件；`CopyToOutput` 默认 `false`（显式 opt-in） |
| `<Resource Include>` | 路径 | 通用 PE 资源（RT_RCDATA，native/IL） |
| `<EmbeddedResource Include>` | 路径 | IL ManifestResource（`GetManifestResourceStream`） |

### G. 条件（Condition）可用派生变量

| 变量 | 值示例 |
|---|---|
| `$(TargetTriple)` | `{os}-{arch}` → `windows-x64` / `linux-arm64` |
| `$(Runtime)` | `fx` · `net` · ``（native 空，条件写 `'$(Runtime)' == ''`） |
| `$(Backend)` `$(TargetOS)` `$(Arch)` `$(Configuration)` | 直接引用 |

## 3. 校验矩阵（实现依据）

| Backend | 非法组合 | 结果 |
|---|---|---|
| 任意 | `Backend` 值缺省 | 报错，提示三选一（T0c 提供迁移路径） |
| native | `Arch=anycpu` | 报错 |
| native | `TargetOS=linux` | T3 起启用（ELF ET_EXEC；仅 x64；支持面见 T3.3 文档） |
| native | `TargetFramework` 非空 | 组外无关（不报、不读） |
| managed-fx | `TargetFramework` ∉ net40~48 | 报错 |
| managed-fx | `RuntimeIdentifier` 非空 | 报错（fx 无 apphost 概念） |
| managed-fx | `Arch=x64` | 报错（netfx 无 x64 合法产物） |
| managed-net | `TargetFramework` ∉ net5.0+ | 报错 |
| managed-net | `RuntimeIdentifier` 未实现 RID | NotSupported |
| managed-net | `Arch≠anycpu` 且 RID 显式冲突 | 报错 |
| 任意 | `Prefer32Bit` 且 `Arch≠anycpu` | 报错 |
| 任意 | `Subsystem` 且 `OutputType=library` | 忽略 |

**RID 与 Arch 规则**：`RuntimeIdentifier` 非空时，`Arch=anycpu` 视为由 RID 推导；`Arch` 显式且与 RID arch 冲突 → 报错。

## 4. 属性读取顺序

1. 读 `Backend`（缺省报错）
2. 读公共坐标 `TargetOS` / `Arch`（+RID 推导/冲突）
3. 按 Backend 组读 `TargetFramework` / `Prefer32Bit` / `RuntimeIdentifier` / `Subsystem`
4. 读构建属性（OutputType / Configuration / OutputPath / …）
5. 读元数据组
6. 派生变量（`$(TargetTriple)` / `$(Runtime)`）入 `State` 供 Condition

## 5. 版本与兼容（Project Version="1"）

- v1 内只做**加法**（新增属性/新 Backend 值），不删除既有键，已支持值不回退。
- 新增 Backend 值域（如未来 native-wasm）→ 协商 Version 升 2；旧解析器对未知值报"需要更新编译器"。
- 平台能力（linux native / arm RID）属**能力开关**，不触发 Version 变更（未启用报 NotSupported，保持 v1）。
- `Platform`→`Arch` 更名：兼容期同时接受 `Platform` 键（映射到 Cpu），`Arch` 优先。

## 6. 三 Backend 示例

```xml
<!-- ① native -->
<Backend>native</Backend>
<Arch>x64</Arch>
<TargetOS>windows</TargetOS>
<Subsystem>Console</Subsystem>

<!-- ② managed-fx -->
<Backend>managed-fx</Backend>
<TargetFramework>net48</TargetFramework>
<Arch>anycpu</Arch>
<Prefer32Bit>true</Prefer32Bit>

<!-- ③ managed-net -->
<Backend>managed-net</Backend>
<TargetFramework>net9.0</TargetFramework>
<Arch>anycpu</Arch>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

## 7. 消费链落点（T0 各步）

| 属性 | 落到 | 批次 |
|---|---|---|
| `Backend` 三值 | `Cocoa.Targeting/CodeBackend{Native,ManagedFx,ManagedNet}` + `ProjectEvaluator.ParseBackend` | T0a |
| `Arch` | `Cocoa.Targeting/Cpu{AnyCPU,X86,X64}` + `CocoaProjectFile.Cpu`（XML 键 `Arch`，兼容 `Platform`） | T0a |
| `TargetFramework` | `IlTarget(Runtime×Version×Cpu×Prefer32Bit)` 四维 | T0a |
| `Prefer32Bit` | `IlTarget.Prefer32Bit` → COR20 `32BITPREFERRED` | T0a |
| `TargetOS` | 删 `CocoaTargetOs`，并入 `TargetOs` | T0b |
| `RuntimeIdentifier` | `AppHostPatcher` 预留告警 | T0a/c |
| `Subsystem` | native PE 子系统（现状保留） | T0a |
| `$(TargetTriple)`/`$(Runtime)` | 派生变量入 `State` | T0b |

## 8. 自举侧与文档同步

- 自举侧（`src/Cocoa.Co`）本轮**不动**；`.co` 侧现有 `coproj` 声明保持。
- 实施完成后：本设计合入 `docs/项目格式规范.md` §2.2/2.3；`docs/ARCHITECTURE.md` 增目标模型章节。