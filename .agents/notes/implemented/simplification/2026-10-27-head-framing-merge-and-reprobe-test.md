# Agent Note: 回包头组帧合流与引导重探负向测试

Status: implemented

Review: LIGHT/2026-10-27/R2=ok（首轮 0 Blocker + 3 Suggestion：夹具不可达组合采纳、覆盖主张部分采纳（补两条 Content-Type 断言并改写措辞）、枚举与计数采纳；无验轮）

## Problem

`ShellProxyFraming` 已把回包头组帧收在 `BuildHead`（状态行 → Content-Type → Content-Length → 白名单头 → `Connection: close`），但另有三处逐字手拼同一形状：`WriteSmallAsync`（失败面 502/500/405/400/403/404 与隧道拒因）、`WriteJsonAsync`（`/__shell_ready` 就绪探针）、`WriteHeadAsync`（静态资源的 HEAD 语义）。三条路径恰是「页看到的错误」与「探针读到的就绪态」的写出口，头部形状与 `BuildHead` 分叉后不会有人发现——改一处漏两处，且不会红任何用例。

`StartupNoticeService` 用 `_bootstrap.IsNeeded ? null : ResolvedDshVersion` 表达「引导介入过则 `Resolve` 探到的版本已过期，必须重探」。测试夹具 `FakeBootstrap.IsNeeded` 固定为 `false`，只覆盖复用半边：把判据写反（取 `!IsNeeded`）或删掉重探，现有用例全绿。

## Decision

三处写出口改调 `BuildHead`，白名单参数传 `null`（这三条路径不透传上游头）：

| 写出口 | 调用 |
|---|---|
| `WriteSmallAsync` | `BuildHead(status, "text/plain; charset=utf-8", bytes.Length, null)` |
| `WriteJsonAsync` | `BuildHead(status, "application/json", bytes.Length, null)` |
| `WriteHeadAsync` | `BuildHead(status, contentType, contentLength, null)` |

字节形态逐字不变：头序、头名大小写、CRLF 与 `Connection: close` 均与手拼串相同，`WriteHeadAsync` 的 `long` 长度经 `long?` 形参无损传入。

`DshLoopbackProxyTests` 另补两条 `Content-Type` 断言：就绪探针的 200（`application/json`）与 guide 的 HEAD（`text/html; charset=utf-8`）——两条路径此前只断言状态码/体，合流后头形态无人钉。

测试夹具 `IsNeeded` 改为可初始化属性（默认 `false`），既有用例语义不变；新增 `RunAsync_BootstrapIntervened_ReprobesInsteadOfReusingStaleVersion` 取生产可达组合：`IsNeeded=true` 的成因即旧值缺失或落后，故旧值取 `0.0.1`（低于底线）、探针返回 `9.9.9`（高于底线），断言探针恰好一次且零横幅——复用旧值会以 `0.0.1` 推底线横幅，判据反转则根本不重探。

## Alternatives considered

- **三处保留手拼、补注释说明「与 BuildHead 同形」**：落败。注释不是约束，形状继续分叉时没有任何信号；`BuildHead` 已存在且签名够用，合流本身零成本。
- **`WriteHeadAsync` 复用 `WriteSmallAsync`（空体）**：落败。HEAD 语义要任意 `content-type` 与 `long` 长度、且不写体；套一层要加参数分支，比直调 `BuildHead` 更绕。
- **给 `BuildHead` 增加 `Connection` 策略参数**：落败。三条路径与现有调用方全部是 `close` 定界（代理逐请求短连接），参数化只多一个恒等分支。
- **用整段头字节快照测试锁死形态**：落败。回环 socket 用例已对状态码与体做端到端断言，本批另钉 JSON 探针与 guide HEAD 的 `Content-Type`；快照会把头序按字节固化成契约，未来合理的头调整要先改快照，收益不足以抵消这层摩擦。
- **只加重探测试、不动三处手拼**：落败。两件事各自都够小，但同属「回包/启动告知的失败面重复」，拆两批要为同一处代码付两次门禁与评审成本。

## Consequences

- 回包头只有一个组帧点（`BuildHead`），六个调用点覆盖小体文本、JSON、HTML、HEAD、buffered、流式头六种写出口；失败面、就绪探针、静态资源的响应头改动落在一处。
- 重探判据进红测试：`IsNeeded` 取反、重探被删、探针被跳过三种退化都会失败；`IsNeeded=false` 的「免第二次 `dsh --version`」仍由既有用例守住。
- 本批不改任何默认值、超时、日志文本或 wire 行为；代理头白名单与 holder `no-store` 的既有语义不变。
- 残留：`WriteSmallAsync` 仍无 XML `<summary>`（内部方法，D004/D005 不要求）；补文档不属本批。

## Testing

- `DeepSeek.Harness.Desktop.Infrastructure.Tests`：新增 1 例（重探分支）+ 2 条 `Content-Type` 断言（JSON 探针、guide HEAD）；`StartupNoticeServiceTests` 与 `DshLoopbackProxyTests` 既有用例全绿，回环 socket 面覆盖合流后的写出口。
- 计数与覆盖率以 `scripts/test-baseline.json` 为唯一家；本批不跟值（无覆盖率变化主张）。

## Related

- [cold-start-wait-collapse-and-proxy-cache-headers](../bug-fix/2026-10-26-cold-start-wait-collapse-and-proxy-cache-headers.md)：版本复用与缓存头白名单的出处；本批给其重探分支补测试。
- [cold-start-mint-303-release](../bug-fix/2026-10-27-cold-start-mint-303-release.md)：启动期 502 归零的成因，使该 ADR 的「启动期导航拦截」立项条件不再成立。
