# Agent Note: port-drift-ipc-origin-mismatch

Status: implemented

Review: FULL/2026-09-12/R1=ok R2=ok R3=ok

## Problem

2026-09-12 实机：桌面设置页的更新块显示「桌面自更新在当前运行时不可用（开发运行时可设 DSH_DESKTOP_UPDATE_FORCE=1 开启）」，读起来是 dev 运行时降级；但同一运行（pid 9832，0.4.8 装机版）的 host.log 明明写着 `[host] 自更新：当前版本 0.4.8，RID linux-x64，包类型 rpm…`——该行只在通过 enabled 判定后才写，dev 判据不成立。

根因链（host.log + 活体 IPC 服务器实测 + 上游源码比对）：

1. 该运行的 dsh 子进程崩溃重启时首选端口被占：`[host] 首选端口 36111 被占（疑似残留实例或孤儿 dsh），本次漂移至 44867`，WebView 随后导航到 `http://127.0.0.1:44867/`。
2. Ryn 在**窗口创建时**用 `RynOptions.Url` 的 authority 作为 IPC 的 CORS 允许源（`RynWindow` → `new LocalWebServer(allowedCorsOrigin: devOrigin)`），此后不再更新；而 `LocalWebServer.IsAuthorized` 放行**任意 loopback origin**。授权与 CORS 两头判据不一致。
3. 桥接的 `window.__ryn.invoke` 是跨源 POST（页面 origin → `http://localhost:7421`），带 `Content-Type: application/json` 与 `X-Ryn-Token`，必须先过 CORS 预检；`BuildCorsHeaders` 只在与那个固定 origin 字符串相等时才回 `Access-Control-Allow-Origin`——漂移后的预检拿不到 ACAO，浏览器拦下请求，invoke reject。
4. companion 设置页把「getState 查询失败」一律渲染成 dev 提示，把成因误导成 dev 运行时。

实测（对活着的 IPC 服务器发同一份预检，只换 Origin）：`http://127.0.0.1:36111` → 204 + `Access-Control-Allow-Origin: http://127.0.0.1:36111`；`http://127.0.0.1:44867` → 204 且无任何 CORS 头。

影响面不止自更新：**窗口已按 dsh URL 建好之后**页面 origin 再变（监督器崩溃重启换端口）时，「页面 → 壳」的**全部**命令失效（更新 getState/check/install、开机自启开关、关闭最小化开关、诊断导出、托盘事件中继、locale 桥）；非引导冷启动漂移不触发——那时窗口随后才按漂移后的 origin 创建，允许源与页面 origin 一致；无 PATH dsh 的首启引导路径另有形态（`opts.Url` 为 null、窗口先以占位页创建），不在本告警面。**【推断 · 未证】**壳 → 页面的状态推送走原生 JS eval、不经 HTTP，故侧栏更新圆钮仍会照常出现、点击静默失败（「看得到更新、点了没用」）——该推论出自代码路径（`PagePump.PushUpdateState` 与 `DesktopUpdateCommandRouter`），未实机复现。

## Decision

1. **失败分流**（companion 客户端 0.0.18）：`getState` 失败后按成因分流，而不是一律判定为「无自更新栈」——用无条件注册的 `desktop.autostart.getState` 探**同一通道**——探通 = 通道好、只有自更新路由缺失（dev，沿用原提示）；探不通 = 命令通道整体失效，显示「无法连接桌面宿主：本会话的页面命令通道不可用，重启应用可恢复自更新」。判别不解析桥接/宿主的错误文案（那是实现细节），只用既有命令做行为判别，不新增帧契约；失败查询的异步结论不得覆盖已到达的宿主推送帧，也不得在组件卸载后落状态。
2. **origin 变化的后果带上路径条件**（`HarnessRuntimeHost.Handoff` 的漂移告警）：仅当**本进程此前已成功起过一次运行时**（判据落成既有的 `_port is not null`，不新增状态——`_port` 只在成功块赋值、无清零点），告警才追加「页面命令通道（自更新/设置开关/诊断）本次会话失效，需重启应用」——**非引导路径**下为真即窗口已按那次启动的 dsh URL 建好、CORS 允许源已钉死，本次漂移必然换掉页面 origin。首次成功启动即漂移不追加：非引导路径下窗口随后才按漂移后 origin 创建，允许源与页面 origin 一致；无 PATH dsh 的首启引导路径另有形态（`opts.Url` 为 null、窗口先以占位页创建），其页面 origin 与允许源的关系不由本判据断言（该形态另记待办核实）。宿主 `RestartAsync` 只是 `StartAsync` 的语义转发，两条路径无法直接区分，故判据取「是否已经成功服务过一次」。
3. **根因交上游**：Ryn 的 `BuildCorsHeaders` 与 `IsAuthorized` 共用同一 origin 判据（配置 origin ∪ 任意 loopback），使「宿主已授权的请求」不再被浏览器拦下。已提上游 issue + PR（见 Related）。
4. **自愈留 proposed**：漂移后自动重启壳属行为变更，另立 [proposed 笔记](../../proposed/bug-fix/2026-09-12-drift-command-channel-selfheal.md) 待拍板。

## Alternatives considered

- **只改 companion 文案、不动壳日志**：落败——桌面形态 stdout 不可见，host.log 是唯一可靠通道；缺后果记录会让下一次仍要重新侦查一小时。
- **按桥接错误文案判别失败因（`IPC network error` / `IPC timeout`）**：落败——把上游错误措辞变成我方契约，上游改字即静默失效；用既有命令探通道是行为判别。
- **壳侧常驻代理让页面 origin 恒定**：落败——为单点问题引入常驻组件与新的失败面，成本远超收益（记入 proposed 备选）。
- **等上游修好再动手**：落败——上游修复要跨一个发版周期（合并 → 发 NuGet → 本仓 bump → 出壳版本），而文案与日志是立刻可发的止血面。
- **漂移后重建 Ryn 窗口/应用以刷新允许源**：落败——允许源在窗口创建时固定且无更新入口，重建应用等价于重启壳；作为 proposed 的自愈候选而非本批实现。

## Consequences

- 设置页不再把命令通道故障说成 dev 运行时；用户拿到可执行处置（重启应用）。
- 漂移的代价在 host.log 里一次说清，不必再靠外部探测（预检对比 + 上游源码比对）复盘。
- 残留：上游未修前，漂移发生后的**当次会话**仍需重启应用才能恢复命令面；上游 PR 合并 + 本仓 bump Ryn 后该残留消失。
- 验证：`node --check` 通过；`dotnet test` 569/569、build 0 警告、`dotnet format` 与全部 `verify-*.py` 门禁绿；实机验收项 = 窗口已建后制造端口漂移，设置页显示「无法连接桌面宿主…重启应用可恢复自更新」而非 dev 文案（真机清单见 HANDOFF 待办）。

## Related

- [子进程收割与端口漂移](2026-08-26-child-process-reaping-port-drift.md)：漂移机制与告警的出处；本篇是其未覆盖的后果面（IPC CORS 允许源固定）。
- [端口记忆](2026-08-26-port-memory-per-profile.md)：首选端口记忆与回退 OS 分配。
- [运行时交接收养](2026-09-12-runtime-handoff-adoption.md)：同批的血统收割——漂移触发面的收敛（不是本缺陷的修复）。
- [companion 更新设置页](../feature/2026-08-22-companion-update-settings-section.md)：设置页失败降级的契约所有者（本篇把它的单一降级细化为两种因）。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：本区块失败降级文案的来处；本篇补的是「降级原因不止一种」。
- 上游：`Yupmoh/Ryn` [issue #90](https://github.com/Yupmoh/Ryn/issues/90) / [PR #91](https://github.com/Yupmoh/Ryn/pull/91)——`BuildCorsHeaders` 与 `IsAuthorized` 共用同一 origin 判据，并给 `/ipc/eval/` 响应补同样的头。
