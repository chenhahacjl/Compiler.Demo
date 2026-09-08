# Stage 5: 语言特性补全路线图

> **日期**：2026-09-08 | **起点**：43,650 测试 green | **目标**：补全 C# 高频缺失特性
> **当前**：44,629 测试 green，Phase 1-3 完成

---

## 已完成

| 特性 | 提交 | 日期 |
|------|------|------|
| `??` null 合并 | `58e505a` | 2026-09-08 |
| `??=` null 合并赋值 | `58e505a` | 2026-09-08 |
| `nameof` 编译期字符串常量 | `c37fe3a` | 2026-09-08 |
| `is` 常量模式 (`is null` / `is "hello"`) | `f6896cb` | 2026-09-08 |
| 二进制字面量 `0b` + 数字分隔符 `_` | `70424a1` | 2026-09-08 |

---

## Phase 1: `nameof` 运算符

**复杂度**：低 | **后端改动**：无（→ 字符串字面量）

### 实现步骤

1. **SyntaxKind**：shared + 两方言添加 `NameofKeyword` token
2. **Lexer**：`nameof` 识别为关键字
3. **SyntaxNode**：新建 `NameofExpressionSyntax`（持有参数表达式）
4. **Parser**：`ParsePrimaryExpression` 中解析 `nameof(expr)`
5. **Binder**：解析参数符号名 → `BoundLiteralExpression(name, TypeSymbol.String)`
6. **BoundNodeKind**：添加 `NameofExpression`
7. **BoundTreeRewriter / BoundNodePrinter / Serialization**：添加对应 case
8. **Evaluator**：`EvaluateNameofExpression` 返回字符串
9. **测试**：解析、绑定、三后端求值

### 语义

- `nameof(x)` → `"x"`（编译期常量）
- `nameof(obj.Method)` → `"Method"`
- `nameof(T)` → `"T"`（泛型类型参数名）
- 参数可以是任意表达式，但仅名称部分被提取，不求值

---

## Phase 2: 常量模式 `is`

**复杂度**：低-中 | **后端改动**：无（→ 二元比较）

### 实现步骤

1. **Parser**：`is` 后接受 `null`、整数字面量、字符串字面量（不限于标识符）
2. **SyntaxNode**：扩展 `IsExpressionSyntax` 或新建 `ConstantPatternSyntax`
3. **Binder**：
   - `is null` → `BoundBinaryExpression(expr, ReferenceNotEquals, null)`
   - `is 0` → `BoundBinaryExpression(expr, EqualsEquals, 0)`
   - `is "hello"` → `BoundBinaryExpression(expr, EqualsEquals, "hello")`
4. **测试**：`is null`、`is 0`、`is "hello"`、类型仍走原有路径

---

## Phase 3: 二进制字面量 / 数字分隔符

**复杂度**：低 | **后端改动**：无（词法层）

### 实现步骤

1. **Lexer**：
   - 识别 `0b`/`0B` 前缀 → 二进制解析
   - 识别 `_` 作为数字分隔符（解析时跳过）
2. **组合**：`0b1010_0101` → 165
3. **测试**：`0b1010` → 10、`1_000_000` → 1000000、`0xFF` 不受影响

---

## Phase 4: `using` 声明

**复杂度**：中-高 | **后端改动**：无（→ try/finally）

### 实现步骤

1. **Parser**：`using var x = expr` 作为局部声明语句
2. **SyntaxNode**：新建 `UsingDeclarationSyntax`
3. **Binder**：绑定资源表达式，解析 `.Dispose()` 方法
4. **Lowerer**：重写为 `try { var x = expr; ... } finally { if (x != null) x.Dispose(); }`
5. **测试**：基本 using、嵌套 using、null 资源、自定义 Dispose

---

## Phase 5: `?.` 空条件运算符

**复杂度**：高 | **后端改动**：三后端均需

### 实现步骤

1. **Lexer**：添加 `QuestionDotToken`（`?.`）
2. **Parser**：`expr ?. member` / `expr ?. method(args)` / `expr ?[index]`
3. **SyntaxNode**：新建 `ConditionalAccessExpressionSyntax`
4. **BoundNode**：新建 `BoundConditionalAccessExpression`
5. **Binder**：确定可空结果类型，绑定内部访问
6. **Lowerer**：`expr?.member` → `var __t = expr; __t != null ? __t.member : default`
7. **三后端**：评估/发射降级后的形式
8. **链式支持**：`a?.b?.c`
9. **与 `??` 交互**：`a?.b ?? fallback`
10. **测试**：null 接收者、非空、链式、与 `??` 组合、索引器变体

---

## 统计

| Phase | 特性 | 复杂度 | 预估工时 | 后端 |
|-------|------|--------|----------|------|
| 1 | `nameof` | 低 | 1-2h | 无 |
| 2 | 常量模式 `is` | 低-中 | 1-2h | 无 |
| 3 | 二进制字面量 / 分隔符 | 低 | 0.5-1h | 无 |
| 4 | `using` 声明 | 中-高 | 2-4h | 无 |
| 5 | `?.` 空条件 | 高 | 4-8h | 三后端 |
