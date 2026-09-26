# Agent Note: smoke-linux-xvfb-fullchain（Linux 冒烟 Xvfb 全链）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 兑现 [`smoke-runner-deepening`](2026-09-25-smoke-runner-deepening.md) 的"Linux Xvfb 全链下批事项"；[`smoke-settle-content-verdict`](2026-09-26-smoke-settle-content-verdict.md) 的落定等待在 Linux 腿的生效前提。

## Problem

Linux 冒烟在 CI 无显示环境只能到②安装链：壳在 Ryn Run 建窗即退出（GTK 需 display），引导后台任务随之夭折，①全链不可达；截图 best-effort 因 `$DISPLAY` 为空恒跳过。verdict/落定/截图三件套在 Linux 腿从未实跑过。同时 mac-x64 FAIL（WebView 空白，hop2 未提交）疑似 `NavigateAsync` 挂起，P0（ADR `webauth-token-reentry`）已埋发起/返回诊断行，缺判读口径。

## Decision

- CI `package-linux.yml`：apt 加装 `xvfb` + `scrot`；安装冒烟步骤改经 `xvfb-run -a -s "-screen 0 1280x1024x24"` 运行（DISPLAY 自动分配；`SMOKE_SHOT_DIR` 透传，截图在 Xvfb 下真实开火）。
- `smoke-install-linux.sh`：注释更新（Xvfb 下批事项兑现）；`smoke_deb` 启动行打印 DISPLAY 状态（有/无）供判读；等待/落定/回退语义零变化（无 DISPLAY 直跑仍按②收工；xvfb-run 本体失败则整步失败，不掩盖环境故障）。
- rpm 容器腿不动（容器内无 X，仍②收工）。
- x64 诊断判读口径（`NavigateAsync` 挂起 / commit 信号缺失 / renderer 无响应三态，对应发起无返回 / 有返回加提交超时 / 探针超时）家在本 ADR Decision（cookbook 无余量，见 Consequences）。

## Alternatives considered

- **容器内也加 Xvfb**：落败——fedora 容器需另装 Xvfb + X socket，diff 与验证面翻倍；deb 腿先验证，有收益再扩。
- **Xvfb 失败即硬 FAIL**：落败——Xvfb 起不来是环境问题不是产品事故；②收工保留，FAIL 只留给①已见 + 落定失败（既有回退门语义）。
- **改落定语义迁就无显示**：落败——落定是内容就绪的唯一判定 rung，为 CI 无显示放宽等于重开误报口子；反向是给 CI 补显示（本决定）。

## Consequences

- Linux deb 腿首次能 verdict full-chain + 落定后截图；CI 耗时预计 +1 分钟内（apt 增量 + Xvfb 启动）。
- rpm 腿仍②（缺口在此诚实记录）；真验证待 `workflow_dispatch` 跑 package-linux（见 Testing）。
- cookbook 未加条目：判读口径单一家在本 ADR（cookbook 基线 2698/2700 无余量，加条必超预算；提额度留 PR 理由）。

## Testing

- `bash -n` 三脚本 + lib + `--self-test` 全绿（等待/落定语义零变化，自测矩阵不动）。
- 真验证在 CI：`workflow_dispatch` 跑 package-linux，看日志 DISPLAY 行 + verdict + 截图 artifact 人眼复核（deb 腿预期 full-chain 或 fail loud，不再是恒②）。
