# 委托 / Lambda / 闭包 / 事件 设计

> 状态：🔧 设计定稿（2026-08-24，6e-M22 规划，随 6e-M20 G6/G7 收尾同轮实施）
> 关联：`docs/泛型设计.md`（G6 stdlib / G7 `.cod` 泛型序列化，本轮合并推进）、`docs-dev/对象模型设计.md`（M4 native vtable 复用）、`docs/语法手册.md` §9.12/§15/§20
> 核心决策：**结构化函数类型为内核**（Kotlin/F# 路线），delegate 声明为纯语法糖；event 自研多播（三后端同构）；方言只在 Parser 分叉，语义层单一。

---

## 1. 目标与范围

| 能力 | 说明 | 提交 |
|------|------|------|
| 函数类型 `(A, B) -> R` | 一等类型：变量/字段/参数/返回/泛型实参位置全可用 | C2/C3 |
| Lambda 表达式 | 函数类型的字面量构造 | C2/C4 |
| 无捕获调用 | 函数值存储、传递、`f(x)` 间接调用 ×三后端 | C4 |
| 闭包捕获 | 局部变量/`this` 捕获 → 环境对象 | C5 |
| 事件 `event` | 多播订阅/退订/触发 | C5+ |
| delegate 声明糖 | 带名函数类型，双向隐式转换 | C5++ |

**明确不做**：`in`/`out` 型变注解（解析报诊断，泛型无运行时型变）、默认参数 lambda、表达式树、`?.Invoke` 空条件调用首版（判空由触发方显式写）。

---

## 2. 函数类型系统（C3）

### 2.1 符号

```
FunctionTypeSymbol : TypeSymbol
├─ ParameterTypes: ImmutableArray<TypeSymbol>
├─ ReturnType: TypeSymbol          // void 允许 → Action 形态
└─ 相等性: 结构化（逐参数类型 + 返回类型严格相等，不变型）
```

- 单例去重：工厂 `FunctionTypeSymbol.Get(params, returnType)` 缓存，同形状同实例（对齐 `TypeSymbol.ArrayOf`）。
- mangle（Encode v3 风格）：`(int, string) -> bool` → `Func$!System.Int32$!System.String__!System.Boolean`（参数 `$` 分隔、返回 `__` 后缀；`$`/`.`/`!` 均非标识符字符 ⇒ 结构隔离零禁令）。
- 数组/嵌套：`((int) -> int)[]`、`(int) -> ((int) -> int)` 递归合法。

### 2.2 位置覆盖

变量声明、字段、参数、返回类型、属性、泛型实参 `List<(int) -> int>`、cast/is/as、数组元素。**首版不变型**：`(Derived -> Base)` 不兼容 `(Base -> Derived)` 参数位置（无逆变）。

### 2.3 `.co` ↔ `.cs` 拼写（共享同一符号）

| 写法 | `.co` | `.cs` |
|------|-------|-------|
| 直接函数类型 | `(int, string) -> bool` | ❌ 进拒绝清单（C# 无此语法） |
| BCL 家族别名 | ✅ 同样可用 | `Func<int, string, bool>` / `Action<T1..>` / `Predicate<T>` |

**Func/Action/Predicate 内建家族**：编译器预合成的带名函数类型别名（元数 0~16，对齐 BCL）。实现 = 启动期以 C5++ 的 delegate 别名机制批量注册（非硬编码符号分支）：`Func<A,R> ≡ (A) -> R`。两方言均可用；`.cs` 主推家族拼写，`.co` 两写法并存。

### 2.4 using 别名

```
using Handler = (Object, string) -> void        // .co
using Handler = System.Action<Object, string>;  // .cs
```

现有 using 别名机制（6e-M18）右侧扩展接受函数类型/家族拼写；别名编译期局部，不入 `.cod` 导出。

---

## 3. Lambda 语法与解析（C2）

### 3.1 形态矩阵

| 形态 | `.co` | `.cs` |
|------|-------|-------|
| 显式参数 + 表达式体 | `(x: int, y: int) => x + y` | 同左 |
| 显式参数 + 块体 | `(x: int) => { return x * x }` | `(x: int) => { return x * x; }` |
| 无参 | `() => print("hi")` | 同左 |
| 单参免括号 | ❌（拒绝清单引导加括号） | `x => x * 2` |
| 隐式类型参数 | ❌（要求显式标注） | `(x, y) => x + y`（目标推导，见 §5.3） |

词法：`=>` FatArrowToken 已存在（表达式体成员复用）；新增 `->` ArrowToken（仅 `.co` 函数类型用，`.cs` 出现 `->` 报错）。

### 3.2 AST

```
LambdaExpressionSyntax : ExpressionSyntax
├─ ParameterList: SeparatedSyntaxList<ParameterSyntax>   // 复用；ImplicitlyTyped 标记
├─ ArrowToken
└─ Body: ExpressionSyntax | BlockStatementSyntax

FunctionTypeSyntax : TypeSyntax
├─ ParameterTypes: SeparatedSyntaxList<TypeClauseSyntax>
├─ ArrowToken（->）
└─ ReturnType: TypeClauseSyntax
```

括号歧义消解（Parser）：`(` 后前瞻——命中 `标识符: 类型` / `类型` 且随后 `)` + `=>`/`->` 则走 lambda/函数类型分支，否则普通括号表达式（与 G0 泛型 `<` 平衡前瞻同套路）。块体歧义：`=> {` 固定为语句块（表达式体不支持块值语义，与 C# 一致需 return）。

---

## 4. 绑定：匿名方法提升与方法组转换（C4）

### 4.1 提升

Lambda 在绑定期提升为**编译器生成的静态/实例方法符号**：

- `LambdaSymbol → FunctionSymbol`（mangle 名 `__Lambda$<序号>$<宿主名>`，序号按出现顺序全局分配，保证跨编译稳定）
- 显式参数类型直接落签名；隐式类型参数须存在期望函数类型（§5.3），否则诊断
- 体经 `BuildFunctionBody` 同管道绑定（捕获变量处理见 §6）
- 表达式体自动包 return（void 体除外）

`BoundLambdaExpression(syntax, FunctionSymbol, FunctionTypeSymbol)` —— 三后端见到它就构造函数值。

### 4.2 方法组转换

```
b.onClick += this.OnClick      // 实例方法 → 函数值（env = this）
let f: (int) -> int = Math.Abs // 静态方法 → 函数值（env = null）
```

- 触发点：期望类型为 FunctionTypeSymbol 的赋值/实参/返回/±= 位置
- 重载解析：按目标签名过滤候选，唯一则转换，否则歧义诊断
- 构造：`BoundMethodGroupConversion(receiver?, method)` → 发射期物化函数对象

### 4.3 期望类型推导（隐式类型 lambda）

绑定期下推 expected type：`BindCallExpression` 实参位、赋值右部、return 位、`+=/-=` 事件位。命中 FunctionTypeSymbol 时注入 `_expectedLambdaType`，隐式参数逐一取 `ParameterTypes[i]` 回填后再走 §4.1。无期望类型报 CS0748 式诊断。

---

## 5. 运行期表示 —— 三后端 ABI（C4 核心）

### 5.1 语义模型（唯一真相）

函数值 = **单方法接口对象**：

```
调用: r = f(a, b)
≡ 接口: interface Fn2<A,B,R> { R Invoke(A a, B b); }
```

三后端各自物化该模型，语义层只有一种调用形态（间接调用 + 环境槽）：

### 5.2 各后端载体

| 后端 | 载体 | Invoke 实现 | 环境槽 |
|------|------|------------|--------|
| **Evaluator** | 托管对象（`EvaluatorFunction`：FunctionSymbol + env 引用） | 直接解释提升方法体，env 入 this 栈 | 托管引用 |
| **IL** | **映射 `System.Func<…>` / `System.Action<…>`**（netfx/netcore 均内置） | `new Func<...>(target, methodRef)` + `callvirt Invoke` | 委托 target 槽 |
| **native** | 合成函数对象：`[typeId/vtable][fnptr][env*]` 三字布局（M4 对象头复用） | `call [obj.fnptr]`，约定 `(env, args...)` | env 指针 |

- **实例方法组**：`env = receiver`，fnptr = 方法静态入口（this 首参约定，M4c 栈 ABI 直接复用）。
- **静态方法/Lambda**：native env = null（0），fnptr 直指；IL target = null；Lambda 本身已是提升方法，无需二次包装类。
- **为什么 IL 不自研合成闭包类**：BCL Func 高度优化且 netfx/netcore 全覆盖；facade 类同理借 .NET 运行时（M19 既例）。语义同构由 §5.1 保证，反射可见名差异属 .NET 互操作面（反而更兼容）。
- **为什么 native 不用 qsort 风格裸 fnptr**：闭包需要 env；统一三元布局使无捕获/有捕获/方法组三种来源同形，调用点零分支。

### 5.3 调用发射

`BoundInvocationExpression(functionExpr, args)`：
- Evaluator：递归求值 → `EvaluatorFunction.Invoke`
- IL：`ldloc f; ldarg..; callvirt Invoke`
- native：`mov rax,[f+fnptr]; mov rcx,[f+env](win x64 this=rcx)；call rax`；x86 stdcall 栈传 env 首位
- 函数类型参数在 IR 层按引用传递（对象指针，同用户类）

---

## 6. 闭包捕获（C5）

### 6.1 分析与改写

- 收集阶段（绑定时）：lambda 体内引用的外层局部变量/参数 → 捕获集；含 `this`（成员访问隐式）
- 改写：被捕获变量声明点 → **环境字段**读写（`BoundEnvironmentAccess` 降级为字段访问）；声明初始化移入环境构造
- 环境 = 编译器合成类 `__Env$<宿主>$<序号>`（字段 = 捕获变量，普通用户类身份 → M4 vtable/G3 TypeDef/序列化全免费）
- 嵌套 lambda 共享同一环境（同作用域链合并）；跨作用域捕获外层 lambda 的环境字段直引

### 6.2 生命周期

环境对象随**首个捕获它的 lambda 创建点**构造（保守：变量声明处若被捕获即构造，语义等价因 Cocoa 无地址暴露）。foreach 循环变量只读（P6），无 per-iteration 环境分裂问题。

---

## 7. 事件 event（C5+）

### 7.1 语法

```
// .co
public class Button
{
    public event Click: (Object, string) -> void
    public function Fire(msg: string): void { Click(this, msg) }
}
b.onClick += (s, m) => print(m)
b.onClick -= handler            // 引用相等移除单个

// .cs
public event Action<Object, string> Click;
public event EventHandler Registered;         // 经 C5++ 别名
button.Click += (s, e) => { ... };
```

### 7.2 符号与绑定降级

- `EventSymbol(Name, HandlerType: FunctionTypeSymbol, Visibility, IsStatic)` → `ClassTypeSymbol.Events`
- 绑定期降级（同 foreach/switch 惯例）：
  - `e += f` / `e -= f` → `BoundEventSubscription(accessor=add/remove)`
  - 类内 `e(args)` → 判空 + **快照遍历**逐个调用
  - **外部禁读禁调**（CS0070 对齐）：仅允许 `+=`/`-=`，违者诊断
- 访问器式 `{ add {} remove {} }`：解析先行（AST 就绪），绑定首版报"未实现"，留后续
- 隐藏状态：发射期为每事件合成函数值**数组字段**（初值 null）；add 尾插、remove 按引用相等移除首个匹配；触发前判空 no-op（M5 null 语义）
- **自研多播，不用 .NET MulticastDelegate**：native 无 .NET，三后端必须同构（M19 vtable/M20 单态化同一原则）；IL 侧数组存 Func 引用即可

### 7.3 语义边界

- `-=` 移除按**函数对象引用相等**：匿名 lambda 每次 new ⇒ "自订自退"无效（C# 行为一致，文档化）；具名方法组每次转换也产生新对象 ⇒ 退订须保存原引用
- 静态事件/虚事件/override 事件：后置
- 泛型类内事件 `event OnChange: (T) -> void`：随单态化展开（C7 替换管道零特判）

---

## 8. delegate 声明语法糖（C5++）

```
// .co                              // .cs
delegate void Handler(Object, string)   public delegate void Handler(Object sender, string msg);
delegate T Selector<in T>(T item)       public delegate T Selector<in T>(T item);   // in/out 解析后报诊断
```

- 语义：声明 = 注册**带名的 FunctionTypeSymbol 别名**（`ClassTypeSymbol.DelegateAliases` / 命名空间级表）
- 双向隐式转换：`Handler h = ...` 与 `(Object,string)->void` 互通（结构相等即兼容）
- 可用于事件类型、字段、API 签名；发射层复用 §5 ABI 零新概念；`.cod` 增 `dlgalias` 节点导出
- 命名空间级与类内嵌套两级；泛型 delegate 别名 = 类型参数化模板，实例化走既有 Instantiator 缓存

---

## 9. `.cod` 序列化咬合（C6/C7，并入 G7）

- 新节点：`fnty`（函数类型）、`evt`（EventSymbol）、`dlgalias`、lambda 提升方法 = 普通 `fn`（mangle 名稳定）、环境/合成类 = 普通 `cls/gcls`
- 开放泛型体内的 lambda/事件：T 不透明序列化，消费方 BoundTreeSubstituter 全节点替换（fnty 参数/返回一并替换）
- 版本硬升级；读侧拒旧库提示重建

---

## 10. 实施里程碑

| 里程碑 | 内容 | 依赖 |
|--------|------|------|
| C2 语法 | `->` 词法 + Lambda/FunctionType AST + 歧义消解 + 门禁诊断 + `.cs` 拒绝清单 | — |
| C3 函数类型系统 | FunctionTypeSymbol + 工厂缓存 + mangle + 位置覆盖 + using 别名 + Func/Action/Predicate 家族 | C2 |
| C4 无捕获 ⚠️ | 设计评审 → 提升管道 + 方法组转换 + 三后端 ABI（§5）+ e2e | C3 |
| C5 闭包 ⚠️ | 捕获分析 + 环境类合成 + 变量改写 + 三后端 + e2e | C4 |
| C5+ 事件 | EventSymbol + 降级绑定 + 多播数组三后端 + e2e | C5 |
| C5++ delegate 糖 | 别名注册 + 双向转换 + in/out 诊断 + e2e | C5+ |
| （G7 并轨） | fnty/evt/dlgalias 入 CodSerializer（C6）+ Substituter 覆盖（C7） | C5++ |

## 11. 验收标准

- `(int) -> int` 变量存取/传参/返回 ×三后端输出一致
- 闭包计数器（§语法手册 20.3 用例）×三后端输出 `1` `2`
- 事件订阅两个 handler 触发两次；退订其一触发一次；空事件触发 no-op
- `delegate void Handler(...)` 与结构类型互转；`Func<int,bool>` 与 `(int) -> bool` 同一符号（is/as/== 判型一致）
- `.cs` 方言：Func 家族 + 免括号 lambda + 隐式类型 lambda 全通过；`->` 与 `in/out` 报明确诊断
- 全量回归绿；文档同步（§15/§20 转 ✅）
