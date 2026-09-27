# Agent Note: Windows 首启 dsh 不在 PATH 与冒烟无进展等待

Status: implemented

Review: LIGHT/2026-09-27/R2=ok

R2 自审结论：改动收敛 Infrastructure Runtime 适配器 + 测试 + 三冒烟脚本，未碰 Core 与组合根，依赖方向不变；fallback 只在 PATH 验证失败后触发且文件存在性先行，原成功路径早返 untouched；看门狗只统计字节增长，真慢机天然复位；0 Blocker。

## Problem

Windows 首启卡在引导页、CI 冒烟耗满 720s 后按②安装链保底绿（run `36307412722` 实证：② 1s 即见、①永不出现、截图停在 Starting 页）。v0.5.7 基线截图（run `36221929554`）定点更准：InstallDsh 绿、VerifyDsh 红，`trying to start process 'dsh' … The system cannot find the file specified`——npm 装完 dsh，但 GUI 进程 PATH 里没有它。等待预算本身无辜，病根是验证通道单一（只认 PATH 裸名）+ 安装期零日志（`--loglevel=error` 下数分钟零行）+ 等待循环无停滞判定。

## Decision

- VerifyDsh 双通道（`RuntimeBootstrap.cs`）：PATH `dsh --version` 失败后，回退 npm 全局 bin 垫片绝对路径直跑（Windows `dsh.cmd`、Unix `dsh`，`NodeBinDir` 复用既有映射）；文件存在性先行，不猜测执行；垫片可跑才 `PrependPathToProcessEnv`，后续子进程可直解；两通道各报一行进度（`PATH 未命中…` / `垫片直验通过…`）。
- npm 进度进 host.log（`RunNpmInstallGlobalAsync`）：加 `--progress=true`（`--loglevel=error` 保留），进度行经既有 `PumpAsync`（`\r` 刷新行已处理、300 字符截断）以 `[bootstrap] npm>` 前缀进 host.log，冒烟侧可区分慢与死。
- 无进展看门狗 + 心跳加密（`smoke-settle-lib.sh`，三冒烟脚本同形接线）：心跳 60s→15s 并附缺项摘要；`progress_watchdog_tick` 以 OUT/host.log/dsh-home 三体积连续 300s 零增长判停滞，提前 fail loud（dumps 同失败规格）；停滞跳出不触发②回退（零进展的②是死不是慢）。

## Alternatives considered

- **跟 DSH 官方把 dsh 打进包内**：官方 `electron-builder` 把 dsh 物化进 `app.asar` + `runtime/`（`verifyDesktopRuntime` 验包），天然无 PATH 问题。落败原因：与 online-first 拍板（依赖系统全局 node + 全局 dsh）方向相反，已拍板事项不重议。
- **跟 hairyf 安装期注册 dsh 进 PATH**：hairyf 首启装配时把 `dsh` 注册进 PATH。落败原因：我方已在运行时做等价事（`PrependPathToProcessEnv` + 本批垫片直验），效果相同，无需动安装器。
- **收紧 SMOKE_WAIT 到 3 分钟**：落败原因：v0.5.7 实证 npm 安装单步可达 307s+，砍窗只会把慢机误杀；正确修法是 F1 让验证通过 + F2 让慢可见 + F3 只杀真停滞。
- **看门狗只看日志不看 dsh-home**：落败原因：下载类进展（node 归档落盘）不一定打日志行，三体积 OR 语义（任一增长即活）误杀面最小。

## Consequences

Windows 复用预装 node 且 npm 前缀 bin 未进 PATH 的机器，首启经垫片通道自愈（一次 PATH 补齐，终身直解）。CI 冒烟：真慢机（npm 进度滚动）不再被误判；真停滞 5 分钟即红并带三件套证据，不再烧满 720s。代价：VerifyDsh 最坏双倍步超时（两通道各一预算），总额仍受冒烟窗约束。

## Testing

- 新增 `RunAsync_VerifyDsh_FallsBackToNpmBinShim_Succeeds`（PATH miss → 前缀查询 → 垫片直验 → PATH 暴露全链）+ `RunAsync_NpmInstall_EmitsProgressFlag`；Infrastructure 全套件 485/485。
- 三冒烟脚本 `--self-test` 全 PASS（含新增看门狗四态：首活/窗内活/满窗死/增长复位）。
