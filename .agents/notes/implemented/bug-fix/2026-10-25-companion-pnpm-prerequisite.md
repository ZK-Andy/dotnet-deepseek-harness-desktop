# Agent Note: companion 装配的 pnpm 前置补齐与转发 shim 清理

Status: implemented

Review: FULL/2026-10-25#2/R1=ok R2=ok R3=ok; hotfix3 LIGHT/2026-10-25#4/R2=ok（跨平台测试形态修复，用户指令「全修」）

## Problem

随包 companion 装配在无 pnpm 环境系统性失败：`dsh plugin add` 内部 `spawnSync("pnpm")` 需要 PATH 上有真 pnpm，而「简单壳」（ADR simple-shell-single-global-dsh，8-31 取消自备运行时）后该前置悬空——引导链只用 node 自带的 npm 装 dsh，pnpm 从未被安装。壳写的 `~/.local/bin/pnpm` 是**转发脚本**（转发用户自有 pnpm，缺则打 `pnpm not found` 退出 1）——CI 日志里的失败消息正是它打的。实证：CI 失败形态三平台一致（linux/win 实测 run `40127406189`；Ryn 宿主同环境同败 run `40124270810`——pnpm 前置缺失自「简单壳」（8-31）起未变）；「更早 run 从未装上」【推断 · 未证：基于前置结构未变的推断，更早 run 未回溯】；「用户实机因 companion 早期在位（「未就位才装」）未暴露失败」【推断 · 未证：实机历史日志未取证】。残留 shim 还有遮蔽风险：PATH 中 `~/.local/bin` 排在 npm 全局 bin 前时，转发脚本会遮蔽引导新装的真 pnpm。

## Decision

- **装配前置 ensure-pnpm（有就用，没有就引导安装，与系统全局 node 同语义）**：`EnsureBundledPluginsBeforeSpawnAsync` 在存在待装插件时先 `pnpm --version` 探测——可用即用（记版本）；未检出则 `npm install -g pnpm` 装到与 dsh 同一全局位后复验。两条装配路径（首启引导/正常启动 spawn 前）经同一装配器自动覆盖；探测/安装经注入的进程委托（可单测）。两个环境事实的兜底（run `40136296983`/`40137230061` CI 实证）：①可执行缺失时 .NET `Process.Start` 抛 `Win32Exception` 而非返回非零——按 127 收口视为未检出；②npm 壳脚本在部分安装形态下相对解析断裂（`/root/.local/bin/npm` 报 Cannot find module '../lib/cli.js'）——系统全局 node 就位时安装命令按引导 InstallDsh 同款**显式形态**（node + npm-cli.js 全路径，`TryResolveNodeAndNpmCliForBinDir`）驱动，否则回退裸名；③引导装完 node 后幂等补一次进程 PATH 暴露（构造期早调在全新环境必然 no-op——node 未装），后续裸名 pnpm 探测/复验由此可解析。④测试断言不得耦合平台 psi 形态（run `40139317570` windows 腿六红实证）：`BuildToolPsi` 一律经 `RelayFor`——Windows 上全部裸名/显式命令都被包成 `cmd.exe /d /s /c <cmd> <args>`，按裸名形态写的命令序列断言与探测判定在 Windows 必错位；修=测试侧 `CommandOf` 规整化（剥 cmd.exe 中转还原逻辑命令，与 `RelayFor` 互逆钉真）+ 夹具按平台布局造 node/npm-cli（`NodeBinDir`/`NpmCliRelativePath` 同款双形态）+ `TryResolveNodeAndNpmCliForBinDir`/`TryLocateNodeAtPrefix`/`NpmCliRelativePath` 加 windows 注入重载（`DshShimPath` 先例）——Windows 语义在非 Windows 本机可钉，不赌 CI 矩阵。
- **撤 pnpm 转发 shim**：CliShimPlanner 不再生成任何 shim 文件（CliShimFile/CliShimBuilder/写入决策链整体删除）——转发脚本不是正常形态（用户判定，2026-10-25）。
- **启动清理历史自产残留**：注册时扫描 bin 目录的 `pnpm`/`pnpm.cmd`/`pnpm.ps1`，按 GeneratedMarker 识别自产即删；悬空符号链接（目标已失效）无论出处直接清理——用户自放的有效同名文件保留。
- **保留**：系统全局 node bin 目录的终端 PATH 暴露（桌面与终端共用 node/dsh，独立功能，与转发脚本无关）；binDir（~/.local/bin）不再进入注册的 PATH 增量。
- 日志文案如实化：cli-shim 日志不再声称「已注册终端命令（pnpm）」，改为 PATH 暴露与残留清理的实况。

## Alternatives considered

- **恢复自备运行时（捆绑 pnpm）**：与「简单壳」决策正面冲突、体积与维护面回潮。弃。
- **推动 dsh 侧自举 pnpm**：上游行为非我方可控，且 dsh 官方语义就是「宿主环境提供」。弃。
- **转发 shim 升级为自动下载 pnpm**：职责异化（转发器变安装器），掩盖环境事实。弃（用户判定转发脚本本身即不正常）。

## Consequences

- 无 pnpm 环境（CI、普通用户）companion 恢复可装；引导链多一步 pnpm 安装（仅在缺时装）。
- 升级用户机器上的历史 shim 被启动清理移除；用户自放的有效 pnpm 文件不触碰（悬空链接除外——失效物无论出处都清）。
- npm 也不在 PATH 的异常形态：ensure 失败 → 装配放弃走既有 best-effort（日志可见，下次启动重试）——产品面仍不因缺插件阻断桌面启动。

增量闸豁免：gross ≤ 1000，fanout ≤ 15（本批三面联动——pnpm 前置/清理 C# 面 + 冒烟判门脚本面 + 两测试矩阵重写，行数大头是测试与 CliShim 文件重写的对等增删；一次性豁免，不放宽长期帽）。
