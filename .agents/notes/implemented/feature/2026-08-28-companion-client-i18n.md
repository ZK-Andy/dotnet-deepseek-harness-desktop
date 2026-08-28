# Agent Note: companion-client-i18n

Status: implemented

## Problem

`dsh-desktop-companion` 的客户端半部（`client/client.js`）把全部 UI 文案硬编码为中文（一个 `STR` 常量对象 + 若干组标题字面量），从不接入 dsh web 核心里经 `@deepseek-ai/dsh-client-locale` 承载的中英文切换机制。用户实测：在同一 dsh 界面切到英文后，**插件市场（dshmarket）等用上 locale 机制的 client 插件随之切换**，而**桌面设置（companion）仍固定中文**。这不是机制对 companion 失效，而是 companion 把自己排除在该机制之外。

## Decision

让 companion 客户端接入 dsh 的 `ctx.locale` 机制，完全按 dshmarket 的已实证模式（非推断）改造：

1. **`package.json` 的 `dsh.client.inject` 追加 `"@deepseek-ai/dsh-client-locale"`**（保证 locale 服务先于本插件激活；`platform:"web"` 与既有 `inject:[]→["@deepseek-ai/dsh-client-locale"]` 不变其他键）。companion version bump **0.0.13→0.0.14**（功能变更，随包闭包版本感知升级）。
2. **`client.js` Cordis `inject` 数组从 `['slots']` 扩为 `['slots','locale']`**（否则 `ctx.locale` 被 Cordis 拒绝）。file 顶部工厂返回 `{ apply, inject }` 同步改。
3. **硬编码文案改为 `zh`/`en` 双字典**：把 `STR` 对象与散落的组标题（`更新`/`诊断`/`桌面`）、`DIAG` 对象、`UpdateButton` 的 title/aria-label 全部转为 keyed 字典；`const zh={...}` / `const en={...}`（en 为对应英文）。`client.js` 是纯 JS（无 .ts 类型），字典 key 用扁平字符串、模板占位 `{name}` 格式与 dsh `t()` 运行时一致（本插件文案实际不使用占位符）。
4. **注册 + 绑定**：`apply(ctx)` 内 `ctx.effect(() => ctx.locale.register(NS, { zh, en }), "...")`、`const t = ctx.locale.bind(NS)`（NS = `"desktop-companion"`）。`register` 用**双参对象形式** `register(ns, { zh, en })`——命名空间 `"desktop-companion"` 不进 `@deepseek-ai/dsh-client-ui-slots` 的 `LocaleNamespaceMap` 明文合并表，故不触发 typed 键校验，运行时按对象形式注册（dshmarket 的 `"dsh-market"` 亦如此）。
5. **slot 注册声明 `locale: NS`，`label` 用 thunk**：两处 `ctx.slots.register` 的 options 补 `locale: NS`；`label` 从 `function(){ return '<中文>' }` 改为 `function(){ return t('label') }`（函数 thunk，读调用时刻语言）。**正是 `locale: NS` 让 renderer 经 `standardKit` 的 `kit["t"] = localeSeat(face, ns)` 自动注入 `t` 到组件 props，并经 `SlotOutlet.useLocaleRevision(host.locale)` 订阅 revision——切语言时整个 slot 子树重渲染、`t` 重派生**。故组件无需自己订阅 locale，只需从 `props.t` 取翻译函数。
6. **组件用 props.t**：各渲染文案的函数组件读 `var th = props.t || function(k){return k}`（`props.t` 为 renderer 注入、revision 跟随的翻译函数；无 t 时退化显示 key）。组件不自行 `useSyncExternalStore` 订阅 locale——重渲染由 renderer 的 `SlotOutlet` / `useLocaleRevision` 驱动。

配套：`docs/architecture.md` 补一句伴生插件客户端文案已接入 dsh client i18n（现状，见 Related 与同文件 L65 描述）。

## Alternatives considered

- **依赖 slot 渲染器"自动重派生 t"**（只声明 `locale: NS`，不额外传 `locale`/不自行订阅）：初期标"较强推断/存疑"（未实证 renderer 行为），三路评审时由 R2 读取 renderer 源码实证——`standardKit` 的 `kit["t"] = localeSeat(face, ns)` 自动注入 `t`、`SlotOutlet` 的 `useLocaleRevision(host.locale)` 自动订阅 revision 并重渲染整个 slot 子树。**采纳**：终态即此机制——组件只读 `props.t`、不自订阅，消除了初版"显式传 t/locale + 组件自订阅 revision"的重复通道（渲染器机制已覆盖，冗余非必要）。
- **保持中文硬编码，仅在设置里提供切换开关**：落败——dsh 已有全局 locale 机制，companion 自建一套语言开关与之割裂，也不随系统语言；与用户"跟随 dsh 中英文切换"的目标相悖。
- **为 companion 加 `*.i18n.yaml` 资源文件**：落败——dsh 运行时的语言表是构建后内联的 JS 字典（`ctx.locale.register`），`*.i18n.yaml` 只是 README 双语一致性校验记录，非运行时资源；client 插件不靠该类文件。
- **只在组件内改用 `t()` 而不声明 `inject`/`locale`**：落败——不声明 Cordis `inject:['locale']` 则 `ctx.locale` 访问被拒；不声明 slot 的 `locale` 则组件 props 拿不到 `t`。

## Consequences

- companion 客户端文案随 dsh 语言切换（中⇄英）实时变化；语言状态持久化与 `dsh-client-locale` 复用，不新增持久化面。
- 所有 UI 文案移到 `zh`/`en` 字典集中管理，后续新增文案需双语成对提供（与 dsh 插件惯例一致）。
- `plugins/dsh-desktop-companion` 的 `package.json`/`client.js` 变更使 `companion_sha` 变化 → 闭包签名 miss、随下次发版重建并版本感知升级；0.0.14 经发布替换用户 profile 内副本。
- 组件重渲染路径从"读一次 STR 后冻结"变为"依赖 renderer 的 `SlotOutlet.useLocaleRevision` 订阅 revision 驱动重渲染"：切语言时整个 slot 子树重渲染、`props.t` 重派生。组件不自订阅 locale，仅在 `props.t` 缺失时退化显示 key。

## Related

- dsh client i18n 机制（调研实证源）：`@deepseek-ai/dsh-client-locale` 的 `ctx.locale.register/bind/getSnapshot/subscribe`、slot `locale` 的注入语义（renderer `standardKit`/`SlotOutlet` 源码）、dshmarket 实证模板（闭包 `dshmarket/client/client.js` NS="dsh-market"）。
- [companion 设置单页化](../bug-fix/2026-08-24-companion-settings-consolidation.md)：companion 信息架构的前一轮收敛，本变更不改其结构。
