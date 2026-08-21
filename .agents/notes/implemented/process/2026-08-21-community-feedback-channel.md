# Agent Note: community-feedback-channel-for-real-device-testing

Status: implemented

## Problem

`mac x64` 与 `Windows` 真机手测处于「等待社区支持」状态（见 `testing/2026-08-20-community-targeted-testing`），但仓库缺少低门槛的社区反馈入口：现有 `bug_report.yml` 是维护者内部三分类表单（`Owner` / `Priority` / `Class` 必填），社区测试者不了解这些字段、也不会填。仅靠 README 上的「等待社区支持」字样无法触达潜在测试者。

## Decision

- 新增 `.github/ISSUE_TEMPLATE/test_feedback.yml`（社区测试反馈模板）：平台/架构、包格式、版本为下拉/必填，加「现象 + 日志截图」，去掉内部字段；中英双语，30 秒可填完。与内部 `bug_report.yml` 并存，chooser 分开展示。
- 在 `deepseek-ai/deepseek-harness`「Show Your Plugins!」分类发中英双语展示帖（一次性动作）：欢迎试用 + 明确征集 `macOS Intel (x64)` / `Windows` 真机手测；反馈统一引导到本仓库 Issues（用 `test_feedback` 模板）。
- 发布帖的界面截图（两张 PNG）由作者在发帖时拖入讨论编辑器；本仓库不改动（README 截图属后续可选事项）。

## Alternatives considered

- **直接改造现有 `bug_report.yml` 去掉内部字段**：落败——内部三分类表单是维护者分诊工作流（Owner/Priority/Class 对应 ADR class 体系），混用会破坏内部分类；独立模板可在 chooser 分开呈现。
- **只用 Discussion 评论区收集反馈**：落败——评论散落、无结构化字段，无法按 OS/架构/版本追溯证据；Issue 模板把字段固化，反馈可结构化沉淀。
- **不发帖、只等自然流量**：落败——与「等待社区支持」决策自洽，但无主动触达动作，mac x64 / win 反馈速度不可控；发帖成本低、可随时编辑。

## Consequences

- 收益：社区测试者有可一键填完的表单；mac x64 / win 反馈成为结构化证据来源，与 `community-targeted-testing` ADR 的「据证据决定是否调整打包」衔接；发帖同步带来试用、star 与潜在贡献者曝光。
- 代价：新增一个模板文件需维护；社区反馈质量仍不可控（模板只降门槛不保质量）；「Show Your Plugins!」属展示类分类，引流有限、不保证响应。
- 后续：若社区反馈暴露打包问题 → 据证据拉回内部处理；若模板被证明低效（如无人使用/字段无效）→ 按证据合并进统一表单或关闭。
