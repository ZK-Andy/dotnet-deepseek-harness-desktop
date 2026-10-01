# Agent Note: mint-probe-payload-envelope（铸币探针 payload 形状——稳定化恒 fail-open 修复）

Status: implemented

Review: LIGHT/2026-10-01#3/R2=ok（0 Blocker；Suggestion 1 条已采纳：夹具类级 summary 补网关形状闸说明）

## Problem

holder 铸币门控（[holder-mint-gate-deepening](../feature/2026-10-01-holder-mint-gate-deepening.md)）把「首屏一次出全」
钉在「会话服务就绪」，判据是壳侧直发 `POST /api/session/list` 的探活。首版信封发裸 `payload:{}`，
而实机 typert 网关（`dsh-api-gateway` 的 `invokeRpc`）强制 payload 恰有一个 plain-object `args` 字段，
`session/list` 的 args 描述符只收保留空请求 `_request`。

2026-10-01 13:39 对本机运行中的实机（dsh 0.2.0-rc.2 运行时）实测三形状，同 cookie、同路径：

| 探针 payload | 网关应答 |
|---|---|
| `{}`（首版所用） | `200` + `result.ok:false`，`gateway/internal`：Remote payload must contain exactly one plain-object args field |
| `{"args":{}}` | `200` + `ok:false`：args 缺 `_request` |
| `{"args":{"_request":{}}}` | `200` + **`ok:true`**（返回会话列表 items） |

页面真实请求体在壳代理日志里恒 131 字节，与 `{"args":{"_request":{}}}` 逐字节吻合（探针发该形状同样 131 字节）。

后果：判据 `result.ok === true` 恒不成立 → 31 次采样全非 Ready → 稳定窗永不达成 → 30s 预算耗尽 fail-open。
0.5.13 实机日志（`~/.dsh/logs/host.log`）零次「会话面 Ready」、4 次「预算耗尽」，覆盖 04:05（装机后首启）至 13:33
的每次铸币；冷启/引导落定/收养重铸/鉴权自愈四条铸币链同走 `MintAsync`，故整机表现为「更新到 0.5.13 仍然后加载，
且每次启动白等 30 秒」。

证伪面：旧笔记 Decision 1 断言「payload `{}` 即合法（`SessionListRequest = {cursor?}`）」，依据是 host.log
「68 次命中且均为 200 JSON」——只核了 HTTP 状态码，未核响应体的 `ok`；`{cursor:null}` 实测被 boundary validation
拒（`gateway/input-invalid`）。评审 R2#2 已实测到「`{}` 回 200+ok:false」，但只把它读作「判据须加 `result.ok:true`」，
未读作「请求形状错」，于是把必然失败固定成恒 fail-open。单测夹具对任何带 cookie 的 `POST /api/session/list` 都回
`ok:true`，与真实网关契约脱节，901/901 全绿未拦住。

## Decision

1. **探针 payload 改为 `{"args":{"_request":{}}}`**（`DshShellForward.ProbeSessionListReadyAsync` 信封拼接）：
   与实机客户端同形；注释写明 args 包装是 typert 网关硬契约、`_request` 是 session/list 的保留空请求。
2. **夹具按实机网关语义校验形状**：`DshMimicResponder` 对未包 args 的探活回 `200` + `gateway/internal`
   错误信封并置 `ProbeArgumentsRejected`（不再是「任何请求都回 ok:true」的宽松桩）。
3. **形状钉死回归**：`Mint_ProbePayload_CarriesTypertArgsEnvelope` 断言探针体含
   `"method":"session/list","payload":{"args":{"_request":{}}}` 且未被网关拒。
4. **撤销旧笔记的前提**：holder-mint-gate-deepening Decision 1 的「payload `{}` 即合法」按勘误通道作废（正文不动）。

## Alternatives considered

- **保留裸 `{}`，判据放宽为「200 即就绪」**：rejected——网关对形状错的请求恒回 `ok:false`，放宽等于门控不存在，
  回到首版「静态面 200」的被推翻形态（UI 先出、树后到）。
- **门控退回 `GET /` 静态面**：rejected（旧笔记已裁定）——静态面挂载严格早于会话服务激活。
- **用页面注入的 `__DSH_BOOT_READY__` 当判据**：rejected——那是注入表落地信号（比会话服务更早），且要经页面 JS 求值。
- **探针复用手上 vendored 的 `dsh-client-connection` JS `call()`**：rejected——为一条探针引入 JS 运行时耦合，
  成本高于钉住信封形状。
- **只加日志、不修形状，等上游**：rejected——门控在预算内永不通过，等于长期白等 30s 且不解决首屏问题。

## Consequences

收益：稳定化在会话服务真就绪后可达（稳定窗 2s + 节拍 1s），holder reload 落地时列表拉取必命中，首屏回一次出全；
不再每次铸币白付 30s 预算。四条铸币链共用同一判据，一处修复覆盖全部入口。

代价：探针形状与上游网关/描述符契约耦合，上游若改 args 形态（如放开 `cursor`）即漂移——由夹具形状校验与
形状钉死回归在 CI 显形，不再靠「HTTP 200」这种弱证据。

遗留（归上游面）：会话服务就绪 ≠ 页面 hydrate 完成，页内自举残余 1–2s 仍待上游列表快照 stale-while-revalidate。

## Testing

- `DshShellForwardTests` 10/10：新增形状钉死测；夹具按实机语义拒裸形状。
- 反向验证（变异）：把探针临时回退为裸 `{}` 后重跑，`Mint_ProbePayload_CarriesTypertArgsEnvelope`、
  `Mint_ValidToken_StoresCookieWithoutSecretsInLog`、`Mint_WebFaceReadyAfterDelay_PassesGateWithoutFailOpen`
  三条即红——夹具与回归确实咬住形状，非空转。
- `dotnet test` 全绿（902/902）。

## Related

- [holder-mint-gate-deepening](../feature/2026-10-01-holder-mint-gate-deepening.md)（门控决定的出处；Decision 1 的 payload 前提已由勘误行作废）。
- [relay-web-readiness](./2026-09-16-relay-web-readiness.md)（稳定窗判据来源）。
- [relay-restart-client-module-collapse](./2026-09-19-relay-restart-client-module-collapse.md)（「树不在」空窗的事故出处）。
