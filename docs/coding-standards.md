# C# 编码规范（Coding Standards）

> 本项目 C# 编码规范基准。**单一事实源 = 本文件**（索引与规则见根 `AGENTS.md`「编码约定」）；「procedure 进 cookbook、contract 进 README」，本文件为规范。

## 采用的标准

以 **dotnet/runtime 的 C# Coding Style** 与 **Microsoft .NET C# Coding Conventions** 为基准：

- [dotnet/runtime · C# Coding Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)——生产级、配 `.editorconfig` + `dotnet format` 可强制。
- [Microsoft Learn · .NET C# Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)——命名/格式/语言惯用。
- [Microsoft Learn · .NET Framework Design Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/)——API/命名/类型设计（面向公共 API）。

仓库根 `.editorconfig` 据此编码（IDE/Roslyn 自动格式化与提示；本仓库未把 `dotnet format` 纳入 CI 门禁，以 `suggestion` 引导为主，既有文件风格优先）。

## 关键约定（摘要）

**格式**
- 4 空格缩进，不用制表符。
- Allman（K&R 之外的 Allman）花括号：开括号独立成行。
- 文件作用域 namespace（`namespace X;`）。
- `using` 置于 namespace 之外、按字母序、`System.*` 置顶。
- 每文件 UTF-8、LF 换行、末尾换行。

**命名（dotnet/runtime）**
- 私有/内部实例字段 `_camelCase`；静态字段 `s_` 前缀；线程静态 `t_` 前缀。
- 常量、方法、本地函数、类型全部 PascalCase。
- 公共 API 用 XML doc（`<summary>/<param>/<returns>`）。
- 用语言关键字而非 BCL 类型（`int` 而非 `Int32`、`string` 而非 `String`）。
- 用 `nameof(...)` 而非字符串字面量。

**语言惯用**
- `var` 仅在类型于右侧显式命名（`new`/显式转型）时使用；目标类型 `new()` 仅在类型于左侧显式命名时使用。
- 用 `Func<>`/`Action<>`；用 `&&`/`||`（短路）而非 `&`/`|`。
- 字符串插值；循环追加用 `StringBuilder`；优先原始字符串字面量。
- 集合表达式（`[ "a", "b" ]`）、对象/集合初始化器。
- 空引用：`Nullable` 已启用；公共 API 文档化可空语义。
- 只 catch 能妥善处理的异常，避免 catch 通用 `Exception`。

**注释**
- 简短说明用单行 `//`；注释独立成行、首字母大写、以句点结尾、`// ` 与正文间一空格。
- 公共成员用 XML 注释，不用块注释。

## 与文档结构的关系

- 规范细化（本文件）；踩坑判别见 [`docs/cookbook.md`](cookbook.md)；架构见 [`docs/architecture.md`](architecture.md)。
- 规则（如 fail loud、强类型跨界 ID）在根 [`AGENTS.md`](../AGENTS.md)「编码约定」。
