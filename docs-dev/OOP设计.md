# Cocoa 完整 OOP 设计（M5）

> 状态：已实现（M5）+ 6e-M10 扩展 + **6e-M19 已全部落地（2026-08-24：Object 基类 / 全类型成员方法 / System.Type / native 对象模型 / null·is·as / `==` 语义收尾，见 `docs/对象模型设计.md`）**
> 目标：在 M2 class 语言（字段/构造/方法/new/this/可见性）基础上，补齐完整 OOP：**继承、构造链、多态、static、readonly、显式 this.、属性**。
> 相关文档：`docs/类库设计.md`（§4 class 语言）、`docs/语法手册.md`（§12 类与面向对象、§14 属性、§44 C# 兼容语法）、**`docs/对象模型设计.md`（System.Object 基类 / 全类型成员方法 / System.Type / native vtable 对象模型）**
> 最后更新：2026-08-24

---

## 1. 目标与范围

| 项 | 状态 |
|----|------|
| 继承（单继承）、`base(...)`/`this(...)` 构造链 | ✅ |
| 多态：`virtual`/`override`/`abstract`/`sealed` + `base.Method()` | ✅ |
| `static`（类/方法/字段/构造）、`readonly` 字段、显式 `this.` | ✅ |
| 属性：完整属性 + 自动属性（§14）、访问器修饰符、表达式体、初始化器 | ✅ |
| 接口（声明/实现/继承/类多接口）、部分类（§12.9）、`internal`/`protected` | ✅ |
| 嵌套类（§12.8） | 后置 |
| System.Object 基类 + Object 方法（ToString/GetHashCode/Equals/GetType + 静态 Equals/ReferenceEquals） | 🔧 `docs/对象模型设计.md` §3 |
| 全类型成员方法（`1.ToString()` / `"abc".Substring(0,2)` / `arr.Sum()`） | 🔧 `docs/对象模型设计.md` §4/§5 |
| System.Type 类型对象 | 🔧 `docs/对象模型设计.md` §6 |
| native 用户类对象模型（真 vtable 虚分派） | 🔧 `docs/对象模型设计.md` §8 |

后端：**IL 后端**（CLR 引用类型天然支持继承/虚分派）；native 对象模型由"整体后置"转为**6e-M19 规划落地**（`docs/对象模型设计.md` §8 真 vtable 设计）。

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

- 构造签名后 `: base(...)` / `: this(...)`（可选的构造初始参数列表）；前缀支持 `:` 或 `extends`（`constructor(...) extends base(...)` 等价）
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

### 8.5 自动属性初始化器（6e-M10）

```
public int X { get; set; } = 42;      // C# 式
public property Y: int { get set } = 10   // Cocoa 式
```

- 初始化器作用于后备字段 `_X`/`_Y`，在**每个实例构造的构造链之后**执行赋值
- 只读自动属性 `public int X { get; } = 5;` 同样支持

---

## 9. 字段初始化器与 .cctor（6e-M10）

```
private int _x = 5;                    // 实例字段初始化器（C#/Cocoa 式均可）
public static int Max = 100;           // 静态字段初始化器
public readonly int Id = 42;           // readonly + 初始化器（构造内赋值合法）
```

- **实例字段**：初始化赋值注入每个实例构造函数（显式 + 隐式），位于 `base()`/`this()` 构造链**之后**、函数体**之前**
- **静态字段**：编译器合成 `.cctor` 静态构造器（`FunctionSymbol{IsConstructor, IsStatic}`），按声明顺序初始化，首次静态访问/实例化前运行；无静态初始化器时不生成
- **初始化顺序**：`base()` → 字段初始化器 → 构造函数体
- **IL 映射**：`.cctor` 方法名 + `SpecialName|RTSpecialName`，MethodDef 归属所属类 TypeDef；静态方法/构造器的 `declaringType` 判据统一为 `ContainingClass != null`（修复静态方法归属 Program TypeDef 的元数据缺陷）
- 显式构造无显式链时，若基类有 0 参构造则隐式注入 `base()`（保证继承下字段初始化顺序正确）

---

## 10. C# 兼容语法（6e-M10，见 `语法手册.md` §44）

成员声明支持 **C# 式类型前置写法**与 Cocoa 式混用：字段 `private int _x;`、方法 `public int Area() {...}`、属性 `public string Name { get; set; }`、构造函数 `public Point(int x)`、参数 `int x`/`int[] arr`、局部变量 `int x = 10;`、语句分号可选、接口成员 `int Area();`。解析归一为同一批语法节点，绑定/发射共用管道。

---

## 11. 对象模型与 native 后端

- IL 后端：CLR 引用类型 + 继承 + 虚分派，天然支持（无需自研布局）
- native 后端：**6e-M19 规划落地**——用户类对象模型（对象头 vtable 指针 + 8 字节对齐字段、真 vtable 虚分派、实例方法 `this` 隐藏首参、`new`=Alloc+构造链），见 `docs/对象模型设计.md` §8；class 程序不再编译期拒绝。

---

## 12. 测试验收

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

---

## 13. 对照 C# 未实现特性清单

> 更新：2026-08-22。基准：`docs/语法手册.md` 状态标记（✅ 已实现 · 🔧 设计中 · 📋 待实现）与 `docs/开发计划.md` P8+ 路线。用途：OOP 后续补齐路线图。

### 13.1 已实现基准

| 特性 | 状态 |
|------|------|
| 类 / 单继承 / 多接口（`class Foo: Bar, IA, IB`） | ✅ |
| 构造链 `base(...)` / `this(...)`（含隐式 `base()`、`extends` 前缀） | ✅ |
| 多态 `virtual` / `override` / `abstract` / `sealed` + `base.Method()` 非虚调用 | ✅ |
| `static`（类 / 方法 / 字段 / 构造函数 → `.cctor`） | ✅ |
| `readonly` 字段、显式 `this.` | ✅ |
| 属性：完整 / 自动 / 访问器修饰符 / 表达式体 / 初始化器 | ✅ |
| 字段初始化器（实例注入构造、静态合成 `.cctor`） | ✅ |
| 接口（声明 / 实现 / 继承 / 类多接口） | ✅ |
| 部分类（多文件合并、可见性/基类一致性诊断） | ✅ |
| 可见性 `public` / `internal` / `protected` / `private` | ✅ |
| C# 兼容拼写（`.cs` 严格方言 + `.co` 纯 Cocoa，双前端共享管道） | ✅ |

### 13.2 未实现清单

#### 类型层面

| 特性 | 状态 | 说明 |
|------|------|------|
| 泛型类 / 接口 / 方法 + 约束 | 🔧 `docs/泛型设计.md` | **设计已定稿（2026-08-22，6e-M20）**：编译期单态化（模板式特化），解锁泛型集合 `List<T>` 与枚举器模式 foreach；连锁解锁委托/模式匹配 |
| `struct` / `record` / `record struct` | 📋 §32/§33 | 值类型 + 记录语义 |
| 嵌套类 | 🔧 §12.8 | P8+ |
| 接口默认方法、接口 `static abstract` / `static virtual` 成员 | — | 对象模型扩展，依赖泛型体系 |

#### 成员层面

| 特性 | 状态 | 说明 |
|------|------|------|
| 索引器 `this[int]` | 🔧 §31 | P8+ |
| 运算符重载 `operator +` 等 | — | P8+ |
| 事件 `event` + 自定义 `add` / `remove` 访问器 | 🔧 §15 | P8+ |
| 显式接口实现 `I.Method()` | 🔧 §13.4 | 现靠类公开同名成员匹配实现 |
| `abstract` 属性 | — | 现仅 `virtual`/`override` 属性 ✅ |
| 析构函数 `~Foo()` | — | — |
| `new` 遮蔽修饰符（显式隐藏基类成员） | — | 现仅同名遮蔽（隐式 newslot） |
| 组合可见性 `protected internal` / `private protected` | — | 现仅 4 级独立可见性 |

#### 表达式 / 参数层面

| 特性 | 状态 |
|------|------|
| 对象初始化器 `new P { X = 1 }`、集合初始化器 | — |
| 命名参数、可选参数、`params`、`in` | 🔧 §30（后置切片） |
| `ref` / `out` 参数 | ✅ 6e-M23（含普通参数可赋值与明确赋值分析；lambda 捕获禁止 v1） |
| `is` / `as` 类型测试、模式匹配 | 🔧 §27/§9.11 |
| 委托 `delegate` / Lambda / 匿名方法 | 🔧 §11.3/§20 |
| 扩展方法 | — |

#### 其他（C# 对象模型相关）

| 特性 | 状态 |
|------|------|
| 主构造函数、`init` 访问器、`required` 成员、`with` 表达式 | 📋 §33 |
| 引用相等语义、`Equals` / `GetHashCode` / `ToString` 重写 | 🔧 `docs/对象模型设计.md` §3/§4（Object 基类 + 成员面规划，6e-M19） |
| `==`/`!=` 多态语义（当前运算符按类型静态绑定，无引用比较） | 🔧 `docs/对象模型设计.md` §9（类引用相等对齐规划，6e-M19） |
| native 用户类对象模型（对象布局/vtable/虚分派） | 🔧 `docs/对象模型设计.md` §8（真 vtable 规划，6e-M19） |

### 13.3 实施优先级

1. **泛型**（6e-M20，设计已定稿）：泛型类/接口/方法 + 约束（编译期单态化），解锁泛型集合 `List<T>` 与枚举器模式 foreach——见 `docs/泛型设计.md`。
2. **嵌套类**：纯语法/符号层扩展，风险低。
3. **索引器 / 运算符重载 / 事件**：成员级特性，改动集中于 Parser/Binder + IL 元数据。
4. **显式接口实现 / 抽象属性**：接口体系补全。
5. **对象初始化器、命名/可选参数**：语法糖，绑定层降级即可接入。
