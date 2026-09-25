# Agent Note: smoke-wait-full-after-boot（②后继续等①）

Status: implemented

Review: LIGHT/2026-09-25/R2=ok（0 Blocker、1 Suggestion 已收口）

## Problem

三平台冒烟是双信号（①`[host] dsh web =` 全链 / ②`[bootstrap] 引导开始：`安装链），命中其一即 PASS 打 verdict 收工。Evergreen 落地后 Windows runner 窗口已渲染（Ryn running + 引导页到达），①可达，但脚本在②命中当秒即停，verdict 恒为 install-chain，全链证据永远等不来。mac 有 WindowServer 会话同理。Linux 无显示（进程在窗口创建即退出）仍是边界，不在本改收益面。

## Decision

②命中后不再收工，继续等①至超时或进程退出：

- 主循环拆双判据（`FULL_RE` / `BOOT_RE`，原 `PASS_RE` 保留为组合串供失败尾部兼容）。①命中即 full-chain 收工；②首次命中只打印该行 + `note: 已见②安装链，继续等①…` 留痕（含已耗秒数），不退出。
- 终局 verdict 按最佳信号：超时走满 `SMOKE_WAIT` 仍只有② → install-chain PASS（语义不变，只是等满窗）；进程退出 → 按退出时最佳信号 fail fast（补扫一次），Linux 无显示路径因此成本不变。
- 截图在终局点拍（full 在①时刻，install-chain 在超时/退出时刻）；②时刻不拍（窗口未渲染，人眼无价值）。
- 三脚本同改：`smoke-install-windows.sh` / `smoke-install-macos.sh` 主循环；`smoke-install-linux.sh` 的 `wait_url`（deb）与容器内循环（rpm）同款语义。`SMOKE_WAIT` 语义从"等任一"变为"等①的上限，②只定保底"。

## Alternatives considered

- **②后再加固定小窗（如 60s）**：落败——全链耗时是分钟级（node 下载/解压/装树各一步超时），小窗必然截断；复用既有 `SMOKE_WAIT`（已按引导步超时算过）即可，不另设钟。
- **②即 PASS，另起第二 job 蹲①**：落败——两 job 抢安装目录与端口，且 verdict 分裂成两处，人眼归因翻倍；单循环内状态机最简。
- **Linux 去掉进程退出早退、强制等满窗**：落败——无显示下壳秒退，等满 1320s 纯空转；保留退出早退后 Linux CI 成本零增加。
- **②时刻即截图**：落败——mac 有截图无窗口的先例，②时渲染未到；终局单张截图才有人眼价值，中间只留日志行。

## Consequences

- Win/mac 在①不可达时单腿 CI 最坏多等满窗（默认 1320s）；①可达则自动升级为 full-chain，无需再改脚本。
- install-chain 含义收紧为"等满窗/等到退出的仍只有②"，含金量高于改前"见②即停"。
- `SMOKE_SHOT_DIR` 未设时行为与改前一致（截图全跳过）。

## Testing

- `bash -n` 三脚本。
- 模拟时序自测：先②后① → full-chain；只有②至超时/退出 → install-chain；双无 → 失败。
- 真验证在 CI：Win/mac 的 verdict 行 + 截图 artifact；Linux 预期行为不变（deb 退出早退 install-chain，rpm 同）。
