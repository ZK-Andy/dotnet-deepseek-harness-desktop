# Agent Note: Avalonia G2——`window.__shell` 页面桥

Status: implemented

Erratum: 2026-10-24 — 正文「companion 的消费随 G3 改」实际未在 G3 落地，已在 G5 完成（`client.js`/`testkit` 全部改走 `window.__shell`，`__ryn` 回退与托盘中继删除），见 [avalonia-g5-ryn-host-retirement](2026-10-24-avalonia-g5-ryn-host-retirement.md)。正文不动。

Review: FULL/2026-10-22#4/R1=ok R2=ok R3=ok

评审：首轮 R1 1B+5S、R2 1B+3S、R3 3B+7S；两轮验轮后 R3/R2 0B 收口，R1 第 3 轮的 1B（失败导航留痕）折叠后经用户授权第 4 轮单路复验 0B；Suggestion 逐条书面裁定（含 S5 部分采纳），折叠项不另开轮。

## Problem

G0/G1 只点亮窗口、托盘与关窗语义（[avalonia-g0-entry-switch](2026-10-22-avalonia-g0-entry-switch.md)、
[avalonia-g1-tray-recall](2026-10-22-avalonia-g1-tray-recall.md)）：新宿主里的页面与 C# 之间没有任何通道。
旧宿主的 `window.__ryn` 承担四件事——命令请求（`invoke`）、页面事件订阅（`on`）、每页加载脚本注册
（`InjectScriptAsync`）、宿主推事件（`EmitEvent`）；这些全由 Ryn 运行时实现，Avalonia 宿主拿不到。

`NativeWebView` 只给三个原语（[包文档](https://docs.avaloniaui.net/controls/web/nativewebview)）：
`WebMessageReceived`（页面经 `invokeCSharpAction(body)` 上行，参数只有 `Body`，**没有来源 origin**）、
`InvokeScript(script) → Task<string?>`、导航三事件（`NavigationStarted` / `NavigationCompleted` /
`NewWindowRequested`）。没有 document-start 注入钩子，也没有 host→page 订阅语义。

## Decision

- **页面凭据**：注入 `window.__shell = { invoke, on, off, _resolve, _emit }` 与 `window.__shell_titlebar`
  （`{mac, height}`，供 G1 顶栏片消费）。`invoke(cmd, args)` 返回 Promise；上行经
  `invokeCSharpAction(JSON.stringify(...))`，下行经 `InvokeScript` 调 `__shell._resolve(id, ok, payload)`；
  宿主推事件经 `__shell._emit(name, payload)`。
- **信封**：`{"v":1,"n":<nonce>,"id":<int>,"cmd":"<id>","args":<json|null>}`；回包 `_resolve(id,true,<result>)`
  或 `_resolve(id,false,{"error":...})`。`args` 原文透传给 `ICommandRouter.RouteAsync` 的字节面，
  帧契约与命令 ID 不变。
- **来源校验（无 origin 的替代判据）**：`WebMessageReceivedEventArgs` 只有 `Body`，故用两条判据同时成立才分发——
  (a) 信封里的 nonce 等于**当前文档**的 nonce（每次成功 `NavigationCompleted` 轮换并重注入）；(b) 最近一次
  **成功**导航的顶层 URL authority 等于壳 origin。注入对任何成功完成的文档都执行，所以 nonce 只证明「该文档
  加载过桥脚本」，不证明来源——来源由 (b) 兜住；两条判据各自留痕（信封/nonce 不符记
  `[shell-bridge] 丢弃信封不合法的消息`，origin 不符记 `[shell-bridge] 丢弃来源不明的消息（顶层文档不是壳 origin）`）。
  导航失败/取消不轮换、不改 origin（显示的仍是旧文档，换 nonce 会让它的 invoke 全被拒且无恢复路径）。
  残余风险：与壳同源的页面内脚本本就能 `invoke`（旧模型同样如此），本判据挡的是外源文档与跨导航重放。
- **注入时机**：每次成功 `NavigationCompleted`（API 无 document-start 钩子；窗口构造时文档尚不存在，
  不注入）。因此首屏内联脚本执行时 `__shell` 通常尚未定义——页面侧消费者必须容忍缺失（轮询或退化），
  `wwwroot/index.html` 改为轮询至多 2s；companion 的消费随 G3 改。
- **线程**：消息在后台任务分发（不占 UI 线程），`InvokeScript` 由适配器封送到 `Dispatcher.UIThread`；
  桥持有窗口寿命的 `CancellationTokenSource`，`Dispose` 取消并解订阅。
- **端口化**：新增 `IWebViewSurface`（可测面：消息/导航事件 + `InvokeScriptAsync` + `ReloadAsync`）、
  `IPageScriptRegistry`（每页脚本注册，替换 `PagePump` 对 `IRynWebView` 的直接依赖）、
  `IPageEventPublisher`（替换 `EmitEvent`）。本片只有 Ryn 侧适配器实现这两个端口——新宿主的每页脚本
  首个消费者是 G1 顶栏（G2 无消费者，故不实现死面）；旧宿主继续编译到 G5 清理。
- **健康探针**：`AvaloniaWindowScriptTarget` 实现 `IWindowScriptTarget`（`InvokeScript` 直通，未就绪抛
  `InvalidOperationException`）；`PageHealthMonitor` 的 reload 用 `ReloadAsync`（`Refresh` 是原生原语）；首拍间隔沿用
  `HealthInitialDelaySeconds`。
- **外链拦截**：`NavigationStarted` 里的外站 http(s) 一律取消并交系统浏览器（Avalonia 的事件参数没有
  user-initiated 判据，无法区分用户点击与程序化导航；壳内程序化导航只去同源）；`NewWindowRequested`
  标记已处理并同样走系统浏览器（失败推 `ExternalLinkOpenerFailed` 事件）；同源/非 http(s) 放行。
  策略仍是 Core 的 `ExternalLinkPolicy`。
- **命令面**：本片接 PageBridge 域四个路由（`app.openExternal`、`desktop.ui.getLocale`、
  `desktop.companion.setLocale`、`desktop.companion.report`）；其余八路由随 G3。
- **增量闸豁免：gross ≤ 2000，fanout ≤ 15**（本批新增文件与测试面一次性依据；三处 `--exempt` 指针随批
  指向本篇，批次入库后自动失效）。C2 在册清单同步扩 `Hosting/AvaloniaHost.cs` 的 `RunUi`/`CreateStartup`
  （F2 方法长度上限要求把启动面从 `Run` 拆出；二者同属装配/启动头部，非编排）。

## Alternatives considered

- **继续复用 Ryn 的 `IRynWebView` 当通道**：等价于把 Ryn 运行时留在新宿主，G5 再拆一次；且 Ryn 的 IPC
  依赖其窗口类型，Avalonia 宿主没有。落败。
- **保留 `__ryn` 名字、只换实现**：页面侧零改动，但名字把已退役宿主写进新契约，迁移收尾还要再改一轮。
  落败（`__shell` 一次到位）。
- **`__ryn` 与 `__shell` 双别名**：旧 companion 立刻能用，但两个入口指向同一通道会让 G3 的迁移面模糊，
  且别名没有退役时点。落败。
- **用 `WebResourceRequested` 在文档创建前注入**：该事件是请求面，不能注入脚本，且会把静态资源路径
  与桥接耦合。落败。
- **不做来源校验，只信 `invokeCSharpAction`**：外源文档（用户点开外链被放行、或页面被注入）可直投命令帧，
  与原 `AllowedOrigins` 面等价的要求落空。落败。
- **消息在 UI 线程同步处理**：路由里可能有文件/进程 I/O（报障写日志、外链打开），会卡 UI 线程。落败。

## Consequences

- 页面侧兼容窗口：`__shell` 在 `NavigationCompleted` 后才存在，首屏内联脚本拿不到；`wwwroot/index.html`
  已轮询，dsh 页面自身不消费桥，companion 消费随 G3 换到 `__shell`。
- 迁移期两套宿主并存：Ryn 侧 `RynNavigationCallbacks` 与新 `ShellBridge` 各自实现同一条外链策略
  （策略本体在 Core，二者只做接线），G5 清理 Ryn 侧。
- `invoke` 在 JS 侧有 15s 超时；宿主侧不设全局超时（路由自管），超时由页面先兜。
- 无按钮级真机验证：`WebMessageReceived` 在三种引擎上是否都走 `invokeCSharpAction`（包文档如此声明）
  只能实跑取证；本片的本机验证限于构建、单元测试与门禁。
- 新宿主每页脚本注册（`IPageScriptRegistry`）随 G1 顶栏接入；本片只有 Ryn 适配器实现它。
- `desktop.bootstrap.retry` 不在本片路由表：新宿主尚无引导进度面，接进来只会 `Gate.Signal()` 后
  回假成功（无安装循环可唤醒）；随 G3 的引导 UI 一并接入，`wwwroot/index.html` 的 `.catch` 已标注该窗口。
- 覆盖率数字的机器家仍是 `scripts/test-baseline.json`，本篇不复制数值。

## Testing

- `ShellBridgeTests`：导航后 nonce 轮换、origin 不符丢弃、失败导航保留当前文档状态、路由分发成功
  /未知命令/路由异常回错误帧、外站 http(s) 取消并打开、开失败推事件、同源/非 http(s) 放行、
  新窗口标记已处理。
- `ShellBridgeScriptTests`：信封解析（合法/缺字段/版本不符/nonce 不符/缺 args）、注入脚本含 nonce 与
  `_resolve`/`_emit`/`invoke`、`__shell_titlebar` 字段、载荷编码、`AvaloniaWindowScriptTarget`
  直通求值（null→空串）与未就绪上抛。
- `PagePumpTests`：注册路改经 `IPageScriptRegistry` 后的重试/放弃语义不变（假体替换）。

## Related

- [avalonia-host-migration](2026-10-22-avalonia-host-migration.md)：阶段表与本片边界。
- [avalonia-g1-tray-recall](2026-10-22-avalonia-g1-tray-recall.md)：上一片。
- [window-script-target-port](2026-10-21-window-script-target-port.md)：`IWindowScriptTarget` 端口出处。
- [open-external-links-in-system-browser](../bug-fix/2026-08-21-open-external-links-in-system-browser.md)：外链策略与失败提示。
