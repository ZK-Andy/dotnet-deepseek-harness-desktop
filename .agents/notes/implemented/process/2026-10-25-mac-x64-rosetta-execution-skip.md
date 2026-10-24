# Agent Note: mac x64 冒烟执行层跳过（x64-on-Rosetta）与三平台 tee 双写

Status: implemented

## Problem

package 冒烟 mac x64 腿在 GH-hosted runner（Intel runner 已于 2025-12 退役，ARM 硬件）上以 Rosetta 转译执行 x64 应用，SIGILL 三连（run 40109768884/40116948207/40121587215，均启动 ~15-18s 死于 update 检查后，`Illegal instruction: 4`）。对照实验（run 40124270810，Ryn 宿主同 commit 环境重跑）五腿全绿、UI 渲染过像素见证——崩点锁定在 Avalonia 原生栈 × Rosetta 转译层，非 .NET 通用路径。上游缺陷域：AvaloniaUI#10622（Rosetta 转译器 `StateRecovery.cpp` 断言崩，被 .NET 维护者归入 dotnet/runtime#89272）。生态调研（2026-10-25）：WalletWasabi 用付费 `macos-15-intel` runner 跑 osx-x64；Avalonia 官方 CI（`macos-26` 原生）不跑 x64-on-Rosetta 应用；AvaloniaUI#21807 证明组合可跑通但不稳。执行层在该形态下无真值（永红腿会淹没真打包回归）。另：三平台冒烟应用输出全重定向进文件，step 日志等待期只有心跳行——冒烟过程不可见（用户底线：日志必须实时正确）。

## Decision

- mac 冒烟在「主机 arm64 且主二进制 x86_64」形态跳过执行层：`smoke_launch_skipped_on_rosetta`（`file` 魔数实测判二进制架构，非 env 依赖，本地/CI 同判；universal 二进制防误跳）→ 挂载/安装/主二进制在位断言后收绿退出，写真实 skip 说明进 `SMOKE_LOG_DIR`。产物层验证全保留（PayloadSmoke 探针/dmg 布局断言在 workflow 前置步骤，此前已绿）。截图 artifact 预期为空——上传步既有 outcome 门（成功+空=warn）已兼容，workflow 零改动。
- 三平台（mac/linux-deb/linux-rpm/win）冒烟启动行改 tee 双写：应用输出实时进 step 日志 + 全量落文件（`> >(tee -a …) 2>&1 &`，pid 语义不变）。rpm 腿的日志文件随之为「dnf 安装输出 + 应用输出」组合（启动行不再截断，原防截断补偿拷贝删除；dnf 行不命中任何判定正则，评审实证）。
- 自测夹具：Rosetta 判定四例（arm64 主机+x86_64 跳过/arm64 二进制不跳/x86_64 主机不跳/universal 不跳，Mach-O/fat 魔数文件实测 `file` 判定）。

## Alternatives considered

- **付费 Intel runner（WalletWasabi 同款 `macos-15-intel`）**：生态唯一「CI 真验证 x64 执行」方案；弃——按分钟计费需 larger runner 配置，用户拍板不采。
- **修触发面（lldb 拿崩点→换渲染后端/依赖版本）**：崩点若在 Rosetta 转译断言域（#10622 形态）则不可修（Apple 代码）；确定性 SIGILL+随机缺陷史表明投入不可控；用户拍板不采。
- **钉旧 ARM 镜像（macos-15）对照**：仍在 Rosetta 域非根治；弃。
- **仅移除不判形态**（x64 腿一律不启动）：真 x64 硬件（本地 Intel Mac）上执行层有真值，形态判定保留其可达性；弃。

## Consequences

- x64 腿 CI 信号恢复真值：此后红=真打包/安装回归。五腿应全绿（执行层跳过后无已知红面）。
- x64 包「执行层能跑」零 CI 验证记录（Intel 真机验证本就长期缺位，发布面口径既有风险——不新增也不减少）；Apple Silicon 用户误装 x64 包仍可能踩 Rosetta 崩溃（arm64 包为正路分发）。
- 冒烟过程三平台实时可见：等待期 step 日志含应用输出全量（此前只有 15s 一行心跳）。
- `file` 判定依赖 runner 镜像自带 file（macOS 系统自带）；CI 实跑 dispatch 验证跳过路径与 tee 形态。
