# Agent Note: navigate-call-timeout（导航调用超时）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 定量考古（四轮 arm64 deb-start→① 均为 34–43s，与 amd64 同量级） + dispatch run `36234938343`（amd64 首绿；arm64 hop1 发起后 90s+ 无返回、到达/eval/推送全冻）。

## Problem

arm64 腿 hop1 `NavigateAsync` 发起后 90s+ 无返回（无"调用已返回"行），且到达处理/eval/进度推送等 UI 线程相关原生通道同步冻结——调用本身疑在原生侧 stall【推断·未证】（saucer/WebKit 在 ARM runner 上的挂起之一种可能；mac-x64"hop2 未发出"同嫌疑形态）。同调用 amd64 秒级返回（单轮 dispatch 实证，未计 n）；bootstrap 阶段时序两架构同量级，排除整机慢。`NavigateAsync` 当前无界等待：挂起即 bootstrap 永久卡死（连 loud 都没有），是最坏的失败形态。

## Decision

- `NavigateAndAwaitCommitAsync` 内给 `NavigateAsync` 加有界超时（新 `NavCallTimeoutSeconds`，默认 30s：amd64 单轮实证秒级返回，30s 为估计余量；5s 复用被拒是谨慎性原则 + 分键语义，非实证）：超时 loud `[nav] 导航调用超时（Ns），按已提交继续`，沿既有"按已提交继续"哲学走后续提交等待与探针（超时调用本身成 fire-and-forget，沿探针先例可接受）；应用退出 OCE 照常上抛。
- 超时值进配置模型（可调禁硬编码纪律）；快机器零变化（首调即返）。
- arm64 本轮预期仍红（调用超时 loud 化，非修好原生 stall）——本批买的是有界诊断，不是绿；原生根因走 upstream（见 Testing）。

## Alternatives considered

- **无限等（现状）**：落败——挂起即永卡且无声；有界 + loud 严格更优，无任何场景更差。
- **超时后中止 bootstrap**：落败——调用可能只是极慢（非死锁），中止丢掉后续探针带来的鉴别信号；继续走提交等待 + 探针，信号最多。
- **复用 NavCommitTimeoutSeconds（5s）**：落败——提交等待与调用返回是两段不同延迟，分键各表其义；5s 偏紧是谨慎性判断（非实证），宁取 30s 估计余量。
- **等 upstream 修好再动**：落败——上游周期不可控；有界化是壳侧自保，与 upstream 并行。

## Consequences

- 挂起变有界 loud（≤30s+5s）；误杀方向安全（慢调用被当已提交，探针继续鉴别）。
- 代价：超时后悬空的原生调用与后续导航可能在 WebKit 侧合并——与既有"连发合并"同类语义，不新增风险类型。
- arm64 CI 在本批后仍红（预期内）；真绿待 upstream 或更深 native 调试。

## Testing

- 配置矩阵跟值（超时默认 30/全键覆盖 31）；调用编排薄层沿既有先例不单测。
- 真验证 = dispatch：amd64 保持绿；arm64 预期`导航调用超时` loud 行（诊断化，非绿）。
- upstream：Ryn issue（arm64-only 原生 navigate stall，附四轮证据与冻结引导页截图）。
