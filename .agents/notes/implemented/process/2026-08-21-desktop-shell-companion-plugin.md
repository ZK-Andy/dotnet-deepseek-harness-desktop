# Agent Note: desktop-shell-companion-plugin

Status: implemented

## Problem

壳需要触碰 dsh web 页面：外部链接接管此前用宿主侧脚本注入实现（`InjectScriptAsync` READY 注册 + 当前页 `EvaluateJavaScriptAsync` 补跑 + Program.cs 里 60×500ms 重试环），脆弱且静默吞错；即将开工的 Self-Update 也需要在页面上有常驻 UI。而 dsh 的 Web 应用本身由 slot 组装系统构成，插件是一等 UI 公民——继续用注入等于绕开宿主提供的官方扩展点。

## Decision

新增仓库内第一方 dsh 插件 `plugins/dsh-desktop-companion`（桌面伴生），把「壳 ↔ web 页面」的集成收拢到一个包：

- 客户端半（`client/client.js`，手写 `window.__ModuleLoader__.load({id, factory})` 格式，factory 返回值即 exports，免打包器）：
  - 外部链接 capture 拦截：顶层帧、http(s)、`target="_blank"` 或跨源 → `preventDefault` + `window.__ryn.invoke('app.openExternal', {url})`，语义与被删的注入脚本逐条一致；幂等守卫换名 `__dshDesktopCompanionLinks`。
  - SPIKE 可见标记（右下角固定 pill）：供实机验收确认插件客户端确实执行，验收后删除。
- 服务端半（`lib/index.js`）：inert cordis 入口（占位，self-update 阶段挂路由）。
- 壳侧删除 `ExternalLinkClickCatcher` 与 Program.cs 注入重试块；保留 `ExternalLinkPolicy`（URL 判定仍在 C#，xunit 覆盖）与 `ExternalLinkCommandRouter`（`ryn.invoke` 的宿主端不变）。
- 分发复用市场同款链路：`bundle-runtime-ci.sh` 从仓库源码 staging tar 出 `dsh-desktop-companion.tgz`（package/ 前缀，macOS bsdtar 兼容；源码缺失 fail loud）；`MarketInstallHelper` 泛化——`IsBundleInstalled(pkg)`/`EnsureBundlesContainsAsync(pkg)` 取代 dshmarket 专名方法，新增 `ResolveCompanionSpec`（tgz>1K → 闭包目录 → **null**，无 registry 回退）；Program.cs 后台任务改为收集未就位插件列表后单次 `dsh plugin add <spec…>` 多包安装。

## Alternatives considered

- 维持壳侧注入并叠加新功能：落败——重试环、补跑、空 catch 是每功能一份的持续税；且绕开宿主 slot 体系意味着永远做不出原生观感的常驻 UI。
- 灰度并行（注入与插件同时在线）：落败——两套 capture 监听对同一点击各触发一次 `app.openExternal`，系统浏览器会开两个标签，无法安全共存验收。
- 把 Self-Update 全部逻辑做进插件：范围外——跑安装器/重启壳是特权操作必须留 C#（RuntimeSupervisor 会把子进程异常退出当崩溃自动重启，插件侧杀父进程与之打架）；本 ADR 只定「UI 走伴生插件」的方向，更新状态机仍按原计划落 C#。
- 引入 tsdown/tsc 构建 client bundle：暂缓——当前功能为无依赖手写 factory 即可满足；待 self-update 需要 React primitives/slots 注册时再引入构建链。

## Consequences

- 收益：删除壳侧 ~75 行重试/补跑代码；监听随 SPA boot 注册、天然存活于重渲染与崩溃重启后的新文档；此后每个「桌面壳要碰 web 页面」的需求有干净扩展点（overlay pill / settings 区块均为宿主声明过的 slot）。
- 代价/风险：插件安装检测只查存在性不比版本（与 dshmarket 现状一致），伴生插件升版需在 self-update 专案补版本感知重装；client 侧 JS 无自动化测试（薄监听 + 判定逻辑留在 C# 缓解）；dsh web 未启动时插件不生效（可接受——接管对象本就是 dsh 页面）；`shell.overlay`/boot manifest 为上游非契约表面，靠逐 release 钉死内置 dsh 版本兜底。
- 验证：沙箱端到端通过（`plugin add` 双 spec 一条命令装两包 → bundles 双入 → boot manifest 出现 companion 行 → `/plugins/dsh-desktop-companion/client.js` 伺服字节与源码一致）；`dotnet test` 59→64/64；三部门禁全绿。真实桌面「点外链开系统浏览器 + 右下角可见标记」待用户实机验收。
