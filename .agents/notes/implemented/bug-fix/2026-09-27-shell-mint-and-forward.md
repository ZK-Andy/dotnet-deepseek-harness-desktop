# Agent Note: 壳铸币与请求转发（对齐上游官方桌面端）

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok
Review: FULL/2026-09-27#2/R1=ok R2=ok R3=ok

Related: 前序 `bug-fix/2026-09-14-bootstrap-cross-scheme-cookie-401`（两跳）+ `bug-fix/2026-09-26-webauth-token-reentry`（P0 自愈）+ `testing/2026-09-26-page-verdict-gate` + `bug-fix/2026-09-27-macos-cookie-grace-reload-and-witness-gate`（grace 未治愈，H2）+ `testing/2026-09-27-auth-replay-diagnostic-and-mac-ci-hygiene`（复演 M/R）+ 上游 `apps/desktop/src/web-document.ts`（`authenticateWebHost`/`forwardWebRequest`）+ Ryn `RynCustomScheme`/`ConfigureCustomScheme`。

## Problem

mac 401 墙的根因链已闭环，但方向错了：复演证明服务端链完好（mint/200），grace 证明"等"无效（H2：cookie 从未落盘），Ryn 无 cookie 面——在 WebView 内解 cookie 是死路。上游官方桌面端（`apps/desktop`）根本不走这条路：`authenticateWebHost` 在主进程用 Node fetch 打 token URL 铸币（`redirect: manual`，收 `set-cookie`），`forwardWebRequest` 逐请求贴 cookie 转发，`set-cookie` 扣下不给页面；窗口加载自有 scheme（`dsh-app://app`），渲染器永不见 token 与 cookie。竞品（dsh-tauri）是另一条路（插件覆写鉴权闸门降 401），dsh 的门要动——上游形态门一个不动，严格更优，插件覆写方案撤回。

## Decision

- 新增 `Infrastructure/Runtime/DshShellForward.cs`（边界层，壳 origin 常量家）：`MintAsync`（托管复演第 2 步转正：token 跳 `redirect: manual`，取首个 cookie 名值）、`ForwardAsync`（透传 method/path/query/body，删页面自带 Cookie/Host 后贴壳 cookie——Origin 原样透传，dsh 门不用 Origin 且值为壳常量无秘密；`Accept-Encoding` 强制 identity）、`set-cookie` 扣下不回页面。
- 窗口改 load 壳 origin（`dsh-app://app/`，与上游同名；Ryn `ConfigureCustomScheme` 在 `BuildApp` 装配，initial navigation 之前）。
- 铸币点三处（StartRuntime/EnterMainUiAsync/收养）：bootstrap 路径 dsh 在 StartRuntime 之后才就位，
  StartRuntime 的 mint 够不着——EnterMainUiAsync 必须无条件重铸（覆盖式，每次全量 HTTP），否则转发 502 白页
  （dispatch `36273205175` arm64 实证：壳到达 4 次、探针采占位页、见证 mean=1.0 纯白）。
  凭"同一 epoch 免重铸"省一次 mint 是错的：epoch 起点以调用点为准，不以省为准。
- 导航两跳退役为单跳（壳 URL 一次直达）；P0 自愈 + grace 重载退役（含 `AuthGraceReloadDelaySeconds` 配置键、appsettings、单测同步删）；复演诊断类 + 钩子 + 烟脚本 env 按欠账删除。
- 探针/裁决口径跟转：`expectedOrigin` 改壳 origin（`SameSite` 在托管请求里不存在，手工贴 cookie；401 判据保留，dsh 真挂仍 fail loud）。
- 鉴权标记不限 origin（Core）：壳页 `location.origin` 为 opaque `null`，标记命中即 auth（正常 UI 永不含该串）；无标记非同源仍 unknown。
- 落定门跟产品形状走（共享库）：到达门改为 `≥1 到达且（token 第二跳或①后到达 ≥1）`，auth 照拦；占位（①前/后不定）+ 壳单跳在两种时序下都恰好满足，witness 仍是内容裁决者。
- eval 首跳链整条退役（`TryEvalFirstHopAsync` + `PagePump` 两函数 + 单测）：壳单跳经原生 `loadRequest`，无同步 hang 语义，旧绕行无消费者。
- 脱敏沿用复演纪律：日志只记状态码/头名/字节数；token/cookie 值永不落盘（单测钉死零泄漏）。
- bound 数字来源（见代码注释）：30s 回环上限（非用户可调行为）/32MB 与 Ryn IPC 同口径/dsh 单跳铸币链对应 3 跳跟进上限。

## Alternatives considered

- **插件覆写鉴权闸门（竞品同构）**：落败——要降 dsh 的 401 门（env 标记 + 版本耦合 connection 内部形态）；上游形态门不动，安全姿态严格更好（403 fence、独立 dsh、token 链全原样），且无版本耦合。
- **继续修 WebView 内 cookie（原生仓取证/等上游）**：落败——grace/H2 已证此路无解；Ryn 无 cookie 面；周期不可控。
- **env 门控渐进（先双轨，WebView 直连保留）**：落败——双轨要维护两套导航/裁决口径；转发门一旦 dispatch 绿，直连路径即死代码，R1 必砍，不如一次切换（降级路径保留：dsh 未起仍走 wwwroot）。
- **沿用 `ryn://app` 不做新 origin**：落败——占位/引导页同源混用要路径分区，心智贵；新 origin 与上游同名，隔离干净（用户已拍板 B）。
- **SSE/流式直通 WebView**：落败——Ryn serving path 物化流（备注明示），且直通即回到 cookie 墙；流式经转发走（首版行为：不断流、可能缓冲，dispatch 实测聊天流形态验收，降级可接受则转正）。

## Consequences

- mac 401 类问题整类消失（门内无 401）：到达 + 见证双绿即真绿；Linux 同构受益（导航简化）；win 腿未到 settle，行为随之正。
- 代价：新增壳→dsh 本机一跳（亚毫秒）；流式响应经转发可能缓冲且 30s 上限转 502（Ryn serving 物化），聊天流形态 dispatch 验收，不行则立项优化；响应头最小集（content-type 外全丢，ETag/缓存/下载名延期下批，待办落点见代码 `DshShellForward.cs` TODO）；handler 无 ct 位，切走的导航任务自生自灭（Timeout 兜底、回环毫秒级，并发堆积可接受）。
- dsh 未起/铸币失败：loud + 走既有 wwwroot 降级，不挡启动；铸币失败不进转发（窗口不 load 壳 URL）。
- 欠账清理：复演诊断类 + 烟脚本 env、`AuthGraceReloadDelaySeconds` 全套、本 ADR 的前序 grace 批行为——本批一次清，不留死代码。

## Testing

- 铸币/转发单测（条件回环夹具：401/303+cookie/200/错 token；零泄漏断言；取消静默；流式透传字节一致）。
- `dotnet build` 0 警告；`dotnet test` 818/818（Core 162 = 157 + marker 不限 origin 3 + 未标记异源钉死 2；Desktop 182 = 191 − eval 首跳 9；Infra 474 = 468 + 转发 7 + 外链/自循环/脱敏 3 − 复演 4）。
- R1/R2 修法已合入：外链 302 即停（cookie 不出壳）、体读取进 try（SSE 30s 上限转 502）、Location 脱敏、token 段解析、跟进目标 dsh 形（单测逮住壳形跟进 bug）、响应头最小集延期（TODO 下批）。
- 三脚本 `--self-test` 全绿（mac 中央裁剪 witness 不动；lib 门规则变更随附单跳用例 + deadpid 夹具隔离修复）。
- dispatch `package-macos` + `package-linux`：到达 + verdict + 见证 mean/sd + 真 UI 像素四件套。
