# Agent Note: bundled-plugin-version-aware-catalog

Status: implemented

## Problem

版本感知升级范围仅 `dsh-desktop-companion`（2026-08-22 拍板），`dshmarket` 在启动装配中只判「在不在」不判版本。实证后果（2026-08-25）：闭包钉版 `dshmarket-1.15.0` 与 npm latest `1.26.0` 落后 11 个 minor，存量用户的 profile 无任何自动通道跟上；同时仓库没有定时巡检，钉版漂移只能靠人偶然发现。

## Decision

随包插件**清单化 + 装配判定循环化**：`BundledPluginCatalog`（包名 + spec 解析器的单一清单，`Services/BundledPluginCatalog.cs`）承载清单成员，启动装配经 `AssemblePending` 对清单逐项执行统一判定——未装即装、闭包版本更新即升、同版或更高跳过（绝不降级）。清单 = `dshmarket` + `dsh-desktop-companion`；解析器异常与脏版本串均按单插件记日志跳过，隔离粒度与版本比对段一致。

两层决策解耦：**「是否随包」**（逐案拍板，决定清单成员）与**「是否版本感知」**（凡随包即感知，不再逐案）。`@anysearch/anysearch-dsh` 明确不随包、维持用户自装自管（2026-08-25 用户拍板），故天然不在清单内。

## Alternatives considered

- **保守档**（清单仅基础设施类，功能型第三方缓收）：落败——当前清单成员下两档行为完全等价，差别只在「未来新第三方是否默认获得版本感知」。解耦后该分歧归「是否随包」层逐案处理，「是否版本感知」层无需保留两档。
- **构建时浮动 latest**：落败——破坏可重复构建与零下载确定性，与 v0.2.0 闭包缓存陈旧事故是同类教训；钉版 + 自动信号才是正交组合。
- **CI 定时巡检（freshness workflow 开 issue/PR）作为替代**：落败（本回合不实施）——它解决「构建时钉版新鲜度」，管不了存量已装基座；两者正交，巡检可日后另行立项。
- **维持现状**：落败——落后 11 个 minor 且无感知通道，实锤复发中。

## Consequences

- 收益：发版换钉版后，存量机器首次启动自动拉齐全部随包插件；未装/副本损坏的自愈行为从扩展到清单全体。
- 代价/风险：第三方插件新版行为变化直达用户（缓解：`[host]` 升级日志留痕，不做自动回退）；若闭包钉版比用户手动装的 registry spec 更新，重装会把依赖从 registry spec 拉回 `file:` 随包语义（随包哲学的体现，日志说明）；多插件同批升级共用单次 spawn + 单次重启（既有管线不变）；pnpm `minimumReleaseAge` 整锁拒绝由既有放宽重试兜底。
- 延续约束：「改插件必须 bump version」纪律从 companion 扩展到清单全体。
- 验证：`dotnet test` 245/245 全绿 0 警告（基线 233 + 新增 12 用例：未装即装不读版本 / 落后即升带版本日志 / 同版与更高跳过 / 副本缺失修复重装 / spec 缺失跳过 / 解析器异常单项隔离 / registry 回退放弃升级检查保留首装 / 脏版本串 fail-loud 日志跳过且不拖垮其余插件 / 待装顺序随清单 / 真实闭包布局端到端与空闭包回退）；三门禁全绿；两轮评审（简化审查 + 代码评审）发现项全部收口。

## Related

- `2026-08-22-companion-plugin-version-aware-upgrade`：本机制的前身；其 Decision 中「范围仅 companion、dshmarket 不纳入」的范围限定由本笔记取代，机制三件套（ReadBundledVersion / ReadInstalledVersion / NeedsUpgrade）原样复用。
