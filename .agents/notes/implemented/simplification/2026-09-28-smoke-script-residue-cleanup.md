# Agent Note: 冒烟脚本残留清理（影子函数 / 死变量 / 未设置旋钮）

Status: implemented

Review: LIGHT/2026-09-28/R2=ok

## Problem

三平台冒烟脚本与共享库在 CI 抢修期积累了四类残留。

**影子函数**：`smoke-install-macos.sh` 与 `smoke-install-windows.sh` 各自定义了一份本地 `log_has()`，遮蔽共享库 `smoke-settle-lib.sh` 的同名函数——而两脚本本就 source 了该库。副本已与库分叉（丢掉库版在 `9bd49a3` 补的 `${LOG:-}` 兜底），且副本路径更脆：`LOG` 未设时会因 `set -u` 直接炸脚本。

**死变量**：`PASS_RE` 在三脚本各定义一次并注入 rpm 容器环境（`docker run -e`），全仓零读者——`PAGE_VERDICT_REQUIRED` 时代的组合串遗骸。

**未设置的旋钮**：`INSTALL_WAIT_SECONDS` 在 windows 脚本充当 env 覆盖口，但全仓无人设置，脚本自身头部也只声明 `SMOKE_WAIT_SECONDS`/`SMOKE_SETTLE_SECONDS` 可覆写——一个只有作者知道的旋钮。

**陈旧断言**：linux 头部同时写着两条互斥的裁决规则——正确的「verdict 只取 auth 硬拦，healthy/unknown/缺行一律交存活 + 见证判定」与错误的「auth / unknown / 裁决未出现皆 FAIL」；后者是旧 `PAGE_VERDICT_REQUIRED` 语义的残留，且与同文件「导航到达只作诊断回显，不判门」直接矛盾。同一段里的「置位腿之外的落定仍只认到达」同属此类（库内 `wait_settled` 是 ①+铸币303+客户端存活，到达从不参与）。

## Decision

- 删两处 `log_has` 影子副本；共享库成为全仓唯一定义。
- 删 `PASS_RE`：三处定义 + 容器 `-e` 注入一并去。
- `INSTALL_WAIT_SECONDS` → 常量 `INSTALL_WAIT=300`（与改前默认同值，无静默改时）。
- 删 macos 冗余 `mkdir -p "$(dirname "$MNT")"`（`MNT="$(mktemp -d)/mnt"`，父目录必已存在）与库内一行重复注释。
- linux 头部裁决段落改写为与代码一致（落定 → 有界重绘窗截图 → 见证判红后仅客户端存活可兜底，两者俱缺才 FAIL）；「mac/win 腿未置位」收窄为「win 腿未置位」——mac 腿的见证门已开门（CI 传 `SMOKE_SHOT_DIR` 并装 convert，脚本内见证红即 `rc=1`）。

## Alternatives considered

- **把 mac/win 的 `log_has` 副本改成库版的逐字拷贝**：落败——两个函数体仍是两处待同步的拷贝，正是本次要清掉的形态。
- **保留 `PASS_RE` 作为诊断串**：落败——零读者；失败尾部的信号由 `echo_nav_lines`/`echo_mint_lines` 承担，与它无关。
- **保留 `INSTALL_WAIT_SECONDS` 旋钮**：落败——无人设置且未文档化；按「旋钮只允许 CI 或文档真正设置」的口径，它是噪音而非能力。
- **连 macos 的 `wait_verdict`/`echo_verdict_lines` 一并清掉**（初判如此）：**推迟**——该机制决定截图时机，而截图是像素见证门的输入；在一条当前为绿的腿上改时序属行为变更，须与共享库收口同批做并逐平台 dispatch 验证，故归入脚本重构批次。

## Consequences

四类残留归零，`log_has` 全仓单一定义。零行为变更（全部为删除 + 一处字面替换），三腿离线自测全绿（37／31／15 断言）。唯一触及行为面的是 rpm 容器腿少一个 env（`PASS_RE`），经确认容器内无该变量引用、`set -u` 下无炸点。

两处残留刻意留下并已记账，随脚本重构批次处理：macos 的 `wait_verdict` 时序机制，以及 rpm 容器腿的外层等待循环——后者与宿主 `wait_url` 同形，去重需把 `wait_url` 提升进共享库（审计初判的「容器手抄等待循环」不成立：容器已 `source /smoke-lib.sh` 并调 `wait_settled`，重复的只是外层循环）。

## Related

- [2026-09-26-page-verdict-gate](../testing/2026-09-26-page-verdict-gate.md)：linux 头部旧断言所属的裁决门口径；本批把头部改写为其现行语义。
- [2026-09-25-smoke-wait-full-after-boot](../testing/2026-09-25-smoke-wait-full-after-boot.md)：`PASS_RE` 的引入处；其正文仍称「原 `PASS_RE` 保留为组合串供失败尾部兼容」，本批删除后该句失实，已同变更按勘误纪律补 `Erratum:` 行。
- [2026-09-27-smoke-watchdog-markers-and-log-drop](../testing/2026-09-27-smoke-watchdog-markers-and-log-drop.md)：库版 `log_has` 的 `${LOG:-}` 兜底来源。

## Testing

- `bash -n` × 4 文件；三腿离线自测 `--self-test` 全绿（linux 37／macos 31／windows 15 断言，0 fail）。
- 静态取证：`log_has()` 定义数 = 1；`PASS_RE`、`INSTALL_WAIT_SECONDS` 全仓零命中。
- 三平台 dispatch 实跑在批次外确认（结果回写 HANDOFF 待办；受影响面仅上述容器 env 与 mac/win 的 `log_has` 分支）。
