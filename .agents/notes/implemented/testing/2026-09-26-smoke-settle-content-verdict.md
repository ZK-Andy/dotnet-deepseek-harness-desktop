# Agent Note: smoke-settle-content-verdict（落定等待 + 第二跳断言）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok（R1 5 Suggestion、R2 1 Blocker+3 Suggestion、R3 2 Suggestion 全收口）

## Problem

v0.5.7 tag 实跑证明冒烟 verdict 与截图都在导航落定之前开火：mac `verdict 03:52:20.43`，导航到达 `21.01/.02/.04`，截图拍到的不可能是第二跳，只能是第一跳裸 `/` 的 auth 文本，而 verdict 却报了 full-chain。到达（传输层）被当成了内容就绪，误报成立。同时 `SMOKE_WAIT=1320` 在静默在线供应（首启 `npm i -g` 全程无输出）下烧满 runner 时间：mac 5分46秒、Windows 19 分钟零输出，分不清"慢"与"死"。

## Decision

三冒烟脚本（linux deb/rpm 两路、mac、windows）同改：

- 部分取代 [`smoke-wait-full-after-boot`](2026-09-25-smoke-wait-full-after-boot.md)（等①即 verdict → 等① **+ 落定**才 verdict）与 [`smoke-runner-deepening`](2026-09-25-smoke-runner-deepening.md)（verdict/截图先例收紧为落定后 verdict + 内容雏形断言）；前序两者保留独立价值，不归档。

- ①命中后进入落定等待（`SMOKE_SETTLE_SECONDS`，默认 90s）：`[nav] 导航已到达` 去重计数 ≥2 **且**含 `?token=` 第二跳，才 verdict full-chain + 截图 + 壳输出尾部。
- 落定超时或落定期进程退出 → 直接 FAIL（fail loud）：dsh 已就绪但 UI 未落定即真实事故，不再按 full-chain 放行。纯②路径（超时/退出）语义不变，仍按 install-chain PASS。
- 心跳：主循环每 60s 打印已耗秒数 + OUT/host.log/DSH_HOME 体积，"慢"与"死"可区分。
- `SMOKE_WAIT` 1320→720：单轮尝试预算（步超时 10 分钟 + 120s 余量），重试轮不计入——冒烟只等首轮落定。耦合注释同步重写。
- 纯函数自测：三脚本 `--self-test` 断言 verdict/落定/心跳/回退门（夹具日志），回归可本地跑；linux 外加 `wait_url` 五态矩阵。
- 落定原语单源：`scripts/smoke-settle-lib.sh` 承载 NAV 正则 + `log_has/nav/wait_settled/heartbeat/timeout_fallback`，三脚本 source，rpm 容器经 docker `-v` 挂载后 source（R1：禁止手抄复刻）。
- 超时回退门 `timeout_fallback()`：未见①但见②才按安装链收工；①已见 + 落定失败保持 FAIL（R2 B1：旧条件把落定 FAIL 翻回 PASS，正是本批要消灭的误报）。

## Alternatives considered

- **维持 1320**：落败——实证单轮上限约 6 分钟（mac），Windows 的 19 分钟是步超时 + 静默重试叠出来的，等更久买不到信息。
- **截图 OCR 断言内容**：落败——runner 无 OCR；第二跳到达 + 人眼截图是现有条件下唯一可达的判定 rung，cookie 落定问题留产品侧跟进。
- **新增第三 verdict 态（如 full-chain-unverified）**：落败——"未验证的 full-chain"正是本次 bug，用 FAIL + install-chain 二态加 fail loud 更清晰。

## Consequences

- dsh 就绪但导航 broken 的冒烟从绿变红（预期内变严格）；Linux 无显示与 Windows 旧边界（①前退出）路径零变化。
- tag 流水线最坏耗时减半（22→12 分钟窗）。

## Testing

- `bash -n` 三脚本 + lib + `--self-test` 全绿（mac/win：verdict/落定/心跳/回退门三向；linux：外加 wait_url 五态矩阵；rpm 容器内分支零自测覆盖——只在 fedora 容器内执行，缺口在此诚实记录）。
- 自测抓到真 bug 两枚：①大括号 grep 组在 `pipefail` 下空文件致组状态 1（`|| true` 锚定）；② mac/win 超时回退把落定 FAIL 翻回 PASS（`timeout_fallback` 加锁，R2 B1）。
- 真验证在 CI tag 跑：verdict 行 + 落定后截图 artifact 人眼复核（mac 首验）。
