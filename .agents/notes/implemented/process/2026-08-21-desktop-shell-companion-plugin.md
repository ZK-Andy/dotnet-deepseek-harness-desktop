# Agent Note: desktop-shell-companion-plugin

Status: implemented

## Problem

壳需要触碰 dsh web 页面：外部链接接管此前用宿主侧脚本注入实现（`InjectScriptAsync` READY 注册 + 当前页 `EvaluateJavaScriptAsync` 补跑 + Program.cs 里 60×500ms 重试环），脆弱且静默吞错；即将开工的 Self-Update 也需要在页面上有常驻 UI。而 dsh 的 Web 应用本身由 slot 组装系统构成，插件是一等 UI 公民——继续用注入等于绕开宿主提供的官方扩展点。

## Decision

新增仓库内第一方 dsh 插件 `plugins/dsh-desktop-companion`（桌面伴生），把「壳 ↔ web 页面」的集成收拢到一个包：

- 客户端半（`client/client.js`，手写 `window.__ModuleLoader__.load({id, factory})` 格式，factory 返回值即 exports，免打包器；exports 必须携带 `apply(ctx)`——web 内核把每个 boot-manifest 模块 adopt 为一条 cordis loader entry 并应用其 exports，缺 `apply` 的 entry 判 invalid plugin 且单条失败即中止整个 web boot）：
  - 外部链接 capture 拦截：顶层帧、http(s)、`target="_blank"` 或跨源 → `preventDefault` + `window.__ryn.invoke('app.openExternal', {url})`，语义与被删的注入脚本逐条一致；幂等守卫换名 `__dshDesktopCompanionLinks`。
  - 过渡共存守卫：仍带旧注入 catcher 的已发布壳上，插件与注入脚本谁先注册谁生效（认领时同时置 `__ryn_externalLinkCatcher` 与 `__dshDesktopCompanionLinks` 双旗，后到者被自身守卫挡下），保证每文档恰好一个处理器；待无任何在发版本携带注入脚本后移除。
- 服务端半（`lib/index.js`）：inert cordis 入口（占位，self-update 阶段挂路由）。
- 壳侧删除 `ExternalLinkClickCatcher` 与 Program.cs 注入重试块；保留 `ExternalLinkPolicy`（URL 判定仍在 C#，xunit 覆盖）与 `ExternalLinkCommandRouter`（`ryn.invoke` 的宿主端不变）。
- 分发复用市场同款链路：`bundle-runtime-ci.sh` 从仓库源码 staging tar 出 `dsh-desktop-companion.tgz`（package/ 前缀，macOS bsdtar 兼容；源码缺失 fail loud）；`MarketInstallHelper` 泛化——`IsBundleInstalled(pkg)`/`EnsureBundlesContainsAsync(pkg)` 取代 dshmarket 专名方法，新增 `ResolveCompanionSpec`（tgz>1K → 闭包目录 → **null**，无 registry 回退）；Program.cs 后台任务改为收集未就位插件列表后单次 `dsh plugin add <spec…>` 多包安装。
- 开发隔离守卫：检测到 `DSH_DESKTOP_RUNTIME_DIR`（打包产品永不设置）即跳过随包插件安装——开发运行的默认 `DSH_HOME` 与已装正式版共享，否则会把指向工作区的 `file:` 依赖写进共享 profile，导致正式版加载开发插件乃至工作区删除后 web boot 失败。

## Alternatives considered

- 维持壳侧注入并叠加新功能：落败——重试环、补跑、空 catch 是每功能一份的持续税；且绕开宿主 slot 体系意味着永远做不出原生观感的常驻 UI。
- 灰度并行（注入与插件同时在线）：落败——两套 capture 监听对同一点击各触发一次 `app.openExternal`，系统浏览器会开两个标签，无法安全共存验收。
- 把 Self-Update 全部逻辑做进插件：范围外——跑安装器/重启壳是特权操作必须留 C#（RuntimeSupervisor 会把子进程异常退出当崩溃自动重启，插件侧杀父进程与之打架）；本 ADR 只定「UI 走伴生插件」的方向，更新状态机仍按原计划落 C#。
- 引入 tsdown/tsc 构建 client bundle：暂缓——当前功能为无依赖手写 factory 即可满足；待 self-update 需要 React primitives/slots 注册时再引入构建链。

## Consequences

- 收益：删除壳侧 ~75 行重试/补跑代码；监听随 SPA boot 注册、天然存活于重渲染与崩溃重启后的新文档；此后每个「桌面壳要碰 web 页面」的需求有干净扩展点（overlay pill / settings 区块均为宿主声明过的 slot）。
- 代价/风险：插件安装检测只查存在性不比版本（与 dshmarket 现状一致），伴生插件升版需在 self-update 专案补版本感知重装；client 侧 JS 无自动化测试（薄监听 + 判定逻辑留在 C# 缓解）；dsh web 未启动时插件不生效（可接受——接管对象本就是 dsh 页面）；`shell.overlay`/boot manifest 为上游非契约表面，靠逐 release 钉死内置 dsh 版本兜底；客户端半的 `apply` 契约是 web boot 的硬门槛（见 Decision），任何后续手改 client.js 都不得让 factory 返回无 `apply` 的对象。
- 验证：沙箱端到端通过（`plugin add` 双 spec 一条命令装两包 → bundles 双入 → boot manifest 出现 companion 行 → `/plugins/dsh-desktop-companion/client.js` 伺服字节与源码一致）；`dotnet test` 59→64/64；三部门禁全绿。**实机验收 ✅**（2026-08-22 用户正式版 v0.1.20）：外链点击开系统浏览器且仅一签、站内导航正常、SPIKE 标记可见（验收后已删）。
