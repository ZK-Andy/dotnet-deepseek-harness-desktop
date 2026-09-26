# Agent Note: settle-gate-and-probe-retry（落定①后计数 + 探针重试）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 证据由 [`bootstrap-window-ready-wait`](../bug-fix/2026-09-26-bootstrap-window-ready-wait.md) 的 dispatch（run `36232183186`）与截图 batch 的 dispatch（run `36233266978`，截图 `smoke-linux-deb.png` 73KB）联合产出。

部分取代声明：本 ADR 部分取代 [`smoke-settle-content-verdict`](../testing/2026-09-26-smoke-settle-content-verdict.md) 的落定 token 要求（该文 Decision"≥2 到达且含 `?token=` 第二跳"），改为"token 行 OR ①后到达 ≥2"；token 路径保留优先，旧文冻结、关系声明在此。

## Problem

截图证明终页是大厅 UI（侧栏 + Internal Testing Notice + 输入框）：Linux 上 token→cookie 链正常，导航设计路径（授权→发起→返回→到达 ×2）全通——产品无错，错的是判定层两处：①落定门要 `?token=` 到达行，但 Linux WebKit 只报最终提交 URL（两跳到达均为裸 `/`），mac 才报中间跳——平台差异使 Linux 即使成功也过不了门；②鉴权探针单发 15s 超时即判 Unknown（该轮日志有且仅一条探针超时行、其余导航全通，疑似撞上 renderer 繁忙窗口），一次采样赌运气。

## Decision

- 落定门改平台无关口径（`smoke-settle-lib.sh`）：token 到达行（既有 mac 路径，保留）或 ①之后到达 ≥2 次（新 Linux 路径：占位到达多在 ①之前，唯 hop1+hop2 落在 ①后；以 $OUT 文件序为准）。超时/退出 FAIL 语义不变。
- 探针有限重试（产品）：`ProbePageSampleAsync` 内超时/失败重试，总尝试 `AuthProbeAttempts`（默认 2，即初次 + 1 次重试，每次 `AuthProbeTimeoutSeconds`）；成功即返，耗尽回 null（Unknown 放行语义不变）；应用退出 OCE 照常上抛。快机器零变化（首探即中），只给偶发繁忙一次机会。
- 新尝试次数进配置模型（可调参数禁硬编码纪律）；截图持续人眼复核（已开火）。

## Alternatives considered

- **落定门直接数总到达 ≥3（含 ①前占位）**：落败——占位+hop1 双到达即可凑数，hop2 未提交也能过；①后计数把"第二跳真实提交"钉死在时序上。
- **探针无限重试直到有文本**：落败——死 renderer 下拖死启动；有界 + Unknown 放行才是 secure 方向（P0 既定）。
- **只修一边**：落败——两处机制独立（门不过是确定性平台差异，探针 Unknown 是偶发 renderer 繁忙），单修仍留另一风险；用户拍板双修。
- **截图 OCR 断言内容**：落败（重申 smoke-settle-content-verdict 结论）——runner 无 OCR；到达时序 + 人眼截图仍是可达 rung。

## Consequences

- Linux CI 可达 full-chain（产品本就成功）；mac 路径零变化（token 行仍优先命中）。
- 代价：真坏页（两跳提交但内容错）在 Linux 下 verdict 变绿——内容兜底仍靠截图人眼 + P0 自愈 loud 日志；误报面从"恒红"转为"需人眼"，可接受（截图已开火）。
- 探针最坏 +15s 启动延迟（仅超时路径；健康路径零变化）。

## Testing

- 落定自测矩阵加 ①前后用例（①前到达不计数、①后双到达过、token 行仍过）；探针重试由配置矩阵覆盖（尝试数默认 2/全键覆盖）。
- 真验证 = dispatch：预期 `SMOKE_VERDICT=full-chain` + 截图人眼复核为大厅 UI。
