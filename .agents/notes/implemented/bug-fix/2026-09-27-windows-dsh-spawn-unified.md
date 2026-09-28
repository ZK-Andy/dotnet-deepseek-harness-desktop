# Agent Note: Windows dsh 启动统一走 cmd 中转与首启可观测性

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

三审结论：R1 组合根未碰；R2 依赖方向不变（同程序集内引用，Plugins 对 Runtime 的既有边不变）；R3 新增 cmd.exe 外部交互收敛在纯 helper 内，环境净化/重定向语义沿用既有 psi 构造；D001/D002 命名合规，catch 均具名收口；0 Blocker。

> 吸收合并（2026-09-28，E 批 ADR 裁定）：`windows-bootstrap-dsh-path-and-stall-watchdog` 并入本篇。仍成立的部分：VerifyDsh 双通道回退（PATH `dsh --version` 失败 → npm 全局 bin 垫片绝对路径直验，文件存在性先行，垫片可跑才 `PrependPathToProcessEnv`，两通道各报一行进度）与 npm 安装进度进 host.log（`--progress=true` 经 `PumpAsync` 以 `[bootstrap] npm>` 前缀留痕；CI 非 TTY 下 gauge 不可靠是本篇补的实证）。已被本篇取代的部分：其「三体积零增长看门狗」改判内容标记（见 Decision）。其脚本面 Erratum（`smoke-settle-lib.sh` 拆并至 `scripts/lib/smoke-wait-lib.sh`/`smoke-verdict-lib.sh`，见 [script-layer-consolidation](../process/2026-09-28-script-layer-consolidation.md)）随之由本篇承接。

## Problem

dispatch 实证（09:50:14）：npm exit 0（512 包 5 分钟）+ 前缀解析正常 + PATH 已暴露，`run: dsh --version` 抛 `Win32Exception`（系统找不到文件），引导失败耗时 304s。根因不在 PATH 内容，在启动语义：`UseShellExecute=false` 的 CreateProcess 对裸名只试本名 + `.exe`（不走 PATHEXT），npm 在 Windows 只生成 `dsh`/`dsh.cmd`/`dsh.ps1`（无 `.exe`）——裸名永远起不来。同病五处：VerifyDsh 主通道（异常未捕获，fallback 永不执行）、F1 垫片直跑（`dsh.cmd` 报 193）、`HarnessRuntimeHost.BuildStartPsi`（真正的 `dsh web` 位，修完 Verify 的下一堵墙）、`RuntimeVersionGate.BuildProbePsi`（catch 保命，仅跳过底线检查）、`MarketInstallHelper.BuildPsi`（nodeExe 为 null 分支）。附带两实证：`--progress=true` 在 CI 非 TTY 下 5 分钟零行（gauge 不可靠）；体积看门狗被 315s 杂项字节清零（量错了东西）。

## Decision

- `DshCommandFor` 纯 helper（`Pure.cs`，五处裸名 spawn 的唯一家）：Windows 经 `cmd.exe /d /s /c` 中转垫片（无 binDir 用裸 `dsh.cmd` 走 cmd 自身 PATH/PATHEXT，有则绝对路径），Unix 不变；平台可注入重载供单测断言 Windows 引号形态。
- 五处同改走 helper；VerifyDsh 主通道异常（非 OCE）同样进 fallback（report 具名行，无空 catch）。
- spawn 审计附带：`BuildCapturePsi` stdin 重定向 + 启动即关（Tauri `stdin(null)` 同款）；npm 步内存活自报（60s 一行，与 npm 输出无关）。
- CI 只缓存 npm 下载缓存，不缓存安装树；InstallDsh 保留步超时。
- 看门狗改看内容标记（`bootstrap|host|shell|nav|update|health` 前缀行），杂项字节不清零。
- 承认并修正 `bug-fix/2026-09-26-npm-global-bin-path` 第 26 行结论（“不再死于 PATH”不成立：补 PATH ≠ Windows 可启动）。

## Alternatives considered

- **node 直跑 dsh JS 入口（官方/++;Tauri 同款）**：落败——需先知包布局（读已装 dsh 的 package.json 取 bin 目标）+ `BuildStartPsi` 等同步调用点要异步解析前缀，改动面比 cmd 中转大；cmd 方案零布局知识、CI 烟即真机验证。
- **`UseShellExecute=true`**：落败——丢 stdout/stderr 重定向，整套捕获/诊断链重写。
- **安装时 `--prefix` 强制统一前缀**：09-26 已否决（改写用户 npm 布局），维持。
- **SMOKE_WAIT 砍到 3 分钟**：落败——冷装单步 5 分钟实证在案，砍窗误杀慢机；正确修法是验证打通 + 慢可见 + 只杀真停滞。

## Consequences

Windows 首启：npm 装完即经 cmd 垫片自验自愈，`dsh web` 启动位同语义打通（①可达）。代价：Windows 进程树多一层 cmd 父进程（孤儿清扫 kill-tree 已覆盖整树）；cmd 引号规则依赖参数表无手拼串（helper 单测锁死，CI 烟作执行验证）。

## Testing

- 历程 dispatch 痕迹：run `36310235841` 为 Problem 现象的实证 run。
- 新增 `DshCommandTests` 四态（Unix/Windows/空格前缀/映射）+ fake 抛 Win32Exception 复刻本跑 + 存活循环取消单测；Infrastructure 全套件 491/491。
- 三冒烟脚本 `--self-test` 全 PASS（看门狗改标记行回归）。
