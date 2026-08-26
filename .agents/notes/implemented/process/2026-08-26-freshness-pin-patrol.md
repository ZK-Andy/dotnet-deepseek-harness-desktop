# Agent Note: freshness-pin-patrol

Status: implemented

## Problem

随包依赖以精确钉版发布，但「钉版已落后」没有任何检测面，全靠人肉发现——实证：dshmarket 钉 1.15.0 时 npm latest 已 1.26.0（落后 11 个 minor），直到手动排查才暴露。版本感知升级（`BundledPluginCatalog`）解决了「bump 之后如何送达用户」（B 线），没解决「谁来告诉我们该 bump 了」（A 线）。痛点放大器：同一份版本号抄写在多处（脚本默认值 + 三平台 workflow env + C# 底线常量 + docs 散文），人肉巡检繁琐且易漏，半途 bump（只改一半副本）也无人拦截。

## Decision

1. **单一检测器 `scripts/check-pin-freshness.sh`**：
   - **提取我方钉版**：正典取 `scripts/bundle-runtime-ci.sh` 默认值（`NODE_VERSION` / `DSH_VERSION` / dshmarket）；同时核对 `.github/workflows/package-*.yml` env 与 `RuntimeVersionGate.MinimumVersion` 常量的副本一致性；docs 散文不入机检（升级时人工同步）。
   - **查上游**：npm registry dist-tags ×2（`@deepseek-ai/dsh` 取 latest/next 较大者——rc 闭包族历史在 next 线；`dshmarket` 取 latest）+ nodejs.org dist index 的现役 LTS 线（`lts≠false` 的最大版本，不追 Current 线）。
   - **退出码分型**：0=无漂移 / 1=上游漂移 / 2=内部副本不一致 / 3=探测失败。
   - **可离线自测**：数据源路径经环境变量可覆写，`--offline-fixtures` 注入固定响应；`--self-test` 跑内置夹具断言三种结局。
2. **定时巡检 `.github/workflows/freshness.yml`**：每周 cron + 手动 dispatch；漂移 → 以固定标题开/更新 GitHub issue（先查重、追平自动关并留言）；内部不一致或探测失败让 run 变红（可见信号）；报告存 7 天 artifact。issue 只做感知，bump 拍板权留人（dsh rc 升级需连带行为复验；正文附 minimumReleaseAge 政策提示）。
3. **发版注解（warn-only）**：`release-preflight.sh` 尾部 best-effort 调用检测器 `--annotate` 模式，漂移打 `::warning` 到 run log；网络失败一律放行只留日志——preflight 是阻断闸门，注解绝不改变其放行语义。价值：即使没人看 issue，每个发版时刻（当事人必然在场）都有第二触点。

## Alternatives considered

- **构建时浮动 latest**（已否决）：破坏可重复构建（同 tag 重跑产出不同闭包）；绕过人工判断的惊喜升级（dsh 预览质量，rc 间破坏性变更真实存在）；与 pnpm `minimumReleaseAge` 供应链政策打架（太新被拦、构建时灵时不灵）。
- **Dependabot/renovate 自动 PR**（落败）：机器人解析不了我们的钉法——版本在 shell 默认值/env/C# 常量而非 package.json 清单；且自动 bump 违背「bump 是决策」原则。
- **只做 preflight 注解不做定时巡检**（落败）：发版频率低且不定，漂移可能在两次发版间沉默数月；巡检保证恒定感知节奏。
- **本轮顺手把钉版收敛成单一生成源**（暂缓后已单独落地）：收益真实但改动横跨打包链与缓存 key，风险大于 A 线本职故未随本批；后续已单独实施——workflow env 手抄取消、`--print` 从脚本默认值解析，见 [pin-source-convergence](2026-08-26-pin-source-convergence.md)。

## Consequences

- 巡检 issue 可能被忽视——preflight 注解是发版时刻的兜底触点，但两者都不强制动作；长期漏看属流程风险非机制缺陷。
- 探测失败（registry 不可达）在巡检侧表现为红 run、在 preflight 侧静默放行——两侧语义刻意不对称：前者要可见性，后者不能误伤发布。
- Node 判定基准是现役 LTS 线而非 latest-any（v26 Current 不触发漂移）；升级仍由人拍板后同步全部副本位置。

## Related

- [随包插件版本感知清单](../feature/2026-08-25-bundled-plugin-version-aware-catalog.md)：B 线——本决策只补 A 线检测面，送达机制已在彼处落地。
- [产物验证链](2026-08-24-artifact-verification-chain.md)：release-preflight.sh 的出处与阻断语义边界。
