# Agent Note: coverage-via-pure-logic-extraction

Status: implemented

## Problem

测试总覆盖率停在 52.2%（line-rate 0.5216）。UI/平台分支无真机环境写了也验不了，硬补 UI 测试不是出路。盘点覆盖率缺口后发现：**真正的零/极低覆盖集中在平台 IO 胶水与配置解析侧**，而非待办（2026-09-03 代码质量评估拍板）与既有 ADR（[shell-tray-hide-to-tray](../architecture/2026-08-24-shell-tray-hide-to-tray.md)、[shell-convenience-autostart-ready-notify](../architecture/2026-08-24-shell-convenience-autostart-ready-notify.md) 声称可单测的纯逻辑面）里设想的托盘/自启/UDS 面——后者（`TrayMenuActions` 93%、`CloseBehaviorPreference` 100%、`LauncherActivation.SocketPath`、`Autostart.BuildLinuxDesktopEntry/MacOSPlist`、`UpdatePlatform.DetectPackageKind`）大多已被既有测试覆盖。四类真实缺口的共同点：**逻辑与 IO 混在一个方法里**，IO 面 CI 不可达（真弹浏览器/真写注册表/真跑 pkexec），判定面无测试钉住。

## Decision

对四类真实缺口做**纯逻辑面抽取 + 补测**（`UpdateInstaller.InstallCommandFor` 模式推广：平台分支抽纯函数，IO 留在边界）：

- **`UpdateOptions`（1/38 → 全）：抽 internal `Parse(string json)` 纯函数**——把 JSON→Options 的逐键解析（Repository/FeedTimeoutSeconds/DownloadTimeoutMinutes/UpdatesDirName，坏 JSON 回退默认）从 `Load(string baseDirectory)` 的 IO 里抽出；`Load` 只做「读文件 → 委托 Parse」。缺文件/节缺失/损坏语义不变（全默认），外部调用点零改动。
- **`SystemBrowser`（0/27 → 主分支全）：抽 internal `BuildProcessStartInfo(string url)` 纯函数**——返回 `ProcessStartInfo` 的构造分支（Linux `xdg-open` + 重定向 vs 其他 `UseShellExecute`）。`Open` 只做 `Process.Start` + 排空 + 返回。真 spawn 平台分支不测（环境副作用），命令构造形态被测试钉死。
- **`FileReadyPersistence`（0/31 → 全）：补真 IO 往返测试**——ready.json 的读（缺文件→null、合法→记录、缺字段/坏 JSON→null）、写（生成合法 JSON 且可被读回）、清（幂等删除）。这是纯 IO 无平台分支，temp 目录真文件测试无副作用（同 `CloseBehaviorPreference`/`RuntimeBootstrapOptions.Load` 既有模式）。
- **`ReleaseMeta.Pick`（42.9% → 全）：补边界用例**——仓库段防御（他仓 `/owner2/repo2/releases/...` 不匹配）、绝对/相对 href、sha 缺失返回 null、非 download 段 href 跳过。

抽取全部走 internal + `InternalsVisibleTo`（既有 `DeepSeek.Harness.Desktop.csproj` 已开给 Tests），**零公共 API 签名变化、零行为变化**——纯 IO 面抽出判定后原调用点语义逐字节不变。新增 xunit 用例 24 个（UpdateOptions.Parse 缺节/坏 JSON/类型不符 × 11、SystemBrowser 命令构造 × 2、FileReadyPersistence 往返契约 × 7、ReleaseMeta 仓库段防御 × 4）。

## Alternatives considered

- **直接补 IO 测试（真弹 xdg-open / 真写 HKCU / 真跑 pkexec）**：落败——弹浏览器/写注册表/起 root 安装进程是环境副作用，CI 沙箱与实机语义不同（且 `SystemBrowser` Linux 分支依赖 xdg-open 存在），这类测试脆弱且无真机 CI 不可跑，违背「mock 只用于昂贵/非确定性边界」纪律。
- **把缺测的 IO 方法整段标 ExcludeFromCodeCoverage**：落败——掩盖而非补强；IO 判定面（如 xdg-open 命令形态、包后缀选择）恰是易回归点，应钉测试而非豁免。
- **扩到全部平台分支面（含已覆盖的托盘/UDS/自启再细分）**：落败——边际价值低，纯为拉高覆盖数字；已覆盖面的再抽取不改行为也不增加断言信息量。

## Consequences

- **收益**：四类 CI 不可达平台分支的判定面被测试钉死；UpdateOptions 配置解析/FileReadyPersistence 契约/ReleaseMeta 资产选择获得行为回归；覆盖率 52.2%→**53.8%**（+1.6pp）。
- **代价**：`UpdateOptions`/`SystemBrowser` 各多一层 internal 纯函数（约 20 行），代码量微增但职责更清；无真机行为面改动。
- **验证**：build 0 警告、`dotnet test` 449→**473/473**、覆盖率收集 line-rate 0.5216→0.5375、门禁全绿。

## Related

- [test-sdk-coverage-runner-bump](../process/2026-09-03-test-sdk-coverage-runner-bump.md)（implemented）：测试三包升级后覆盖率收集基线（0.5216）；本篇在其上补低覆盖纯逻辑面测试。
- [reference-alignment](../architecture/2026-08-29-reference-alignment.md)（implemented）：其批次一/五确立「判定核心纯逻辑可单测」模式（`MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync` 注入、`PageHealthRecovery` 纯逻辑），本篇沿同一模式推广到配置解析/平台命令构造/文件持久化面。
- [desktop-shell-self-update](../process/2026-08-22-desktop-shell-self-update.md)（implemented）与 [self-update-review-hardening](../bug-fix/2026-08-22-self-update-review-hardening.md)（implemented）：ready.json 持久化契约的主 note（状态机 ready 记录 + 跨启动恢复）；本篇 FileReadyPersistence 往返补测实为该契约族的 IO 面测试，双向交叉引用。
