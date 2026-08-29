# Agent Note: bundled-plugin-registry-normalization

Status: implemented

## Problem

随包 dshmarket 以 `file:` spec 安装（`dshmarket-background-install` 的离线可靠选择），但 `file:`/`link:` 形态被 dshmarket 上游按「本地开发安装」对待：更新检查（`lib/updates.js` 对该形态直接返回 `linked`/`updateAvailable: false`，不发网络请求）与设置卡自更新入口永久失效——随包用户的市场与自装用户不等价（2026-08-29 实机 + 上游 1.15.0/1.31.1/1.36.0 源码考古实证；上游 1.31.x 还把市场自身从已安装列表按名过滤，该条与安装源无关、不可由我们修复）。同时 `bundled-plugin-version-aware-catalog` 的既定后果「闭包钉版更高时把 registry 安装拉回 `file:`」会在用户手动归化后再次关死更新通道，等于把牢笼焊死。

## Decision

**随包 = 种子，不是牢笼**（2026-08-29 用户拍板）：

- 随包 tgz 降级为离线种子：首装与离线升级仍走 `file:` tgz，保离线可靠。
- **registry 归化**：`AssemblePending` 对开启 `NormalizeToRegistry` 的清单项（当前仅 dshmarket），检测到已装且 profile dependencies spec 为本地形态（`file:`/`link:`）、且闭包不比已装更新时，入列归化条目（spec = `dshmarket@latest`——裸名对既有依赖是 pnpm 幂等 no-op、spec 永不翻转，v0.3.12 实机实证后修正）。归化失败（离线/registry 错误）留痕下次启动重试，幂等（成功即 registry 形态不再入列）。归化 spec 用 latest 而非钉版——与用户自装逐字节等价（拍板）。
- **registry 形态完全放手**：已装 spec 非 `file:`/`link:` 一律跳过，即便闭包钉版更高也不回拉 `file:`（拍板）；dependencies 值不可读（null）同样保守不碰；companion 无 registry 上游，不归化，随包是唯一分发面。
- 消费点分组：待装条目带 `FromRegistry` 标记（随包 tgz/目录路径 = 本地组；归化条目/registry 回退串 = registry 组），本地组先装、registry 组后装——pnpm 单事务多 spec 一败俱败，离线时归化失败不得拖累随包种子安装。

## Alternatives considered

- **保留回拉**（registry 形态但版本低于闭包钉版时拉回 `file:`）：落败——与「跟自装一样」的目标直接矛盾，市场更新检查会再次哑掉。
- **归化用钉版 spec（`dshmarket@<MARKET_VERSION>`）**：落败——起点确定性好，但用户要求与自装等价；终态本就收敛（归化后市场自管、更新检查对比 npm latest），钉版只多了不可达的确定性。
- **构建时浮动 latest / 首装直装 registry**：落败——破坏离线首启与零下载确定性（`dshmarket-background-install` 已实证）；种子模型下闭包钉版可重复构建的性质不变，归化只是用户机器上的运行时状态。
- **维持现状**：落败——随包用户的市场自更新永久失效，实机已复现。

## Consequences

- 收益：归化后 dshmarket 与自装完全等价——上游发新版市场内即提示，用户可自更，不再等桌面发版；`MARKET_VERSION` 钉版语义收窄为种子/离线升级钉版（freshness 巡检职责不变）；在线用户不再依赖 bump 节奏跟进上游。
- 代价/风险：归化引入一次额外后台 spawn + dsh 重启（仅 file: → registry 的一次性迁移，幂等）；归化装到的 latest 版本不经我方验证矩阵（缓解：与用户自装同风险面，且随包种子仍走钉版）；已知边界——归化到 latest 理论上可能低于本地形态已装版本（用户手工 `file:` 装过比 registry 新的产物，窗口极窄且市场自更新随即追平）；闭包无 tgz（解析器回退 registry 串）时版本不可比，升级与归化一并保守放弃，下次闭包带 tgz 即自愈；离线用户每次启动多一次失败 spawn 的日志噪音（缓解：后台任务，不阻塞）。
- 延续约束：未来新增随包第三方插件（有 registry 上游）默认开启 `NormalizeToRegistry`；无 registry 的自研插件（companion 形态）不开。
- 验证：`dotnet test` 387/387 全绿 0 警告（基线 365 + 新增 22：spec 形态判定矩阵 8 例 + 路径/registry 串分组矩阵 6 例 + ReadDependencySpec 2 例 + registry 放手/不可读保守跳过/归化/离线升级互斥/回退串放弃/分组契约 6 例）；三路评审 R1/R2/R3 收口（R2 Blocker——分组谓词误用 `IsLocalSpec` 致本地组恒空——已改 `FromRegistry` 数据驱动并补分组契约测试）。

## Related

- `2026-08-25-bundled-plugin-version-aware-catalog`：本笔记修订其 Consequences 中「重装把 registry spec 拉回 `file:`」一条——registry 形态现改为完全放手；未装即装/闭包更新即升的机制三件套原样保留。
- `2026-08-20-dshmarket-background-install`：随包 tgz 种子的来源（离线首启可靠），本笔记在其之上叠加归化层，不取代。
