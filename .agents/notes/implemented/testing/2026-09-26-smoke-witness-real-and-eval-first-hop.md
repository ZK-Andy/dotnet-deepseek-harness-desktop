# Agent Note: 截图见证变实与首跳 eval 优先（冒烟不再加时间）

Status: implemented

Review: FULL/2026-09-26#3/R1=ok R2=ok R3=ok
Review: FULL/2026-09-26#4/R1=ok R2=ok R3=ok

Related: 前序 `page-verdict-gate`（终页裁决门）+ `bee3e49`（内容见证改由截图承担）+ dispatch run `36247724186`（amd64 绿/arm64 红）+ Ryn 上游 issue #101（arm64 `NavigateAsync` 原生 hang，按错误模型关闭）。

## Problem

dispatch `36247724186` 定界出两个独立病灶，都不在等待时间上：

1. **截图见证空转**：amd64 成功日志里是 `无 convert，跳过截图内容见证`——runner 显示栈只装 `rpm xvfb scrot dbus`，`smoke_capture_witness` 的无 convert 分支直接 `return 0`。`bee3e49` 想要的 `mean/sd` 门在 CI 上从未执行，amd64 的绿只是到达门。
2. **arm64 首跳原生 hang**：全日志唯一到达是 `ryn://app/index.html` 自己；两跳对外导航都是 `导航调用超时（30s）+ 等待提交超时（5s）` 零到达。Ryn 0.38.0 `RynWebView.cs:413` 下 `NavigateAsync` 是同步调 `saucer_webview_set_url_str` 后返回 `ValueTask.CompletedTask`——hang 在同步段内（#101 复核结论），`Task.Run + WaitAsync` 界只能变 loud 日志，不能让导航成功。截图冻在"npm 安装中"是果：`进度推送失败 eval timed out ×4`（在途 eval 被 hang 住的导航抛弃）致页面收不到更新。
3. **探针层（已定性，不动）**：外部 origin 上 `ryn._ipcBase` 默认空串，eval 回包 POST 到 dsh 自己的服务器（无 `/ipc/eval/`）→ 宿主 `_pendingEvals` 挂死（Ryn `EvalTimeout = 30s`，`RynWebView.cs:206`）。15s/45s 同形与预算无关，故本批不动任何超时值。

## Decision

- 见证变实（CI 面）：`package-linux.yml` 显示栈加 `imagemagick`；`smoke_capture_witness` 无 `convert` 时由"记 note 放行"改"记 error 不通过"——调用点已限定显示腿（`DISPLAY` + `SMOKE_SHOT_DIR` 非空），无显示腿不受影响；`--self-test` 内无 convert 仍跳过夹具（本地无依赖不红）。
- 首跳 eval 优先（产品面，`DesktopBootstrap.App.EnterMainUiAsync`）：第一跳先经页面内 `location.href`（renderer 发起导航，绕过 saucer `set_url` 同步段）——脚本 `BuildEvalNavigateScript` 纯函数（目标 URL 经 JSON 序列化拼入，`try/catch` 包裹，同步返回布尔）；`PagePump.TryNavigateViaEvalAsync` 以"可注入 eval 委托"为缝（`Func<string, CancellationToken, ValueTask<string>>`），有界 `NavCallTimeoutSeconds` 复用既有配置（不新增可调参数），返回值经 `RynProbeValue.Decode` 判 `"true"`；发不出/超时/异常 → `false`，调用方回退原生 `NavigateAndAwaitCommitAsync`（旧链一字不改）。
- 提交等待抽共用 `WaitNavCommitAsync`（原生/ eval 两路径共用，日志各记各路：原生 `导航提交等待超时`（由旧串 `等待导航提交信号超时` 改来，无机器消费者），eval `首跳 eval 导航提交等待超时`——诊断时可分清哪条路走的）。
- 守卫豁免（产品面，`PageBridge.RynNavigationCallbacks`）：renderer 发起的跳转在引擎眼里是用户发起，异源即被自家外部链接守卫 `Block`（dispatch `36251508658` amd64 实证：`拦截外部导航 → 系统浏览器` 后首跳永不提交，终页 401）——新增已授权 origin 集（随原生 `AuthorizeIpcOrigin` 同点登记 `AuthorizeOrigin`），集内目标一律 `Allow` 并记 `放行已授权 origin 导航`；集外仍拦。只放行宿主亲手 spawn 的 dsh loopback，外站性质不变。
- arm64 冻结（CI 面）：原生 hang 在 managed 边界之下三选一（saucer arm64 库 / WebKitGTK 构建 / runner 环境）未定，不再烧 dispatch 碰运气——矩阵加 `frozen`（amd64=0/arm64=1 → `SMOKE_FROZEN`）；冻结腿上落定失败但①已达 → `SMOKE_VERDICT=frozen-native-hang` 放行（截图留痕跳见证，不拦发版）；①未见仍红（真回归不掩盖）。解冻待 Ryn 最小复现有回音。
- cookie 两跳形状不变：eval 首跳仍是"从 ryn 占位页发起 → 落裸 origin"，再从 http 页同站第二跳，`SameSite=Strict` 链路性质与原生跳一致（ADR `bootstrap-cross-scheme-cookie-401` 前提不碰）。
- 范围：第二跳保持原生；mac/win 腿不动；arm64 只冻 verdict，不断安装/打包失败面。

## Alternatives considered

- **继续加落定窗/探针预算**：落败——两病灶分别死在"见证没跑"与"导航没发出"，加时间只延长红。
- **arm64 继续烧 dispatch 碰运气**：落败——原生 hang 在 managed 边界之下，门禁/绕行/预算三路皆够不着；改冻结 verdict（安装/打包失败面不断），诊断转 Ryn 最小复现线下走。
- **首跳直给 `opts.Url`（建窗即 dsh 页）**：落败——首启路径建窗时 dsh 尚不存在（`StartRuntime` 回 null → `wwwroot` 占位），直给要求把建窗推迟到引导落定之后，是启动编排重构，不是最小对照。
- **eval 全代原生（无回退）**：落败——回退是 amd64 的对照基线；无回退则 arm64 绿/红都说不清是 eval 的功还是原生本就能过。
- **见证改 OCR/像素断言**：落败——沿用 `page-verdict-gate` 结论（runner 无 OCR，像素断言随主题漂移），只补齐 `convert` 使既有 mean/sd 门真实执行。

## Consequences

- 显示腿无 `convert` 即红（fail loud），杜绝见证空转的假绿；amd64 本批即可验证门真实执行（日志应见 `截图内容见证通过/不通过`，不再是跳过）。
- arm64：eval 首跳若通，`导航已到达` 应出现在 30s 内，随后看第二跳原生是否跟上——无论绿红，日志都能定界到"跨 scheme 原生跳"还是"http→http 跳"。
- 代价：首启多一次最多 30s 的 eval 尝试窗（仅 eval 发出后等提交时；发不出即时回退）；`_pendingEvals` 在 http 页吃一次 30s 悬空（fire-and-forget 先例同款，可接受）。
- 本批不碰：探针预算三值、落定窗 120s 上限、mac/win 腿。

## Testing

- 新增 `PagePumpEvalNavigateTests`（缝为 eval 委托，无 WebView）：脚本纯函数（URL 转义/`try` 包裹/同步布尔）；`true→true`、`false→false`、悬空→有界 `false`、抛异常→`false`、预取消/在途中取消→上抛；引号 token 拼入不断裂。
- 新增 `RynNavigationCallbacksTests` 守卫豁免 4 例：集内用户发起跳转 Allow 且不交 opener；集外仍 Block；非法登记输入忽略。
- `bash scripts/smoke-install-linux.sh --self-test`（含既有 witness 白/深/UI 三夹具 + 新增 frozen 三夹具）。
- `dotnet test` 全绿对基线；dispatch `package-linux` 看 amd64 见证行 + arm64 frozen 行。
