# Cocoa 完整 OOP 设计（M5）

> 状态：设计中（M5）
> 目标：在 M2 class 语言（字段/构造/方法/new/this/可见性）基础上，补齐完整 OOP：**继承、构造链、多态、static、readonly、显式 this.、属性**。
> 相关文档：`docs/类库设计.md`（§4 class 语言）、`docs/语法手册.md`（§12 类与面向对象、§14 属性）
> 最后更新：2026-08-19

---

## 1. 目标与范围

| 项 | 状态 |
|----|------|
| 继承（单继承）、`base(...)`/`this(...)` 构造链 | 本次 |
| 多态：`virtual`/`override`/`abstract`/`sealed` + `base.Method()` | 本次 |
| `static`（类/方法/字段）、`readonly` 字段、显式 `this.` | 本次 |
| 属性：完整属性 + 自动属性（§14） | 本次 |
| 嵌套类（§12.8）、部分类（§12.9）、接口（§13）、`internal`/`protected` | 后置 |

后端：**IL 后端**（CLR 引用类型天然支持继承/虚分派）；native 对象模型整体后置（§9）。

---

## 2. 继承

### 2.1 语法

```
public class Circle: Shape
{
    private _radius: int
    ...
}
```

- `class` 名后 `: <BaseTypeName>` 声明基类（TypeClause，仅类类型）
- 单继承；基类必须是类类型（非静态类、非 sealed 类）
- 未声明基类 → 隐式 `System.Object`（与现有一致）

### 2.2 符号

`ClassTypeSymbol.BaseType`（`ClassTypeSymbol?`，null = System.Object）。成员解析优先级：**派生类自身成员 → 基类成员（沿链向上）**（遮蔽：子类同名成员隐藏基类）。

### 2.3 绑定

- 基类名解析：`LookupType` → `ClassTypeSymbol`
- 诊断：循环继承（`A:B` 且 `B:A`）、基类未定义、继承静态类/sealed 类、基类为抽象类且子类未实现抽象成员

### 2.4 IL 映射

TypeDef 的 Extends = 基类 TypeDef（本程序集）/TypeRef（外部）。虚继承运行时天然支持。

---

## 3. 构造链

### 3.1 语法

```
public class Dog: Animal
{
    public constructor(): base("dog") { }
    public constructor(initial: int): this(0) { }   // 委托本类构造
}
```

- 构造签名后 `: base(...)` / `: this(...)`（可选的构造初始参数列表）
- 子类构造必须（直接或经 this(...)）调用基类构造，否则隐式 `base()`（基类需有 0 参构造）
- `base(...)` 不能与 `this(...)` 同时出现

### 3.2 绑定

- 构造链解析为基类/本类构造函数的参数匹配
- 诊断：无匹配构造、`base(...)`+`this(...)` 同现、隐式 `base()` 无 0 参构造

### 3.3 IL 映射

子类 `.ctor` 方法体**开头**先 `call` 基类/本类 `.ctor`（在字段赋值前）。无显式 base/this 时隐式 `call base::.ctor()`。

---

## 4. 多态

### 4.1 修饰符语义

| 修饰符 | 类 | 方法 | 语义 |
|--------|:--:|:----:|------|
| `virtual` | ❌ | ✅ | 可被派生类重写 |
| `override` | ❌ | ✅ | 重写基类 `virtual`/`abstract` 方法（同签名） |
| `abstract` | ✅ | ✅ | 抽象类不可实例化；抽象方法无方法体，派生类必须实现 |
| `sealed` | ✅ | ✅ | sealed 类不可继承；sealed 方法不可再被重写 |

### 4.2 绑定

- `override` 必须在基类找同签名 `virtual`/`abstract` 方法（否则报错）
- `abstract` 类可含非抽象成员；非抽象类含 `abstract` 成员 → 报错
- 非抽象派生类必须实现全部继承的 `abstract` 成员
- `sealed` 方法必须是 `override`

### 4.3 IL 映射

- `virtual` 方法：MethodDef flags `Virtual(0x40)|NewSlot(0x100)`
- `override` 方法：MethodDef flags `Virtual(0x40)`（**不加 NewSlot**，占据基类 slot）
- `abstract` 方法：flags 加 `Abstract(0x400)`，无方法体（RVA=0）
- `sealed` 方法：flags 加 `Final(0x20)`
- 调用：实例方法统一 `callvirt`（虚分派）；`base.Method()` → `call`（非虚，直接调基类方法，绑定到基类 slot）
- 外部类型方法调用：`callvirt`（已有 MemberRef 路径）

### 4.4 `base` 关键字

- `base.Method(...)`：非虚调用基类方法（IL `call` 而非 `callvirt`）
- 仅能在实例方法/构造内使用
- `base` 不能作为值传递（仅限成员调用）

---

## 5. static

### 5.1 语法

```
public static class MathHelpers
{
    public static function Square(x: int): int { return x * x }
    public static _version: int
}
```

- `static class`：仅含静态成员、不可实例化、不可继承/被继承
- `static function` / `static` 字段：属类型而非实例
- 访问：`MathHelpers.Square(2)`（类型名.成员）；类内可直接 `Square(2)`

### 5.2 绑定

- 静态成员解析：类型名 → 类静态成员表
- 静态上下文（静态方法内）禁 `this`、禁实例字段/方法引用
- 实例上下文可访问静态成员（类型名.成员）

### 5.3 IL 映射

- 静态方法：MethodDef `static`（无 HAS_THIS），调用用 `call`
- 静态字段：FieldDef `static(0x10)`，访问用 `ldsfld`/`stsfld`
- 静态类：TypeDef `Abstract|Sealed`（0x00000080|0x00000100）

---

## 6. readonly 字段

- `readonly` 字段：仅构造函数内可赋值（构造结束后只读）
- 绑定：赋值点在构造外 → 诊断（复用 `ReportCannotAssign`）
- IL：无特殊（CLR 不强制；编译期校验足够）

---

## 7. 显式 this.

- `this.x` 显式实例字段/方法引用（与参数重名时必需）
- 语法：现有 `MemberAccessExpression`（`this` 解析为 `BoundThisExpression`）
- 仅实例上下文可用；静态上下文报错
- 与现有隐式字段引用（构造/方法内裸 `_x`）并存

---

## 8. 属性

### 8.1 完整属性（§14.1）

```
public class Person
{
    private _name: string

    public property Name: string
    {
        get { return _name }
        set { _name = value }
    }
}
```

- 块内 `get {...}`（返回类型）+ `set {...}`（`value` 为隐式参数）；可省略其一（只读/只写）
- `value`：setter 内表示待赋值的隐式参数（类型 = 属性类型）

### 8.2 自动属性（§14.2）

```
public property Name: string { get; set }
public property Age: int { get }
```

- 编译器生成后备字段 `_Name` + `get_Name`/`set_Name`；`get;` 无 setter = 只读

### 8.3 绑定与访问

- 属性符号：`PropertySymbol`（类型 + getter/setter）
- `obj.Name` 读 → getter 调用；`obj.Name = v` 写 → setter 调用（setter 返回 void）
- 只读属性（无 setter）赋值 → 诊断

### 8.4 IL 映射

- Property 表行：`Name` + PropertyAttributes + Type（PropertySignature）+ getter/setter MethodDef 引用
- `get_Name`：实例方法返回 `T`；`set_Name`：实例方法参数 `T` 返回 void
- 访问：`callvirt get_Name` / `callvirt set_Name`

---

## 9. 对象模型与 native 后置

- IL 后端：CLR 引用类型 + 继承 + 虚分派，天然支持（无需自研布局）
- native 后端：对象布局/虚表/所有权仍后置（`docs/类库设计.md` §8），class 程序在 native 后端继续编译期拒绝

---

## 10. 测试验收

| 场景 | 验证 |
|------|------|
| 继承层次 | `Shape`→`Circle` 字段/方法解析、遮蔽 |
| 构造链 | `base(...)`、`this(...)`、隐式 `base()`、无 0 参构造报错 |
| 多态 | 基类引用调派生实现（callvirt）、`base.Method()` 非虚 |
| static | 类型名.成员、静态字段、静态上下文禁 this |
| readonly | 构造外赋值诊断 |
| 属性 | 完整/自动属性读写、只读属性赋值诊断 |
| C# 互操作 | C# 消费含继承/属性的 Cocoa dll（基类引用、属性访问） |
| 诊断 | 循环继承、抽象实例化、非虚 override、sealed 继承 |
